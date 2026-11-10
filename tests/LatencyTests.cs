using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using RonWg;

static class LatencyTests
{
    public static void Run(string root, string? nativeResults, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "latency-recording");
        Directory.CreateDirectory(folder);
        long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var info = new LatencySessionInfo(1, "offline-data-test", 0, utc, "offline_fixture", "ms", "fixture", OfflineFixture: true);
        var trace = new LatencyTrace(folder, info);
        trace.SaveInfo();
        string Sample(long tick, double ms, string id = "peer-1", string label = "队友甲") =>
            JsonSerializer.Serialize(new { @event = "rtt", tickMs = tick, utcMs = utc + tick, peerId = id, playerLabel = label, side = "host", rttMs = ms },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string path = Path.Combine(folder, "latency-000001.jsonl");
        byte[] bytes = Encoding.UTF8.GetBytes(Sample(1000, 25) + "\n");
        int split = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("友")) + 1;
        File.WriteAllBytes(path, bytes[..split]);
        var reader = new LatencyLogReader(trace);
        reader.ReadAvailable();
        check(trace.Snapshot(history: true).Peers.Length == 0, "partial live JSON is held until a complete sample arrives");
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(bytes.AsSpan(split));
        reader.ReadAvailable(); reader.ReadAvailable();
        var peer = trace.Snapshot(history: true).Peers.Single();
        check(peer.Label == "队友甲" && peer.SampleCount == 1 && peer.LatestMs == 25, "incremental UTF-8 samples load once with millisecond values");
        File.AppendAllText(path, Sample(1100, 900) + "\n" + Sample(1200, 25) + "\n" + Sample(1300, 40, "peer-2", "队友甲") + "\n");
        reader.ReadAvailable();
        var snapshot = trace.Snapshot(history: true);
        peer = snapshot.Peers.Single(p => p.Id == "peer-1");
        check(peer.SampleCount == 3 && peer.Points.Single().Count == 3 && peer.Points.Single().MaxMs == 900,
            "all samples survive aggregation and a brief spike remains visible");
        check(snapshot.Peers.Length == 2, "distinct connections stay separate even when display names match");
        File.AppendAllText(path, "{\"event\":\"gap\",\"dropped\":2,\"invalid\":0}\n" + Sample(3000, 30) + "\n" + Sample(3100, -1) + "\n");
        reader.ReadAvailable();
        snapshot = trace.Snapshot(history: true);
        peer = snapshot.Peers.Single(p => p.Id == "peer-1");
        check(snapshot.Dropped == 2 && snapshot.Invalid == 1 && peer.Points[0].Segment != peer.Points[^1].Segment,
            "missing and invalid samples create explicit gaps instead of invented latency");
        check(trace.Snapshot(nowTick: 130000).Peers.All(p => p.Points.Length == 0), "live chart window excludes old samples without deleting history");
        File.WriteAllText(Path.Combine(folder, "latency-000002.jsonl"), Sample(4000, 42) + "\n{truncated");
        trace.Complete("game_exited");
        var restored = LatencyLogReader.Load(folder);
        var restoredView = restored.Snapshot(history: true);
        check(restored.Info.EndReason == "game_exited" && restoredView.Peers.Single(p => p.Id == "peer-1").SampleCount == 5,
            "finished sessions reopen across log segments with every valid sample intact");
        check(restoredView.Invalid == 2, "history reports a crash-truncated tail while retaining earlier samples");
        File.WriteAllText(Path.Combine(folder, "latency-evil.jsonl"), "DO_NOT_EXPORT");
        var zipPath = DiagnosticBundle.Export(folder, Path.Combine(root, "exports"));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            string Read(ZipArchiveEntry entry) { using var text = new StreamReader(entry.Open()); return text.ReadToEnd(); }
            string all = string.Join("\n", zip.Entries.Select(Read));
            check(zip.GetEntry("latency-session.json") != null && zip.GetEntry("latency-000002.jsonl") != null &&
                zip.GetEntry("latency-evil.jsonl") == null, "latency export includes only named segments and session metadata");
            check(!all.Contains("队友甲") && !all.Contains("DO_NOT_EXPORT") && all.Contains("player-"),
                "latency diagnostic export pseudonymizes player names");
        }
        if (nativeResults != null)
        {
            var native = LatencyLogReader.Load(Path.Combine(nativeResults, "latency"));
            var nativeView = native.Snapshot(history: true);
            check(nativeView.Peers.Length == 3 && nativeView.Peers.Sum(p => p.SampleCount) == 4,
                "native callback output reopens through the production history reader");
            check(Math.Abs(nativeView.Peers.Single(p => p.Id == "11-101").PeakMs - 1700) < 0.1 && nativeView.Invalid == 1,
                "native pre-clamping RTT spike and invalid-sample counts reach the UI model");
            check(nativeView.Peers.Single(p => p.Side == "client").Label == "本机 ↔ 房主",
                "client view identifies its own host connection");
        }
    }
}
