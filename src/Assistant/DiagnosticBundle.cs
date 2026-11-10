using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RonWg;
public sealed class LogRedactor
{
    readonly byte[] salt=RandomNumberGenerator.GetBytes(32);
    static readonly Regex V4=new(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])",RegexOptions.Compiled);
    static readonly Regex V6=new(@"(?<![\w])(?:[a-fA-F0-9]{0,4}:){2,}[a-fA-F0-9:]{0,20}(?![\w])",RegexOptions.Compiled);
    static readonly Regex PathPattern=new(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n\""<>|]*",RegexOptions.Compiled);
    static readonly Regex Secrets=new(@"(?i)\b(privatekey|presharedkey|token|password|secret|authorization)\s*[:=]\s*[^\s,;]+",RegexOptions.Compiled);
    public string Label(string value)=>"ip-"+Convert.ToHexString(HMACSHA256.HashData(salt,Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..10];
    public string Text(string value,bool addresses=true)
    {
        value=Secrets.Replace(value,"$1=[omitted]");
        value=PathPattern.Replace(value,"[local-path]");
        var user=Environment.UserName;if(user.Length>=3)value=value.Replace(user,"[local-user]",StringComparison.OrdinalIgnoreCase);
        if(!addresses)return value;
        value=V4.Replace(value,m=>IPAddress.TryParse(m.Value,out var ip)?Label(ip.ToString()):m.Value);
        return V6.Replace(value,m=>IPAddress.TryParse(m.Value,out var ip)?Label(ip.ToString()):m.Value);
    }
    public JsonNode? Sanitize(JsonNode? node,string field="")
    {
        if(node is JsonObject obj)
        {
            var result=new JsonObject();foreach(var p in obj)
            {
                if(Regex.IsMatch(p.Key,@"(?i)(private.?key|preshared.?key|password|secret|token|credential|authorization|^key$)"))result[p.Key]="[omitted]";
                else result[p.Key]=Sanitize(p.Value,p.Key);
            }
            return result;
        }
        if(node is JsonArray array)return new JsonArray(array.Select(x=>Sanitize(x,field)).ToArray());
        if(node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            if(field.Equals("name",StringComparison.OrdinalIgnoreCase)||field.Equals("adapterName",StringComparison.OrdinalIgnoreCase))return "[adapter-name]";
            if(field.Equals("playerLabel",StringComparison.OrdinalIgnoreCase))return "player-"+Label(s)[3..];
            return Text(s,!field.Contains("version",StringComparison.OrdinalIgnoreCase)&&field!="os");
        }
        return node?.DeepClone();
    }
}
public static class DiagnosticBundle
{
    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) {
        "session.json","network.json","game-settings.json","files-before.json","files-after.json","udp-probe.json",
        "report.md","native.jsonl","markers.jsonl","assistant.jsonl","assistant.1.jsonl","assistant.2.jsonl","assistant.3.jsonl","test-context.json","latency-session.json"
    };
    public static string Export(string sourceFolder,string outputFolder)
    {
        sourceFolder=Path.GetFullPath(sourceFolder);outputFolder=Path.GetFullPath(outputFolder);
        if(!Directory.Exists(sourceFolder))throw new DirectoryNotFoundException("没有可导出的会话目录。");
        Directory.CreateDirectory(outputFolder);
        var archivePath=Path.Combine(outputFolder,$"RONWG-logs-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var redactor=new LogRedactor();var entries=new List<object>();long total=0;
        try
        {
            using var zip=new ZipArchive(new FileStream(archivePath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None),ZipArchiveMode.Create);
            foreach(var path in Directory.GetFiles(sourceFolder).Order())
            {
                var name=Path.GetFileName(path);if(!Allowed.Contains(name) && !LatencyLogReader.IsSegment(name))continue;
                var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                // Freeze a byte snapshot: active native logging cannot keep export reading indefinitely.
                var length=input.Length;total+=length;
                if(length>70*1024*1024 || total>96*1024*1024)throw new IOException("本次日志超过诊断包上限 96 MB，请分次保存较短会话。");
                var bytes=new byte[checked((int)length)];input.ReadExactly(bytes);
                var content=Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');int dropped=0;
                if(name.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase))
                {
                    var builder=new StringBuilder();foreach(var line in content.Split('\n'))
                    {
                        if(string.IsNullOrWhiteSpace(line))continue;
                        try{builder.AppendLine(redactor.Sanitize(JsonNode.Parse(line))!.ToJsonString());}
                        catch(JsonException){dropped++;}
                    }
                    content=builder.ToString();
                }
                else if(name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
                    content=redactor.Sanitize(JsonNode.Parse(content))!.ToJsonString(Json.Options);
                else content=redactor.Text(content);
                var safe=Encoding.UTF8.GetBytes(content);Write(zip,name,safe);
                entries.Add(new{name,bytes=safe.Length,sha256=Convert.ToHexString(SHA256.HashData(safe)),omittedMalformedOrPartialLines=dropped});
            }
            if(entries.Count==0)throw new IOException("此目录没有可导出的助手日志，请选择单次会话文件夹。");
            Write(zip,"bundle-manifest.json",Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                schema=SessionJournal.Schema,assistantVersion=SessionJournal.Version,exportedUtc=DateTimeOffset.UtcNow,
                addresses="pseudonymized consistently inside this ZIP; labels differ between ZIPs",localPaths="redacted",
                liveSnapshot=true,files=entries,
                exclusions=new[]{"WireGuard/nya-client configuration, keys and tokens","game saves and raw game logs","packet captures, binaries, memory dumps","other test sessions"}
            },Json.Options)));
            Write(zip,"READ-ME.txt",Encoding.UTF8.GetBytes("这是助手诊断日志包，不是适配成功证明。\n联机诊断先看 report.md、markers.jsonl，再对照 assistant.jsonl 和 native.jsonl。\n延迟记录先看 latency-session.json 和 latency-*.jsonl，单位 ms，playerLabel 为包内一致的玩家标签。\nUTC 用于双端对时；tickMs 仅能在同一台电脑内对齐。\nIP 标签在本 ZIP 内一致，跨 ZIP 不一致；用双方填写的测试编号、角色及接口序号关联。\n没有打包原始抓包、游戏文件、存档、喵服配置或密钥。\n部分尾行若尚未写完会省略，数量见 bundle-manifest.json。\n"));
            return archivePath;
        }
        catch {if(File.Exists(archivePath))File.Delete(archivePath);throw;}
    }
    static void Write(ZipArchive zip,string name,byte[] bytes){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using var s=entry.Open();s.Write(bytes);}
}
