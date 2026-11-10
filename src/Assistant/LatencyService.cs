using System.ComponentModel;
using System.Diagnostics;

namespace RonWg;

public sealed class LatencyService
{
    readonly string sessionRoot;
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    volatile LatencyTrace? current;
    volatile string status = "等待游戏启动，助手将自动记录延迟。";
    volatile string gameRoot;
    public string GameRoot { set => gameRoot = value; }
    public LatencyTrace? Current => current;
    public string Status => status;

    public LatencyService(string root, string sessionRoot)
    {
        gameRoot = root;
        this.sessionRoot = sessionRoot;
        Directory.CreateDirectory(sessionRoot);
        worker = Task.Run(Run);
    }

    async Task Run()
    {
        Process? process = null;
        LatencyCapture? capture = null;
        LatencyLogReader? reader = null;
        int blockedPid = 0, attempts = 0;
        string ending = "assistant_closed";
        string checkedRoot = gameRoot;
        LatencyTrace NewTrace(int pid)
        {
            var folder = Path.Combine(sessionRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-latency-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            var info = new LatencySessionInfo(1, Guid.NewGuid().ToString("N"), pid,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "ue_playerstate_update_ping", "ms", LatencyProfile.GameSha256);
            var trace = new LatencyTrace(folder, info);
            trace.SaveInfo();
            current = trace;
            return trace;
        }
        async Task EndCurrent(string reason, string? error = null)
        {
            try
            {
                if (capture != null)
                {
                    capture.RequestStop();
                    for (int i = 0; i < 20 && capture.State < 2 && process?.HasExited == false; i++) await Task.Delay(100);
                    if (capture.State < 2 && process?.HasExited == false) reason = "restore_requested";
                }
                if (reader != null) while (reader.ReadAvailable() > 0) { }
                if (current?.SourceEndReason == "restore_conflict") reason = "restore_conflict";
                current?.Complete(reason, error);
            }
            finally
            {
                capture?.Dispose(); capture = null;
                process?.Dispose(); process = null;
                reader = null;
            }
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (checkedRoot != gameRoot) { checkedRoot = gameRoot; blockedPid = attempts = 0; }
                    if (process == null)
                    {
                        using var found = Compatibility.Running();
                        if (found == null)
                        {
                            blockedPid = attempts = 0;
                            status = "等待游戏启动，助手将自动记录延迟。";
                        }
                        else if (found.Id != blockedPid)
                        {
                            status = "发现游戏，正在核对延迟采集版本…";
                            LatencyProfile.Verify(gameRoot, found);
                            if (stop.IsCancellationRequested) break;
                            var trace = NewTrace(found.Id);
                            try
                            {
                                process = Process.GetProcessById(found.Id);
                                capture = new LatencyCapture(process, trace.Folder);
                            }
                            catch (Exception error)
                            {
                                trace.Complete("attach_failed", error.Message);
                                blockedPid = found.Id;
                                process?.Dispose(); process = null;
                                throw;
                            }
                            reader = new LatencyLogReader(trace);
                            attempts = 0;
                            status = "正在记录游戏 RTT，等待有效连接样本。";
                        }
                    }
                    else if (process.HasExited)
                    {
                        await EndCurrent("game_exited");
                        status = "游戏已退出，延迟记录已保存。";
                    }
                    else
                    {
                        capture!.Heartbeat();
                        reader!.ReadAvailable();
                        if (capture.State is 2 or 3)
                        {
                            int state = capture.State;
                            blockedPid = state == 3 || current?.SourceEndReason == "restore_conflict" ? process.Id : 0;
                            await EndCurrent(state == 3 ? "capture_failed" : "source_stopped");
                            status = state == 3 ? "RTT 采集已停止，请检查日志目录是否可写并重启游戏。" : "RTT 来源已停止，记录已保存。";
                        }
                        else status = "正在记录游戏 RTT · 每秒刷新曲线";
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
                {
                    status = "延迟记录暂不可用。" + error.Message;
                    if (process != null)
                    {
                        blockedPid = process.Id;
                        await EndCurrent("recording_error", error.Message);
                    }
                    // Module enumeration can be incomplete while the game is starting.
                    if (error is InvalidOperationException || ++attempts >= 10)
                    {
                        using var failed = Compatibility.Running();
                        if (failed != null)
                        {
                            blockedPid = failed.Id;
                            if (current?.Info.Pid != failed.Id) NewTrace(failed.Id).Complete("verification_failed", error.Message);
                        }
                    }
                }
                await Task.Delay(capture == null ? 1500 : 250, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { status = "延迟记录中止。" + error.Message; ending = "recorder_error"; }
        finally
        {
            try
            {
                if (capture != null)
                {
                    await EndCurrent(ending);
                }
            }
            catch (Exception error) { status = "停止记录时出现错误。" + error.Message; }
            capture?.Dispose();
            process?.Dispose();
        }
    }

    public async Task StopAsync()
    {
        stop.Cancel();
        await worker;
        stop.Dispose();
    }
}
