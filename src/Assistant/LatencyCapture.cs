using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace RonWg;

// Derived from this build's reflection metadata and UpdatePing virtual tables.
// The file hash gates these offsets before a module is loaded into the game.
public static class LatencyProfile
{
    public const string GameSha256 = "FF445331E7144A2DC79EC50C2E6FD42F2A2E7B49AE7B03AE1A6076DCFC769262";
    public const uint MethodRva = 0x331c140;
    public static readonly uint[] Slots = [0x6da26d8, 0x743b5f8];
    public static readonly byte[] Signature = Convert.FromHexString("40534883EC500F29742440488BD90F28F10F297C2430F30F");

    public static void Verify(string gameRoot, Process process)
    {
        var path = Path.GetFullPath(Path.Combine(gameRoot, Compatibility.GameExe));
        if (!string.Equals(Compatibility.Hash(path), GameSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此游戏构建的 RTT 数据布局尚未适配。请保留本次错误记录。");
        process.Refresh();
        if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("游戏进程与所选游戏目录不一致。");
    }
}

public sealed class LatencyCapture : IDisposable
{
    public static void StopExisting(int pid)
    {
        using var map = MemoryMappedFile.OpenExisting($"Local\\RonLatency-{pid}");
        using var control = map.CreateViewAccessor();
        if (control.ReadUInt32(0) == 0x4c4e4f52u && control.ReadUInt32(4) == 1u) control.Write(16, 1);
    }
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    bool disposed;
    public int State => view.ReadInt32(20);

    public LatencyCapture(Process process, string folder)
    {
        var path = Encoding.Unicode.GetBytes(Path.GetFullPath(folder) + '\0');
        if (path.Length > 2048) throw new IOException("延迟记录目录路径过长。");
        map = MemoryMappedFile.CreateNew($"Local\\RonLatency-{process.Id}", 4096);
        view = map.CreateViewAccessor();
        try
        {
            view.Write(0, 0x4c4e4f52u);
            view.Write(4, 1u);
            view.Write(24, LatencyProfile.MethodRva);
            view.Write(28, (uint)LatencyProfile.Slots.Length);
            view.WriteArray(32, LatencyProfile.Slots, 0, LatencyProfile.Slots.Length);
            view.Write(64, 0x158u); // AActor::Role
            view.Write(68, 0x2a4u); // APlayerState::PlayerId
            view.Write(72, 0x338u); // APlayerState::PlayerNamePrivate
            view.Write(76, 0xcu);   // UObject::InternalIndex
            view.Write(80, 0xf22d401fu);
            view.Write(84, 0x96e8000u);
            view.WriteArray(88, LatencyProfile.Signature, 0, LatencyProfile.Signature.Length);
            view.WriteArray(112, path, 0, path.Length);
            Heartbeat();
            ProbeSession.Inject(process, Path.Combine(AppContext.BaseDirectory, "RonLatency.dll"), "RonLatencyStart", reuseLoaded: true);
        }
        catch { Dispose(); throw; }
    }

    public void Heartbeat() => view.Write(8, Native.GetTickCount64());
    public void RequestStop() { if (!disposed) { view.Write(16, 1); view.Flush(); } }
    public void Dispose()
    {
        if (disposed) return;
        RequestStop();
        disposed = true;
        view.Dispose();
        map.Dispose();
    }
}
