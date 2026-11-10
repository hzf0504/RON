using RonWg;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

var root=Path.GetFullPath(args[0]);var folder=Path.Combine(root,"tests","results","logging-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(folder);int failures=0,checks=0;
void Check(bool value,string name){checks++;Console.WriteLine((value?"PASS ":"FAIL ")+name);if(!value)failures++;}
var session=Path.Combine(folder,"session");
using(var journal=new SessionJournal(session))
{
    journal.Context("same-test-01","房主","移动网络");journal.Marker("切图开始","首次切换","same-test-01","房主");
    journal.Error("attach",new IOException("Cannot read C:\\Users\\Example\\private-folder\\game.exe"));
    journal.Write("message",new{message="peer 10.20.30.40:7777 token=TOP_SECRET",secret="TOP_SECRET"});
}
var events=File.ReadAllLines(Path.Combine(session,"assistant.jsonl")).Select(l=>JsonNode.Parse(l)!).ToArray();
Check(events.All(e=>e["utc"]!=null&&e["tickMs"]!=null&&e["sequence"]!=null&&e["sessionId"]!=null),"every helper event has UTC, monotonic clock, sequence and session ID");
Check(events.Select(e=>e["sequence"]!.GetValue<long>()).SequenceEqual(Enumerable.Range(1,events.Length).Select(x=>(long)x)),"event sequence ordered");
Check(File.ReadAllText(Path.Combine(session,"markers.jsonl")).Contains("same-test-01"),"test marker carries shared run ID");
var rotated=Path.Combine(folder,"rotation");using(var journal=new SessionJournal(rotated,512))for(int i=0;i<50;i++)journal.Write("stress",new{index=i,note=new string('a',120)});
Check(Directory.GetFiles(rotated,"assistant*.jsonl").Length==4,"rotation bounded at four segments");
var last=JsonNode.Parse(File.ReadAllLines(Path.Combine(rotated,"assistant.jsonl")).Last())!;Check(last["data"]!["index"]!.GetValue<int>()==49,"rotation preserves newest event");
File.WriteAllText(Path.Combine(session,"network.json"),"{\"peer\":\"10.20.30.40\",\"ipv4\":[\"10.20.30.40\"],\"token\":\"TOP_SECRET\",\"privateKey\":\"TOP_SECRET\",\"name\":\"Custom private adapter\"}");
File.WriteAllText(Path.Combine(session,"native.jsonl"),"{\"event\":\"eos_loaded\",\"version\":\"1.19.1.2-53289219\"}\n{\"event\":\"eos_udp\",\"remote\":\"10.20.30.40:7777\"}\n{partial");
File.WriteAllText(Path.Combine(session,"wg-secret.conf"),"TOP_SECRET");File.WriteAllText(Path.Combine(session,"raw.pcapng"),"RAW_PACKET_SECRET");File.WriteAllText(Path.Combine(session,"steam_api64.dll"),"DO_NOT_SHIP");
var archive=DiagnosticBundle.Export(session,Path.Combine(folder,"exports"));
using(var zip=ZipFile.OpenRead(archive))
{
    string Read(string name){using var r=new StreamReader(zip.GetEntry(name)!.Open());return r.ReadToEnd();}
    var combined=string.Join("\n",zip.Entries.Select(e=>Read(e.FullName)));
    Check(!combined.Contains("TOP_SECRET")&&!combined.Contains("RAW_PACKET_SECRET")&&!combined.Contains("DO_NOT_SHIP"),"secrets, captures and binaries excluded");
    Check(!combined.Contains("10.20.30.40")&&!combined.Contains("private-folder")&&!combined.Contains("Custom private adapter"),"IP, absolute path and custom adapter name redacted");
    var network=JsonNode.Parse(Read("network.json"))!;var pseudonym=network["peer"]!.GetValue<string>();
    Check(network["ipv4"]![0]!.GetValue<string>()==pseudonym&&Read("native.jsonl").Contains(pseudonym+":7777"),"address pseudonyms consistent across files, port preserved");
    Check(Read("native.jsonl").Contains("1.19.1.2-53289219"),"EOS version is not mistaken for an IPv4 address");
    var manifest=JsonNode.Parse(Read("bundle-manifest.json"))!;
    Check(manifest["files"]!.AsArray().Any(f=>f!["name"]!.GetValue<string>()=="native.jsonl"&&f["omittedMalformedOrPartialLines"]!.GetValue<int>()==1),"truncated line omission explicitly counted");
    Check(zip.Entries.All(e=>!e.FullName.EndsWith(".dll")&&!e.FullName.EndsWith(".conf")&&!e.FullName.EndsWith(".pcapng")),"archive file allowlist enforced");
    foreach(var f in manifest["files"]!.AsArray())
    {using var s=zip.GetEntry(f!["name"]!.GetValue<string>())!.Open();Check(Convert.ToHexString(SHA256.HashData(s))==f["sha256"]!.GetValue<string>(),"exported file hash verified: "+f["name"]!.GetValue<string>());}
}
Check(File.ReadAllText(Path.Combine(session,"network.json")).Contains("10.20.30.40"),"export leaves local original logs unchanged");
var corrupt=Path.Combine(folder,"invalid.jsonl");File.WriteAllText(corrupt,"{\"event\":\"sample\"}\n{broken");
Check(EvidenceReader.Read(corrupt,[],0).InvalidLines==2,"damaged log lines reported without crashing evidence reader");
EvidenceReader.Report(session,[],0);Check(File.ReadAllText(Path.Combine(session,"report.md")).Contains("尚未")||File.ReadAllText(Path.Combine(session,"report.md")).Contains("未确认"),"report does not invent a successful game path");
LatencyTests.Run(folder,args.Length>1?args[1]:null,Check);
Console.WriteLine($"Checks: {checks}; failures: {failures}; local files only, no game launch or network tests.");
File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new{checks,failures,networkTests=false,gameLaunched=false,time=DateTimeOffset.Now},Json.Options));return failures==0?0:1;
