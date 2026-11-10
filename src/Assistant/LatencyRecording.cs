using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RonWg;

public record LatencySessionInfo(int Schema, string SessionId, int Pid, long StartedUtcMs,
    string Source, string Unit, string GameSha256, long? EndedUtcMs = null, string? EndReason = null,
    bool OfflineFixture = false, string? Error = null);
public record LatencyPoint(long TickMs, long UtcMs, double MeanMs, double MinMs, double MaxMs, long Count, int Segment);
public record LatencyPeerView(string Id, string Label, string Side, double LatestMs, long LastTickMs,
    double PeakMs, long SampleCount, LatencyPoint[] Points);
public record LatencyView(LatencyPeerView[] Peers, long Dropped, long Invalid, string? EndReason);

public sealed class LatencyTrace
{
    sealed class Bucket(long tick, long utc, int segment)
    {
        public long Tick = tick, Utc = utc, Count;
        public int Segment = segment;
        public double Sum, Min = double.PositiveInfinity, Max;
        public LatencyPoint View() => new(Tick, Utc, Sum / Count, Min, Max, Count, Segment);
    }
    sealed class Peer(string id)
    {
        public string Id = id, Label = "", Side = "";
        public long LastTick, Count;
        public double Latest, Peak;
        public readonly SortedList<long, Bucket> Buckets = new();
    }
    readonly object gate = new();
    readonly Dictionary<string, Peer> peers = new();
    long dropped, invalid;
    int segment;
    string? endReason;
    public string Folder { get; }
    public LatencySessionInfo Info { get; private set; }
    public string? SourceEndReason { get { lock (gate) return endReason; } }
    public LatencyTrace(string folder, LatencySessionInfo info) { Folder = folder; Info = info; }

    public static LatencySessionInfo ReadInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = JsonSerializer.Deserialize<LatencySessionInfo>(stream, Json.Options)
            ?? throw new InvalidDataException("无法读取延迟记录信息。");
        if (info.Schema != 1 || info.Unit != "ms") throw new InvalidDataException("延迟记录版本或单位不受支持。");
        return info;
    }

    public void SaveInfo()
    {
        var path = Path.Combine(Folder, "latency-session.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Info, Json.Options));
        File.Move(temporary, path, true);
    }
    public void Complete(string reason, string? error = null)
    {
        lock (gate)
        {
            endReason = reason;
            Info = Info with { EndedUtcMs = Info.EndedUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EndReason = reason, Error = error };
            SaveInfo();
        }
    }
    public void CorruptLine() { lock (gate) { invalid++; segment++; } }
    public void Accept(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var kind = entry.GetProperty("event").GetString();
            lock (gate)
            {
                if (kind == "rtt")
                {
                    var value = entry.GetProperty("rttMs").GetDouble();
                    var tick = entry.GetProperty("tickMs").GetInt64();
                    var utc = entry.GetProperty("utcMs").GetInt64();
                    var id = entry.GetProperty("peerId").GetString()!;
                    var side = entry.GetProperty("side").GetString()!;
                    var label = entry.GetProperty("playerLabel").GetString() ?? "";
                    if (!double.IsFinite(value) || value < 0 || tick < 0 || utc < 0 || utc > 253402300799999 ||
                        string.IsNullOrEmpty(id) || id.Length > 128 || side is not ("host" or "client"))
                        throw new FormatException("Invalid RTT sample");
                    if (!peers.TryGetValue(id, out var peer)) peers.Add(id, peer = new Peer(id));
                    peer.Side = side;
                    peer.Label = side == "client" ? "本机 ↔ 房主" : string.IsNullOrWhiteSpace(label) ? "队友 " + id : label;
                    peer.Count++;
                    peer.Peak = Math.Max(peer.Peak, value);
                    if (tick >= peer.LastTick) { peer.Latest = value; peer.LastTick = tick; }
                    long second = tick / 1000;
                    if (!peer.Buckets.TryGetValue(second, out var bucket))
                        peer.Buckets.Add(second, bucket = new Bucket(tick, utc, segment));
                    if (bucket.Segment != segment) bucket.Segment = -1;
                    bucket.Sum += value;
                    bucket.Min = Math.Min(bucket.Min, value);
                    bucket.Max = Math.Max(bucket.Max, value);
                    bucket.Count++;
                }
                else if (kind == "gap")
                {
                    dropped += Math.Max(0, entry.GetProperty("dropped").GetInt64());
                    invalid += Math.Max(0, entry.GetProperty("invalid").GetInt64());
                    segment++;
                }
                else if (kind == "source_end") endReason = entry.GetProperty("reason").GetString();
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { CorruptLine(); }
    }

    public LatencyView Snapshot(bool history = false, long? nowTick = null)
    {
        lock (gate)
        {
            long cutoff = history ? 0 : (nowTick ?? Environment.TickCount64) - 120_000;
            var views = peers.Values.Select(peer =>
            {
                // Binary search avoids rescanning an entire long match each second.
                int low = 0, high = peer.Buckets.Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (peer.Buckets.Keys[middle] * 1000 < cutoff) low = middle + 1;
                    else high = middle;
                }
                var points = new LatencyPoint[peer.Buckets.Count - low];
                for (int i = low; i < peer.Buckets.Count; i++) points[i - low] = peer.Buckets.Values[i].View();
                return new LatencyPeerView(peer.Id, peer.Label, peer.Side, peer.Latest, peer.LastTick, peer.Peak, peer.Count, points);
            }).ToArray();
            return new(views, dropped, invalid, endReason ?? Info.EndReason);
        }
    }
}

public sealed class LatencyLogReader(LatencyTrace trace)
{
    sealed class Cursor { public long Position; public byte[] Partial = []; }
    readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    static readonly Regex SegmentName = new(@"^latency-\d{6}\.jsonl$", RegexOptions.CultureInvariant);
    public static bool IsSegment(string name) => SegmentName.IsMatch(name);

    public int ReadAvailable()
    {
        int consumed = 0;
        var files = Directory.EnumerateFiles(trace.Folder, "latency-*.jsonl").Where(p => IsSegment(Path.GetFileName(p))).Order().ToArray();
        foreach (var path in files)
        {
            if (!cursors.TryGetValue(path, out var cursor)) cursors.Add(path, cursor = new Cursor());
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < cursor.Position) { cursor.Position = 0; cursor.Partial = []; trace.CorruptLine(); }
            stream.Position = cursor.Position;
            int count = (int)Math.Min(stream.Length - cursor.Position, 1024 * 1024);
            if (count == 0)
            {
                if (path != files[^1] && cursor.Partial.Length > 0) { trace.CorruptLine(); cursor.Partial = []; }
                continue;
            }
            var bytes = new byte[cursor.Partial.Length + count];
            cursor.Partial.CopyTo(bytes, 0);
            stream.ReadExactly(bytes.AsSpan(cursor.Partial.Length));
            cursor.Position += count;
            consumed += count;
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != '\n') continue;
                if (i > start)
                {
                    if (i - start > 16384) trace.CorruptLine();
                    else trace.Accept(Encoding.UTF8.GetString(bytes, start, i - start).TrimStart('\uFEFF'));
                }
                start = i + 1;
            }
            cursor.Partial = bytes[start..];
            if (cursor.Partial.Length > 16384) { trace.CorruptLine(); cursor.Partial = []; }
            if (cursor.Position < stream.Length) return consumed;
            if (path != files[^1] && cursor.Partial.Length > 0) { trace.CorruptLine(); cursor.Partial = []; }
        }
        return consumed;
    }

    public static LatencyTrace Load(string folder)
    {
        var info = LatencyTrace.ReadInfo(Path.Combine(folder, "latency-session.json"));
        var trace = new LatencyTrace(folder, info);
        var reader = new LatencyLogReader(trace);
        // Consume bounded chunks to keep parser memory independent of match length.
        while (reader.ReadAvailable() > 0) { }
        foreach (var cursor in reader.cursors.Values) if (cursor.Partial.Length != 0) trace.CorruptLine();
        return trace;
    }
}
