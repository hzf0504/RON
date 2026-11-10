using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RonWg;
public record Fingerprint(string RelativePath, string Sha256);
public record CompatibilityCatalog(string BuildId, Fingerprint[] Files);
public static class Compatibility
{
    public const string GameExe = "ReadyOrNot\\Binaries\\Win64\\ReadyOrNotSteam-Win64-Shipping.exe";
    public const string EosDll = "ReadyOrNot\\Binaries\\Win64\\RedpointEOS\\EOSSDK-Win64-Shipping.dll";
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static string FindGame()
    {
        var starts = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var start in starts)
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, GameExe))) return d.FullName;
        var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam != null && File.Exists(Path.Combine(steam, "steamapps", "common", "Ready Or Not", GameExe)))
            return Path.Combine(steam, "steamapps", "common", "Ready Or Not");
        return "";
    }
    public static void Verify(string gameRoot, Process? process = null)
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "compatibility.json");
        var catalog = JsonSerializer.Deserialize<CompatibilityCatalog>(File.ReadAllText(catalogPath), Json.Options)
            ?? throw new InvalidOperationException("缺少兼容性目录。");
        foreach (var item in catalog.Files)
        {
            var path = Path.GetFullPath(Path.Combine(gameRoot, item.RelativePath));
            if (!path.StartsWith(Path.GetFullPath(gameRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("兼容性目录包含无效路径。");
            if (!File.Exists(path) || !string.Equals(Hash(path), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"版本未验证：{item.RelativePath}。此实验版只允许已记录的构建 {catalog.BuildId}，请先重新调查，不能直接刷新哈希绕过检查。");
        }
        if (process != null && !string.Equals(process.MainModule?.FileName, Path.GetFullPath(Path.Combine(gameRoot, GameExe)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标进程不是所选安装目录中的 RON。");
    }
    public static Process? Running() => Process.GetProcessesByName("ReadyOrNotSteam-Win64-Shipping").FirstOrDefault();
    public static string[] PatchFiles(string root) => new[] { "steam_api64.dll", "steam_api64_o.dll", "cream_api.ini" }
        .Select(x => Path.Combine(root, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv153", "Win64", x)).Where(File.Exists).ToArray();
    public static void SaveBaseline(string root, string folder)
    {
        var files = new[] { Path.Combine(root, GameExe), Path.Combine(root, EosDll) }.Concat(PatchFiles(root));
        File.WriteAllText(Path.Combine(folder, "files-before.json"), JsonSerializer.Serialize(files.Select(p => new Fingerprint(Path.GetRelativePath(root, p), Hash(p))), Json.Options));
        // Config is inspected for this single boolean only. No accounts, tokens or file contents are exported.
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReadyOrNot", "Saved", "Config", "Windows", "GameUserSettings.ini");
        var crossplay = File.Exists(settings) ? File.ReadLines(settings).FirstOrDefault(l => Regex.IsMatch(l, @"^\s*bCrossplayEnabled=")) : null;
        File.WriteAllText(Path.Combine(folder, "game-settings.json"), JsonSerializer.Serialize(new { crossplay = crossplay?.Trim() ?? "unknown", originalFilesModifiedByAssistant = false }, Json.Options));
    }
    public static bool CheckBaseline(string root, string folder)
    {
        var list = JsonSerializer.Deserialize<Fingerprint[]>(File.ReadAllText(Path.Combine(folder, "files-before.json")), Json.Options)!;
        var checkedFiles = list.Select(f => new { f.RelativePath, unchanged = File.Exists(Path.Combine(root, f.RelativePath)) && Hash(Path.Combine(root, f.RelativePath)) == f.Sha256 }).ToArray();
        File.WriteAllText(Path.Combine(folder, "files-after.json"), JsonSerializer.Serialize(checkedFiles, Json.Options));
        return checkedFiles.All(f => f.unchanged);
    }
}
