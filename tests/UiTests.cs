using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using RonWg;

static class UiTests
{
    [DllImport("user32.dll")]
    static extern bool PrintWindow(nint window, nint context, uint flags);
    static int checks;

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Control.CheckForIllegalCrossThreadCalls = true;
        int result = 1;
        using var loop = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        loop.Shown += (_, _) => { result = Run(args[0]); loop.Close(); };
        Application.Run(loop);
        return result;
    }

    static int Run(string destination)
    {
        string output = Path.GetFullPath(destination);
        Directory.CreateDirectory(output);
        try
        {
            using (var running = Compatibility.Running())
                if (running != null) throw new InvalidOperationException("Run the offline UI check while the real game is closed.");
            using (var form = new MainForm())
            {
                ShowHidden(form);
                var tabs = Children<TabControl>(form).Single();
                Check(tabs.SelectedTab?.Text == "延迟记录", "normal startup opens the latency page");
                var live = Children<Label>(form).Single(label => label.AccessibleName == "延迟记录状态");
                Check(live.Text.Contains("等待游戏"), "idle startup shows waiting status without fabricated latency");
                Save(form, Path.Combine(output, "latency-idle-desktop.png"));
                form.ClientSize = new Size(form.LogicalToDeviceUnits(390), form.LogicalToDeviceUnits(640));
                Application.DoEvents();
                Save(form, Path.Combine(output, "latency-idle-narrow.png"));
                tabs.SelectedIndex = 1;
                Application.DoEvents();
                FindButton(form, "刷新网卡并保存快照").PerformClick();
                Check(Children<RichTextBox>(form).Single().Text.Contains("本机快照已保存"), "diagnostic snapshot remains usable from its tab");
                Save(form, Path.Combine(output, "diagnostics-narrow.png"));
                form.Close();
                Wait(() => form.IsDisposed);
                Check(form.IsDisposed, "closing the normal window completes recorder shutdown");
            }

            string sessions = Path.Combine(output, "sessions");
            string folder = Path.Combine(sessions, "offline-chart-fixture");
            Directory.CreateDirectory(folder);
            long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 90_000;
            long tick = Environment.TickCount64 - 90_000;
            var trace = new LatencyTrace(folder, new LatencySessionInfo(1, "offline-ui", 0, utc, "offline_fixture", "ms", "fixture", OfflineFixture: true));
            trace.SaveInfo();
            using (var writer = new StreamWriter(Path.Combine(folder, "latency-000001.jsonl")))
                for (int second = 0; second <= 90; second++)
                {
                    if (second == 60) writer.WriteLine("{\"event\":\"gap\",\"dropped\":2,\"invalid\":0}");
                    if (second is 61 or 62) continue;
                    for (int peer = 0; peer < 2; peer++)
                        for (int sample = 0; sample < 3; sample++)
                        {
                            double ms = second == 47 && peer == 0 && sample == 1 ? 650 : 45 + peer * 30 + Math.Sin(second / 7.0) * 12 + sample;
                            writer.WriteLine(JsonSerializer.Serialize(new { @event = "rtt", tickMs = tick + second * 1000 + sample * 100,
                                utcMs = utc + second * 1000 + sample * 100, peerId = "peer-" + peer,
                                playerLabel = peer == 0 ? "离线队友甲" : "离线队友乙", side = "host", rttMs = ms }));
                        }
                }
            trace.Complete("offline_fixture_finished");
            // An empty installation path prevents this fixture service from attaching to a game.
            var service = new LatencyService(Path.Combine(output, "no-game-installation"), sessions);
            using var replay = new Form
            {
                Text = "离线界面验证", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                Font = new Font("Microsoft YaHei UI", 10), ClientSize = new Size(800, 640)
            };
            var panel = new LatencyPanel(service, sessions);
            replay.Controls.Add(panel);
            try
            {
                ShowHidden(replay);
                var select = Children<ComboBox>(replay).Single();
                Check(select.Items.Count == 2, "history picker finds persisted match records");
                select.SelectedIndex = 1;
                var table = Children<DataGridView>(replay).Single();
                Wait(() => table.Rows.Count == 2);
                Check(table.Rows.Count == 2, "history opens both peer curves through the normal picker");
                var state = Children<Label>(replay).Single(label => label.AccessibleName == "延迟记录状态");
                Check(state.Text.Contains("离线验证数据") && state.Text.Contains("缺失 2"), "fixture origin and missing samples are visible");
                Save(replay, Path.Combine(output, "latency-history-desktop.png"));
                table.ClearSelection(); table.Rows[1].Selected = true;
                var detail = Children<Label>(replay).Single(label => label.AccessibleName == "连接采样信息");
                Check(detail.Text.Contains("离线队友乙"), "selecting a different player changes the displayed curve");
                replay.ClientSize = new Size(replay.LogicalToDeviceUnits(390), replay.LogicalToDeviceUnits(640));
                Application.DoEvents();
                Check(table.Columns.Cast<DataGridViewColumn>().Sum(column => column.Width) <= table.ClientSize.Width,
                    "390 logical pixels: all peer table columns fit");
                Save(replay, Path.Combine(output, "latency-history-narrow.png"));
                FindButton(replay, "刷新历史记录").PerformClick();
                Check(select.SelectedIndex == 1 && table.Rows.Count == 2, "refreshing history preserves the selected match");
            }
            finally
            {
                replay.Close();
                var stopped = service.StopAsync();
                Wait(() => stopped.IsCompleted);
                stopped.GetAwaiter().GetResult();
            }
            Console.WriteLine($"Checks: {checks}; failures: 0. Screenshots: {output}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static IEnumerable<T> Children<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(child => child is T value ? new[] { value }.Concat(Children<T>(child)) : Children<T>(child));
    static Button FindButton(Control root, string text) => Children<Button>(root).Single(button => button.Text == text);
    static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks++; Console.WriteLine("PASS " + label);
    }
    static void ShowHidden(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show(); Application.DoEvents();
    }
    static void Wait(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("UI operation did not finish");
            Application.DoEvents(); Thread.Sleep(5);
        }
    }
    static void Save(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var context = graphics.GetHdc();
            try { if (!PrintWindow(form.Handle, context, 2)) throw new InvalidOperationException("Window capture failed"); }
            finally { graphics.ReleaseHdc(context); }
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
