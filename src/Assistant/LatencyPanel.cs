using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace RonWg;

public sealed class LatencyPanel : UserControl
{
    sealed record Choice(string Folder, string Caption) { public override string ToString() => Caption; }
    readonly LatencyService service;
    readonly string sessionRoot;
    readonly ComboBox history = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "选择延迟记录" };
    readonly Label state = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "延迟记录状态" };
    readonly Label detail = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "连接采样信息" };
    readonly DataGridView peers = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "各玩家 RTT"
    };
    readonly LatencyChart chart = new() { Dock = DockStyle.Fill, AccessibleName = "游戏 RTT 曲线" };
    LatencyTrace? displayed;
    LatencyView? view;
    LatencySessionInfo? lastInfo;
    bool changing, loading, exporting;
    int loadGeneration;

    public LatencyPanel(LatencyService service, string sessionRoot)
    {
        this.service = service;
        this.sessionRoot = sessionRoot;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(history, 0, 0);
        state.Margin = new Padding(3, 10, 3, 10);
        layout.Controls.Add(state, 0, 1);
        foreach (var column in new[] { ("连接", 44f), ("RTT ms", 28f), ("秒峰值 ms", 28f) })
        {
            var index = peers.Columns.Add(column.Item1, column.Item1);
            peers.Columns[index].FillWeight = column.Item2;
            peers.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        layout.Controls.Add(peers, 0, 2);
        detail.Margin = new Padding(3, 8, 3, 8);
        layout.Controls.Add(detail, 0, 3);
        layout.Controls.Add(chart, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        var refresh = new Button { Text = "刷新历史记录", AutoSize = true };
        var export = new Button { Text = "导出此记录的脱敏 ZIP", AutoSize = true };
        refresh.Click += (_, _) => RefreshHistory();
        export.Click += async (_, _) => await Export();
        actions.Controls.AddRange([refresh, export]);
        layout.Controls.Add(actions, 0, 5);
        history.SelectedIndexChanged += async (_, _) => { if (!changing) await SelectHistory(); };
        peers.SelectionChanged += (_, _) => { if (!changing) UpdateChart(); };
        RefreshHistory();
        UpdateRecording();
    }

    void RefreshHistory()
    {
        var selected = (history.SelectedItem as Choice)?.Folder ?? "";
        changing = true;
        history.Items.Clear();
        history.Items.Add(new Choice("", "实时 / 最近一次记录"));
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(sessionRoot).OrderDescending())
            {
                var path = Path.Combine(folder, "latency-session.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var info = LatencyTrace.ReadInfo(path);
                    string date = DateTimeOffset.FromUnixTimeMilliseconds(info.StartedUtcMs).ToLocalTime().ToString("MM-dd HH:mm:ss");
                    string label = info.OfflineFixture ? "离线验证记录" : info.EndedUtcMs == null ? "进行中 / 待恢复" : "已结束";
                    history.Items.Add(new Choice(folder, $"{date} · {label}"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { state.Text = "历史目录暂不可读。" + error.Message; }
        history.SelectedIndex = Math.Max(0, history.Items.Cast<Choice>().ToList().FindIndex(item => item.Folder == selected));
        changing = false;
    }

    async Task SelectHistory()
    {
        int generation = ++loadGeneration;
        if ((history.SelectedItem as Choice)?.Folder is not { Length: > 0 } folder)
        {
            loading = false;
            displayed = null;
            UpdateRecording();
            return;
        }
        loading = true;
        state.Text = "正在读取历史延迟记录…";
        try
        {
            var trace = await Task.Run(() => LatencyLogReader.Load(folder));
            if (generation != loadGeneration || IsDisposed) return;
            displayed = trace;
            view = trace.Snapshot(history: true);
            loading = false;
            ShowView(historyView: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (generation != loadGeneration || IsDisposed) return;
            loading = false;
            displayed = null;
            view = null;
            state.Text = "历史记录读取失败。" + error.Message;
            peers.Rows.Clear();
            chart.SetPoints([]);
        }
    }

    public void UpdateRecording()
    {
        var latest = service.Current;
        if (latest?.Info != lastInfo)
        {
            lastInfo = latest?.Info;
            RefreshHistory();
        }
        if (loading || (history.SelectedItem as Choice)?.Folder is { Length: > 0 }) return;
        displayed = latest;
        view = displayed?.Snapshot(history: displayed.Info.EndedUtcMs != null);
        ShowView(historyView: false);
    }

    void ShowView(bool historyView)
    {
        var selected = peers.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as string;
        changing = true;
        peers.Rows.Clear();
        bool ended = displayed?.Info.EndedUtcMs != null || view?.EndReason != null;
        state.Text = historyView ? "历史记录 · " + (ended ? "已结束" : "记录未正常结束") : service.Status;
        if (historyView && displayed?.Info.Error is { Length: > 0 } error) state.Text += " · " + error;
        if (displayed?.Info.OfflineFixture == true) state.Text = "离线验证数据 · 用于检查显示和记录流程";
        if (view != null)
        {
            foreach (var peer in view.Peers)
            {
                bool fresh = historyView || ended || Environment.TickCount64 - peer.LastTickMs <= 3000;
                var latest = peer.Points.LastOrDefault();
                int index = peers.Rows.Add(peer.Label, fresh ? peer.LatestMs.ToString("F1") : "暂无新数据",
                    fresh && latest != null ? latest.MaxMs.ToString("F1") : "—");
                peers.Rows[index].Tag = peer.Id;
                peers.Rows[index].Cells[0].ToolTipText = peer.Label;
            }
            if (view.Dropped + view.Invalid > 0) state.Text += $" · 缺失 {view.Dropped}，无效 {view.Invalid}，曲线保留空缺";
        }
        if (peers.Rows.Count > 0)
        {
            var row = peers.Rows.Cast<DataGridViewRow>().FirstOrDefault(item => item.Tag as string == selected) ?? peers.Rows[0];
            peers.ClearSelection();
            row.Selected = true;
        }
        changing = false;
        UpdateChart();
    }

    void UpdateChart()
    {
        var selected = peers.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as string;
        var peer = view?.Peers.FirstOrDefault(item => item.Id == selected);
        detail.Text = peer == null ? "等待有效 RTT 样本。选择连接后可查看曲线。"
            : $"{peer.Label} · {peer.SampleCount:N0} 个样本 · 全程峰值 {peer.PeakMs:F1} ms";
        chart.SetPoints(peer?.Points ?? []);
    }

    async Task Export()
    {
        if (exporting) return;
        if (displayed == null) { state.Text = "尚无可导出的延迟记录。"; return; }
        exporting = true;
        try
        {
            string folder = displayed.Folder;
            string archive = await Task.Run(() => DiagnosticBundle.Export(folder, Path.Combine(AppContext.BaseDirectory, "exports")));
            state.Text = "延迟记录已脱敏导出。";
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", archive }, UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { state.Text = "延迟记录导出失败。" + error.Message; }
        finally { exporting = false; }
    }
}

public sealed class LatencyChart : Control
{
    LatencyPoint[] points = [];
    readonly ToolTip tip = new();
    RectangleF plot;
    long firstTick, lastTick;
    public LatencyChart() { DoubleBuffered = true; ResizeRedraw = true; BackColor = SystemColors.Window; }
    public void SetPoints(LatencyPoint[] value) { points = value; Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        float scale = DeviceDpi / 96f;
        using var text = new SolidBrush(SystemColors.WindowText);
        if (points.Length == 0)
        {
            graphics.DrawString("暂无有效样本", Font, text, 12 * scale, 12 * scale);
            return;
        }
        plot = new RectangleF(58 * scale, 26 * scale, Width - 74 * scale, Height - 58 * scale);
        if (plot.Width <= 1 || plot.Height <= 1) return;
        firstTick = points[0].TickMs;
        lastTick = Math.Max(firstTick + 1000, points[^1].TickMs);
        double maximum = Math.Max(10, Math.Ceiling(points.Max(point => point.MaxMs) / 10) * 10);
        float X(long tick) => plot.Left + (float)((double)(tick - firstTick) / (lastTick - firstTick) * plot.Width);
        float Y(double value) => plot.Bottom - (float)(value / maximum * plot.Height);
        using var grid = new Pen(SystemColors.ControlLight);
        using var line = new Pen(SystemColors.HotTrack, 1.5f * scale);
        using var range = new Pen(Color.FromArgb(95, SystemColors.HotTrack), 3 * scale);
        graphics.DrawString("ms · 均值与采样范围", Font, text, plot.Left, 2 * scale);
        for (int i = 0; i <= 2; i++)
        {
            double value = maximum * i / 2;
            float y = Y(value);
            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            graphics.DrawString(value.ToString("0.#"), Font, text, 3 * scale, y - Font.Height / 2f);
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        LatencyPoint? previous = null;
        // Collapse only for rendering. Every per-second maximum remains visible.
        foreach (var group in points.GroupBy(point => (int)X(point.TickMs)))
        {
            var current = group.Last();
            float x = X(current.TickMs);
            graphics.DrawLine(range, x, Y(group.Min(point => point.MinMs)), x, Y(group.Max(point => point.MaxMs)));
            if (previous != null && current.Segment >= 0 && previous.Segment == current.Segment && current.TickMs - previous.TickMs <= 2500)
                graphics.DrawLine(line, X(previous.TickMs), Y(previous.MeanMs), x, Y(current.MeanMs));
            else graphics.FillEllipse(text, x - scale, Y(current.MeanMs) - scale, 2 * scale, 2 * scale);
            previous = current;
        }
        string first = DateTimeOffset.FromUnixTimeMilliseconds(points[0].UtcMs).ToLocalTime().ToString("HH:mm:ss");
        string last = DateTimeOffset.FromUnixTimeMilliseconds(points[^1].UtcMs).ToLocalTime().ToString("HH:mm:ss");
        graphics.DrawString(first, Font, text, plot.Left, plot.Bottom + 4 * scale);
        var lastSize = graphics.MeasureString(last, Font);
        graphics.DrawString(last, Font, text, plot.Right - lastSize.Width, plot.Bottom + 4 * scale);
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (points.Length == 0 || !plot.Contains(args.Location)) return;
        long tick = firstTick + (long)((args.X - plot.Left) / plot.Width * (lastTick - firstTick));
        var point = points.MinBy(item => Math.Abs(item.TickMs - tick))!;
        string time = DateTimeOffset.FromUnixTimeMilliseconds(point.UtcMs).ToLocalTime().ToString("HH:mm:ss");
        string label = $"{time}  均值 {point.MeanMs:F1} ms，范围 {point.MinMs:F1}～{point.MaxMs:F1} ms，{point.Count} 个样本";
        if (tip.GetToolTip(this) != label) tip.SetToolTip(this, label);
    }
}
