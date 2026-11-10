using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RonWg;
public static class ProbeWire
{
    public const int Port = 39741;
    public static byte[] Key(string hex)
    {
        byte[] key;
        try { key = Convert.FromHexString(hex.Trim()); } catch { throw new ArgumentException("诊断口令必须是 64 位十六进制，两边填写相同口令。"); }
        if (key.Length != 32) throw new ArgumentException("诊断口令必须是 64 位十六进制。");
        return key;
    }
    public static byte[] Create(byte[] key, Guid nonce, long sequence, bool response, int size = 512)
    {
        if (size is < 96 or > 1200) throw new ArgumentOutOfRangeException(nameof(size));
        var packet = new byte[size]; "RWG1"u8.CopyTo(packet); packet[4]=(byte)(response?2:1);
        nonce.TryWriteBytes(packet.AsSpan(8,16)); BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(24,8), sequence);
        HMACSHA256.HashData(key,packet.AsSpan(0,size-32)).CopyTo(packet.AsSpan(size-32)); return packet;
    }
    public static bool Read(byte[] key, byte[] packet, out Guid nonce, out long seq, out bool response)
    {
        nonce=default;seq=0;response=false;
        if(packet.Length is <96 or >1200 || !packet.AsSpan(0,4).SequenceEqual("RWG1"u8) || packet[4] is not (1 or 2)) return false;
        var mac=HMACSHA256.HashData(key,packet.AsSpan(0,packet.Length-32));
        if(!CryptographicOperations.FixedTimeEquals(mac,packet.AsSpan(packet.Length-32))) return false;
        nonce=new Guid(packet.AsSpan(8,16));seq=BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(24,8));response=packet[4]==2;return true;
    }
}
public record ProbeResult(string Peer,int Sent,int Received,double LossPercent,double? P50Ms,double? P95Ms,double? JitterP95Ms,int PacketBytes,string Kind="helper_udp_only");
public sealed class UdpProbe : IDisposable
{
    readonly UdpClient udp;
    readonly byte[] key;
    readonly HashSet<IPAddress> peers;
    readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<(Guid,long,IPAddress),TaskCompletionSource<double>> pending=new();
    readonly ConcurrentDictionary<(Guid,long,IPAddress),long> starts=new();
    readonly Dictionary<IPAddress,(long second,int count)> rate=new();
    readonly Action? validate;
    readonly Task receiver;
    public UdpProbe(IPAddress local, IEnumerable<IPAddress> allowedPeers, byte[] key, Action? validate = null, int port=ProbeWire.Port)
    {
        this.key=key.ToArray();peers=allowedPeers.ToHashSet();this.validate=validate;
        udp=new UdpClient(AddressFamily.InterNetwork); udp.Client.ExclusiveAddressUse=true;
        try { udp.Client.Bind(new IPEndPoint(local,port)); }
        catch { udp.Dispose();throw; }
        receiver=Receive();
    }
    async Task Receive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var p=await udp.ReceiveAsync(stop.Token);
                if(!peers.Contains(p.RemoteEndPoint.Address) || !ProbeWire.Read(key,p.Buffer,out var nonce,out var seq,out var response)) continue;
                if(response)
                {
                    var id=(nonce,seq,p.RemoteEndPoint.Address);
                    if(pending.TryRemove(id,out var completion) && starts.TryRemove(id,out var start)) completion.TrySetResult(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                else
                {
                    var second=Environment.TickCount64/1000; rate.TryGetValue(p.RemoteEndPoint.Address,out var bucket);
                    bucket=bucket.second==second?(second,bucket.count+1):(second,1); rate[p.RemoteEndPoint.Address]=bucket;
                    if(bucket.count>25) continue;
                    // A fixed-size authenticated echo, never an amplification relay.
                    var reply=ProbeWire.Create(key,nonce,seq,true,p.Buffer.Length);
                    await udp.SendAsync(reply,p.RemoteEndPoint,stop.Token);
                }
            }
        }
        catch(OperationCanceledException) { }
        catch(ObjectDisposedException) { }
        catch(SocketException) when(stop.IsCancellationRequested) { }
    }
    public async Task<ProbeResult> Measure(IPAddress peer,int count=100,int intervalMs=200,int size=512,int port=ProbeWire.Port,CancellationToken cancellation=default)
    {
        if(!peers.Contains(peer)) throw new ArgumentException("对端不在本次朋友列表中。");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stop.Token);
        var token=linked.Token; var nonce=Guid.NewGuid(); var items=new List<TaskCompletionSource<double>>();
        try
        {
            for(int i=0;i<count;i++)
            {
                token.ThrowIfCancellationRequested(); validate?.Invoke();
                if(receiver.IsFaulted) await receiver;
                var t=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously); var id=(nonce,(long)i,peer);
                items.Add(t); pending[id]=t; starts[id]=Stopwatch.GetTimestamp();
                await udp.SendAsync(ProbeWire.Create(key,nonce,i,false,size),new IPEndPoint(peer,port),token);
                await Task.Delay(intervalMs,token);
            }
            await Task.WhenAny(Task.WhenAll(items.Select(x=>x.Task)),Task.Delay(2000,token));
            token.ThrowIfCancellationRequested();
            var rtt=items.Where(t=>t.Task.IsCompletedSuccessfully).Select(t=>t.Task.Result).ToArray();
            var jitter=rtt.Zip(rtt.Skip(1),(a,b)=>Math.Abs(a-b)).ToArray();
            return new(peer.ToString(),count,rtt.Length,100.0*(count-rtt.Length)/count,Percentile(rtt,.5),Percentile(rtt,.95),Percentile(jitter,.95),size);
        }
        finally
        {
            for(int i=0;i<count;i++) {pending.TryRemove((nonce,i,peer),out _); starts.TryRemove((nonce,i,peer),out _);}
        }
    }
    public static double? Percentile(double[] values,double quantile)
    {
        if(values.Length==0)return null;var sorted=values.Order().ToArray();return sorted[Math.Clamp((int)Math.Ceiling(quantile*sorted.Length)-1,0,sorted.Length-1)];
    }
    public void Dispose(){stop.Cancel();udp.Dispose();}
}
