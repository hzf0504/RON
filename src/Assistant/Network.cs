using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RonWg;
public record Adapter(int Index, string Name, string Description, string Status, string[] IPv4, int Mtu, bool IsWireGuard)
{
    public override string ToString() => $"{Name} / {string.Join(", ", IPv4)} / #{Index}";
}
public static class Network
{
    [DllImport("iphlpapi.dll")] static extern uint GetBestInterfaceEx(byte[] destination, out uint index);
    // 喵服客户端 nya-client 内置 wireguard-nt，网卡名默认为 "nya"，描述仍为 "WireGuard Tunnel"。
    public const string NyaClientProcess = "nya-client", NyaAdapterName = "nya";
    static bool IsRunning(string name) { var p = Process.GetProcessesByName(name); foreach (var x in p) x.Dispose(); return p.Length > 0; }
    public static string[] TunnelClients() => new[] { (NyaClientProcess, "喵服客户端 nya-client"), ("wireguard", "WireGuard 官方客户端") }
        .Where(x => IsRunning(x.Item1)).Select(x => x.Item2).ToArray();
    public static Adapter[] Adapters() => NetworkInterface.GetAllNetworkInterfaces().Select(n =>
    {
        var p = n.GetIPProperties(); var ip = p.GetIPv4Properties();
        return new Adapter(ip?.Index ?? 0, n.Name, n.Description, n.OperationalStatus.ToString(),
            p.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork).Select(x => x.Address.ToString()).ToArray(),
            ip?.Mtu ?? 0, n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase));
    }).ToArray();
    public static uint Route(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("当前实验版仅支持 WG IPv4。");
        byte[] address = new byte[16]; address[0] = 2; destination.GetAddressBytes().CopyTo(address, 4);
        var code = GetBestInterfaceEx(address, out var index);
        if (code != 0) throw new InvalidOperationException($"无法查询 {destination} 路由，Windows 错误 {code}。");
        return index;
    }
    public static IPAddress[] Peers(string value)
    {
        var items = value.Split(new[] { ',', ';', ' ', '\r', '\n', '，', '；' }, StringSplitOptions.RemoveEmptyEntries);
        var result = items.Select(x => IPAddress.TryParse(x, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224 && !ip.Equals(IPAddress.Any)
                ? ip : throw new ArgumentException($"无效的朋友 WG IPv4：{x}")).Distinct().ToArray();
        if (result.Length is < 1 or > 4) throw new ArgumentException("请填写 1～4 个朋友的 WG IPv4，以逗号分隔。");
        return result;
    }
    public static void Validate(Adapter selected, IPAddress[] peers)
    {
        var live = Adapters().SingleOrDefault(x => x.Index == selected.Index);
        if (live is null || !live.IsWireGuard || live.Status != "Up" || live.IPv4.Length == 0 ||
            !live.IPv4.SequenceEqual(selected.IPv4)) throw new InvalidOperationException("选中的 WG 网卡已断开或地址变化（喵服客户端断开重连会重建网卡），请在客户端重新连接后刷新。");
        foreach (var peer in peers)
        {
            if (live.IPv4.Contains(peer.ToString())) throw new ArgumentException("朋友地址不能是本机地址。");
            if (Route(peer) != selected.Index) throw new InvalidOperationException($"到 {peer} 的系统路由未指向所选 WG。请确认该地址是朋友在喵服客户端里显示的虚拟 IP，且双方连在同一网络；助手不会添加路由。");
        }
    }
    public static string SaveSnapshot(string folder, IPAddress[]? peers = null)
    {
        Directory.CreateDirectory(folder);
        var data = new { time = DateTimeOffset.Now, os = Environment.OSVersion.ToString(), tunnelClients = TunnelClients(), adapters = Adapters(),
            routes = (peers ?? []).Select(p => new { peer = p.ToString(), predictedInterface = Route(p) }).ToArray() };
        var path = Path.Combine(folder, "network.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, Json.Options)); return path;
    }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
