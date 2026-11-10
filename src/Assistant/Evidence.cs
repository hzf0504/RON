using System.Text;
using System.Text.Json;

namespace RonWg;
public record Evidence(long Sends, long Received, long Filtered, long AdapterCalls, long Missing, int Hooks,
    long PeerTx, long PeerRx, bool Restored, string Verdict, int InvalidLines=0, long Polls=0);
public static class EvidenceReader
{
    public static string ReportFromMetadata(string folder)
    {
        string[] peers=[];int index=0;var path=Path.Combine(folder,"session.json");
        if(File.Exists(path))
        {
            try{using var d=JsonDocument.Parse(File.ReadAllText(path));var e=d.RootElement;if(e.TryGetProperty("peers",out var p))peers=p.EnumerateArray().Select(x=>x.GetString()??"").ToArray();if(e.TryGetProperty("interfaceIndex",out var i))index=i.GetInt32();}
            catch(Exception ex)when(ex is JsonException or InvalidOperationException or FormatException){}
        }
        return Report(folder,peers,index);
    }
    public static Evidence Read(string path, string[] peers, int wgIndex)
    {
        long sends=0, received=0, filtered=0, calls=0, missing=0, tx=0, rx=0, polls=0; int hooks=0,invalid=0; bool restored=false;
        if (!File.Exists(path)) return new(0,0,0,0,0,0,0,0,false,"等待进程内模块日志。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
                var type = e.GetProperty("event").GetString();
                if (type == "start") { sends=received=filtered=calls=missing=tx=rx=polls=0; hooks=0; restored=false; }
                else if (type == "sample")
                {
                    sends=e.GetProperty("eosSendAccepted").GetInt64(); received=e.GetProperty("eosReceivePackets").GetInt64();
                    filtered=e.GetProperty("adapterFiltered").GetInt64(); calls=e.GetProperty("adapterCalls").GetInt64();
                    missing=e.GetProperty("adapterMissing").GetInt64(); hooks=e.GetProperty("hooks").GetInt32();
                    if(e.TryGetProperty("eosPolls",out var value)) polls=value.GetInt64();
                }
                else if (type == "eos_udp" && wgIndex > 0 && e.GetProperty("predictedInterface").GetInt32() == wgIndex)
                {
                    var remote=e.GetProperty("remote").GetString()!;
                    if (peers.Any(p => remote.StartsWith(p+":", StringComparison.Ordinal)))
                    {
                        if (e.GetProperty("direction").GetString()=="tx") tx+=e.GetProperty("packets").GetInt64();
                        else rx+=e.GetProperty("packets").GetInt64();
                    }
                }
                else if(type=="restored") restored=e.GetProperty("slots").GetInt32()==e.GetProperty("total").GetInt32();
            }
            catch (Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { invalid++; }
        }
        var verdict = hooks == 0 ? "尚未确认 hook 生效。" : sends+received == 0 ? (polls>0 ? $"EOS P2P 已轮询 {polls} 次；尚无远端对局收发证据。" : "已观测进程；尚无 EOS P2P 对局收发证据。") :
            tx>0 && rx>0 ? "观测到 EOS P2P 调用及朋友 WG 地址双向 UDP；需接口抓包和双端对照确认对局路径。" :
            "已确认 EOS P2P 收发调用，尚未确认朋友 WG 地址的双向传输。";
        if(missing>0) verdict+=" 所选 WG 曾不可用，候选筛选未成功；需要退出本局并检查隧道。";
        if(restored) verdict+=" 导入表已恢复，完整恢复请重启游戏。";
        return new(sends,received,filtered,calls,missing,hooks,tx,rx,restored,verdict,invalid,polls);
    }
    public static string Report(string folder, string[] peers, int index)
    {
        var e=Read(Path.Combine(folder,"native.jsonl"),peers,index);
        var text=$"""
            # RON WG 实验会话记录

            生成时间：{DateTimeOffset.Now:O}

            {e.Verdict}

            - EOS P2P SendPacket 接受次数：{e.Sends}
            - EOS P2P ReceivePacket 成功次数：{e.Received}
            - EOS P2P 接收队列轮询次数：{e.Polls}（轮询不等于收到游戏包）
            - EOS 网卡枚举次数：{e.AdapterCalls}；候选筛选成功：{e.Filtered}；找不到所选接口：{e.Missing}
            - Hook 槽位：{e.Hooks}；恢复确认：{e.Restored}
            - 到指定朋友 WG 地址且预测路由为所选 WG 的 EOS UDP：发送 {e.PeerTx}，接收 {e.PeerRx}
            - 本次读取无法解析的日志行：{e.InvalidLines}（运行中可能有尚未写完的末行）

            ## 关联双端日志

            test-context.json 包含共同测试编号、角色和网络类型；markers.jsonl 包含切图/卡顿等手动时间点。
            assistant.jsonl 记录操作、错误类型、原因和恢复确认；native.jsonl 记录实际进程接口证据。
            使用 UTC 或 utcMs 对齐不同电脑；tickMs 只用于同一台电脑内的精确顺序。请先同步双方 Windows 时间。
            原生日志最多 64 MB，助手日志最多约 16 MB（轮换）。日志丢失/轮换意味着更早信息可能不完整。

            ## 结论边界

            网卡筛选是实验性原生候选适配。它不保证 EOS 选择该候选，也不禁止原有公网/中继回退。
            predictedInterface 来自 GetBestRoute2，不是抓包证据。UDP 计数不包含助手探测流量，但可能含 EOS 探测/握手。
            测试 UDP 成功、P2P API 收发成功、真实对局经过 WG、卡顿改善是不同结论。
            必须结合双端同时间段记录和接口捕获，完成 Steam 邀请、切图、连续对局、断隧道和恢复验收。
            本记录不自动宣布真实对局通过或体验改善。

            ## 原连接恢复

            停止助手后，进程内 hook 恢复。SDK 已缓存的候选和现存连接需要退出并重启游戏才能清除。
            助手不写游戏文件，不改默认路由，不改 WireGuard 配置。只有手动运行防火墙脚本才会添加指定规则。
            files-before.json 与 files-after.json 用于核对原游戏和现有补丁文件未改动。
            """;
        var path=Path.Combine(folder,"report.md"); File.WriteAllText(path,text,Encoding.UTF8); return path;
    }
}
