using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RonWg;
public sealed class SessionJournal : IDisposable
{
    public const string Version = "0.3.0-experimental";
    public const int Schema = 2;
    const long SegmentLimit = 4 * 1024 * 1024;
    readonly object gate = new();
    readonly long segmentLimit;
    StreamWriter? writer;
    long sequence;
    public string Folder { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string? LastError { get; private set; }
    public SessionJournal(string folder, long segmentLimit = SegmentLimit)
    {
        Folder = Path.GetFullPath(folder); this.segmentLimit = segmentLimit;
        Directory.CreateDirectory(Folder); Open();
        Write("session_open", new { schema = Schema, assistantVersion = Version, os = Environment.OSVersion.ToString(),
            runtime = Environment.Version.ToString(), x64 = Environment.Is64BitProcess, helperPid = Environment.ProcessId,
            utcAnchor = DateTimeOffset.UtcNow, tickAnchorMs = Environment.TickCount64,
            timezone = TimeZoneInfo.Local.Id, note = "tickMs is per-machine; compare peers using UTC and testRunId." });
    }
    void Open() => writer = new StreamWriter(new FileStream(Path.Combine(Folder,"assistant.jsonl"),FileMode.Append,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)) { AutoFlush = true };
    void Rotate()
    {
        if (writer!.BaseStream.Length < segmentLimit) return;
        writer.Dispose();writer=null;
        // Bounded retention: current segment + three preceding segments.
        for(int i=3;i>=1;i--)
        {
            var source=Path.Combine(Folder,i==1?"assistant.jsonl":$"assistant.{i-1}.jsonl");
            var dest=Path.Combine(Folder,$"assistant.{i}.jsonl");
            if(File.Exists(source))File.Move(source,dest,true);
        }
        Open();
    }
    public bool Write(string eventName, object? data = null, string level = "info")
    {
        lock(gate)
        {
            if(writer==null)return false;
            try
            {
                Rotate();
                writer.WriteLine(JsonSerializer.Serialize(new { schema=Schema, utc=DateTimeOffset.UtcNow.ToString("O"),
                    tickMs=Environment.TickCount64, sequence=++sequence, sessionId=SessionId, level, @event=eventName, data }));
                return true;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            { LastError=e.Message;return false; }
        }
    }
    public void Error(string stage, Exception error) => Write("operation_error",new { stage, type=error.GetType().FullName,
        error.HResult, message=error.Message, stack=error.StackTrace },"error");
    public void Context(string runId,string role,string network) => Write("test_context",new { testRunId=runId, role, network });
    public void Marker(string stage,string note,string runId,string role)
    {
        var entry=new { utc=DateTimeOffset.UtcNow.ToString("O"),tickMs=Environment.TickCount64,testRunId=runId,role,stage,note };
        lock(gate)
        {
            File.AppendAllText(Path.Combine(Folder,"markers.jsonl"),JsonSerializer.Serialize(entry)+"\n",new UTF8Encoding(false));
            Write("marker",entry);
        }
    }
    public void Dispose(){lock(gate){writer?.Dispose();writer=null;}}
}
