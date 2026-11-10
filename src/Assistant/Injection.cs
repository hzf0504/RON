using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace RonWg;
internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint VirtualAllocEx(nint p, nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFreeEx(nint p, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteProcessMemory(nint p, nint address, byte[] data, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint CreateRemoteThread(nint p, nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeThread(nint thread, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadLibraryExW(string file, nint reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] internal static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] internal static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetModuleFileNameW(nint module, StringBuilder name, int size);
    [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
}

public sealed class ProbeSession : IDisposable
{
    public static void StopExisting(int pid)
    {
        using var control=MemoryMappedFile.OpenExisting($"Local\\RonWg-{pid}");
        using var access=control.CreateViewAccessor();
        if(access.ReadUInt32(0)!=0x524F4E57u || access.ReadUInt32(4)!=1)throw new InvalidOperationException("控制块不兼容。");
        access.Write(8,0);access.Write(24,1);access.Flush();
    }
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    public int Pid { get; }
    public string Folder { get; }
    public string LogPath => Path.Combine(Folder, "native.jsonl");
    bool disposed;
    public ProbeSession(Process process, string folder, bool candidates, int index)
    {
        Pid = process.Id; Folder = folder;
        Directory.CreateDirectory(folder);
        var bytes = Encoding.Unicode.GetBytes(LogPath + '\0');
        if (bytes.Length > 2048) throw new InvalidOperationException("日志路径过长。");
        map = MemoryMappedFile.CreateNew($"Local\\RonWg-{Pid}", 4096, MemoryMappedFileAccess.ReadWrite);
        view = map.CreateViewAccessor();
        view.Write(0, 0x524F4E57u); view.Write(4, 1u); view.Write(8, candidates ? 2 : 1);
        view.Write(12, index); view.WriteArray(32, bytes, 0, bytes.Length); Heartbeat();
        try { Inject(process, Path.Combine(AppContext.BaseDirectory, "RonWgProbe.dll")); }
        catch { Dispose(); throw; }
    }
    public void Heartbeat() { if (!disposed) view.Write(16, Native.GetTickCount64()); }
    public void Dispose()
    {
        if (disposed) return;
        view.Write(8, 0); view.Write(24, 1); view.Flush(); disposed = true;
        view.Dispose(); map.Dispose();
    }
    static Exception Error(string text) => new Win32Exception(Marshal.GetLastWin32Error(), text);
    static uint RemoteCall(nint process, nint entry, nint arg)
    {
        var thread = Native.CreateRemoteThread(process, 0, 0, entry, arg, 0, out _);
        if (thread == 0) throw Error("无法启动进程内模块。");
        try
        {
            if (Native.WaitForSingleObject(thread, 15000) != 0)
                throw new TimeoutException("模块加载超过 15 秒。请退出游戏后重试；不尝试终止游戏线程。");
            if (!Native.GetExitCodeThread(thread, out var code)) throw Error("无法读取模块启动结果。");
            return code;
        }
        finally { Native.CloseHandle(thread); }
    }
    static nint ResolveRemote(Process target, string moduleName, string export)
    {
        var local = Native.LoadLibraryExW(moduleName, 0, 0);
        if (local == 0) throw Error("无法加载 Windows API 模块。");
        try
        {
            var function = Native.GetProcAddress(local, export);
            if (function == 0 || !Native.GetModuleHandleExW(4 | 2, function, out var owner)) throw Error("无法解析 Windows API。");
            var path = new StringBuilder(1024); Native.GetModuleFileNameW(owner, path, path.Capacity);
            var remote = target.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, Path.GetFileName(path.ToString()), StringComparison.OrdinalIgnoreCase));
            return remote.BaseAddress + (function - owner);
        }
        finally { Native.FreeLibrary(local); }
    }
    internal static void Inject(Process target, string dll, string entryPoint = "RonWgStart", bool reuseLoaded = false)
    {
        dll = Path.GetFullPath(dll);
        if (!File.Exists(dll)) throw new FileNotFoundException("缺少构建产物 RonWgProbe.dll。", dll);
        target.Refresh();
        var loaded = target.Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase));
        if (loaded != null && (!reuseLoaded || !string.Equals(loaded.FileName, dll, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("本次游戏已加载过观测模块。请完整退出游戏再开始新测试。");
        var p = Native.OpenProcess(0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010, false, target.Id);
        if (p == 0) throw Error("无法访问游戏。请让游戏和助手均以普通权限运行。");
        nint buffer = 0; bool remoteFinished = true;
        try
        {
            if (loaded == null)
            {
                var bytes = Encoding.Unicode.GetBytes(dll + '\0');
                buffer = Native.VirtualAllocEx(p, 0, (nuint)bytes.Length, 0x3000, 4);
                if (buffer == 0 || !Native.WriteProcessMemory(p, buffer, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                    throw Error("无法传递模块路径。");
                remoteFinished = false;
                RemoteCall(p, ResolveRemote(target, "kernel32.dll", "LoadLibraryW"), buffer);
                remoteFinished = true;
            }
            target.Refresh();
            var remote = target.Modules.Cast<ProcessModule>().SingleOrDefault(m => string.Equals(m.FileName, dll, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Windows 未加载适配模块。请检查文件、架构和安全软件报告。");
            // Read the export RVA without running DllMain in the assistant.
            var local = Native.LoadLibraryExW(dll, 0, 1); // DONT_RESOLVE_DLL_REFERENCES
            if (local == 0) throw Error("无法检查适配模块导出。");
            try
            {
                var start = Native.GetProcAddress(local, entryPoint);
                if (start == 0) throw new InvalidOperationException("适配模块 ABI 不兼容。");
                var code = RemoteCall(p, remote.BaseAddress + (start - local), 0);
                if (code != 0) throw new InvalidOperationException($"适配模块启动失败：{code}。");
            }
            finally { Native.FreeLibrary(local); }
        }
        finally
        {
            // A timed-out LoadLibrary may still read its argument. Retain a few KB
            // until game exit instead of freeing live remote memory.
            if (buffer != 0 && remoteFinished) Native.VirtualFreeEx(p, buffer, 0, 0x8000);
            Native.CloseHandle(p);
        }
    }
}
