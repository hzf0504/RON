using RonWg;
using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;

var root=Path.GetFullPath(args[0]);var results=Path.Combine(root,"tests","results");Directory.CreateDirectory(results);int failures=0;
void Check(bool value,string name){Console.WriteLine((value?"PASS ":"FAIL ")+name);if(!value)failures++;}
void Throws(Action action,string name){try{action();Check(false,name);}catch(ArgumentException){Check(true,name);}}
var key=RandomNumberGenerator.GetBytes(32);var nonce=Guid.NewGuid();var packet=ProbeWire.Create(key,nonce,42,false,1152);
Check(ProbeWire.Read(key,packet,out var id,out var seq,out var response)&&id==nonce&&seq==42&&!response,"authenticated protocol roundtrip");
packet[20]^=1;Check(!ProbeWire.Read(key,packet,out _,out _,out _),"tampering rejected");packet[20]^=1;
Check(!ProbeWire.Read(RandomNumberGenerator.GetBytes(32),packet,out _,out _,out _),"wrong pairing key rejected");
Check(!ProbeWire.Read(key,new byte[30],out _,out _,out _),"truncated packet rejected");
Throws(()=>ProbeWire.Key("x"),"invalid key rejected");Throws(()=>Network.Peers("127.0.0.1"),"loopback refused as friend WG address");
Throws(()=>Network.Peers("224.0.0.1"),"multicast refused");Throws(()=>Network.Peers("1.2.3.4,2.3.4.5,3.4.5.6,4.5.6.7,5.6.7.8"),"more than four peers refused");
Check(UdpProbe.Percentile([5,2,10,1],.5)==2 && UdpProbe.Percentile([],.95)==null,"percentiles including empty results");
using(var a=new UdpProbe(IPAddress.Loopback,[IPAddress.Loopback],key,port:49741))
using(var b=new UdpProbe(IPAddress.Loopback,[IPAddress.Loopback],key,port:49742))
{
    var r=await a.Measure(IPAddress.Loopback,count:10,intervalMs:50,size:1152,port:49742);
    Check(r.Received==10&&r.LossPercent==0&&r.Kind=="helper_udp_only","real UDP echo with authentication, 1152 byte datagrams");
    using var c=new UdpProbe(IPAddress.Loopback,[IPAddress.Loopback],RandomNumberGenerator.GetBytes(32),port:49743);
    r=await c.Measure(IPAddress.Loopback,count:2,intervalMs:50,port:49742);
    Check(r.Received==0&&r.LossPercent==100&&r.P50Ms==null,"wrong key produces honest 100 percent timeouts");
}
var path=Path.Combine(results,"classification.jsonl");
File.WriteAllText(path,"{\"event\":\"sample\",\"eosSendAccepted\":8,\"eosReceivePackets\":9,\"adapterFiltered\":1,\"adapterCalls\":2,\"adapterMissing\":0,\"hooks\":7}\n"+
    "{\"event\":\"eos_udp\",\"predictedInterface\":10,\"remote\":\"10.2.3.40:9000\",\"direction\":\"tx\",\"packets\":300}\n"+
    "{\"event\":\"eos_udp\",\"predictedInterface\":10,\"remote\":\"10.2.3.4:9000\",\"direction\":\"tx\",\"packets\":3}\n{" );
var evidence=EvidenceReader.Read(path,["10.2.3.4"],10);
Check(evidence.PeerTx==3&&evidence.PeerRx==0&&evidence.Sends==8,"evidence handles truncated tail and exact peer address");
Check(!evidence.Verdict.Contains("成功"),"API and routing prediction alone never claim adaptation success");
var injectDir=Path.Combine(results,"remote");Directory.CreateDirectory(injectDir);
File.Copy(Path.Combine(root,"build","native","RonWgProbe.dll"),Path.Combine(AppContext.BaseDirectory,"RonWgProbe.dll"),true);
using(var p=Process.Start(new ProcessStartInfo(Path.Combine(root,"build","native-tests","RemoteHarness.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})!)
{
    await Task.Delay(300);
    using(var session=new ProbeSession(p,injectDir,false,0))
    {
        for(int i=0;i<7;i++){session.Heartbeat();await Task.Delay(400);}
    }
    await p.WaitForExitAsync();Console.Write(await p.StandardOutput.ReadToEndAsync());Check(p.ExitCode==0,"remote LoadLibrary and export-RVA startup preserve harness behavior");
    evidence=EvidenceReader.Read(Path.Combine(injectDir,"native.jsonl"),[],0);
    Check(evidence.Sends==1&&evidence.Received==1&&evidence.Restored,"external helper records game API calls and restores all hooks");
}
Console.WriteLine($"Failures: {failures}");return failures==0?0:1;
