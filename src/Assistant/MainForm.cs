using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RonWg;
public sealed class MainForm : Form
{
    readonly TextBox root=new();
    readonly ComboBox adapter=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly TextBox peers=new(){PlaceholderText="虚拟 IPv4，多个用逗号分隔"};
    readonly TextBox key=new(){UseSystemPasswordChar=true,PlaceholderText="64 位十六进制字符"};
    readonly TextBox runId=new(){Text=DateTime.Now.ToString("yyyyMMdd")+"-test01",MaxLength=40};
    readonly ComboBox role=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly ComboBox connection=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly ComboBox marker=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly TextBox note=new(){MaxLength=400,PlaceholderText="选填，勿填口令或账号"};
    SessionJournal journal;
    readonly RichTextBox output=new()
    {
        Dock=DockStyle.Fill, ReadOnly=true, BackColor=SystemColors.Window,
        ForeColor=SystemColors.WindowText, BorderStyle=BorderStyle.FixedSingle,
        AccessibleName="运行日志", Margin=Padding.Empty
    };
    readonly Label status=new(){Anchor=AnchorStyles.Left,AutoSize=true,AccessibleName="运行状态"};
    readonly List<Button> actions=[];
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    ProbeSession? session; UdpProbe? udp; CancellationTokenSource? pending;
    System.Threading.Timer? heartbeat; Process? game; string folder; string[] sessionPeers=[]; int sessionIndex; bool candidates;
    Adapter? sessionAdapter;
    bool busy,finishing; int ticks;
    readonly string sessionRoot=Path.Combine(AppContext.BaseDirectory,"sessions");
    readonly LatencyService latency;
    readonly LatencyPanel latencyPanel;
    public MainForm()
    {
        folder=NewFolder();journal=new SessionJournal(folder);
        root.Text=Compatibility.FindGame();
        latency=new LatencyService(root.Text,sessionRoot);
        latencyPanel=new LatencyPanel(latency,sessionRoot);
        root.TextChanged+=(_,_)=>latency.GameRoot=root.Text;
        role.Items.AddRange(["未指定","房主","客机1","客机2","客机3","客机4"]);role.SelectedIndex=0;
        connection.Items.AddRange(["网络未说明","移动网络","宽带 Wi-Fi","有线宽带"]);connection.SelectedIndex=0;
        marker.Items.AddRange(["进入大厅","好友加入","切图开始","进入任务","发生瞬移/卡顿","WG 断开","WG 恢复","返回大厅","其他观察"]);marker.SelectedIndex=0;
        BuildLayout();
        root.Select(0,0);
        RefreshAdapters();
        Log("已创建本机诊断会话。");
        timer.Tick+=async(_,_)=>{latencyPanel.UpdateRecording();await Tick();};timer.Start();
        FormClosing+=async(_,e)=>{if(closing)return;e.Cancel=true;closing=true;timer.Stop();await latency.StopAsync();await Finish("window_closed");journal.Write("application_close");journal.Dispose();Close();};
    }

    void BuildLayout()
    {
        SuspendLayout();
        Text="RON × 喵服 WG 助手 · 0.3 延迟记录测试版";
        AutoScaleDimensions=new SizeF(96,96);
        AutoScaleMode=AutoScaleMode.Dpi;
        Font=new Font("Microsoft YaHei UI",10);
        BackColor=SystemColors.Control;
        StartPosition=FormStartPosition.CenterScreen;
        MinimumSize=new Size(380,510);
        ClientSize=new Size(800,640);

        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(16)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,112));
        var tabs=new TabControl{Dock=DockStyle.Fill,AccessibleName="助手功能"};
        var latencyTab=new TabPage("延迟记录");
        var diagnosticsTab=new TabPage("联机诊断");
        latencyTab.Controls.Add(latencyPanel);
        diagnosticsTab.Controls.Add(layout);
        tabs.TabPages.AddRange([latencyTab,diagnosticsTab]);
        Controls.Add(tabs);

        var body=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
        var columns=ContentTable();
        columns.ColumnCount=2;
        columns.RowCount=2;
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        var settings=BuildSettings();
        settings.Margin=new Padding(0,0,24,0);
        var workflows=BuildWorkflows();
        columns.Controls.Add(settings,0,0);
        columns.Controls.Add(workflows,1,0);
        body.Controls.Add(columns);
        layout.Controls.Add(body,0,0);

        var stateRow=ContentTable();
        stateRow.ColumnCount=2;
        stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stateRow.Margin=new Padding(0,8,0,6);
        var stop=CreateButton("停止 / 恢复原连接");
        stop.Margin=Padding.Empty;
        stop.Click+=async(_,_)=>await Finish();
        stateRow.Controls.Add(status,0,0);
        stateRow.Controls.Add(stop,1,0);
        layout.Controls.Add(stateRow,0,1);

        var exportRow=ButtonRow();
        exportRow.Controls.Add(new Label{Text="运行日志",AutoSize=true,Font=new Font(Font,FontStyle.Bold),Margin=new Padding(0,6,12,4)});
        var report=CreateButton("导出脱敏诊断 ZIP");
        report.Click+=async(_,_)=>await ExportReport();
        var history=CreateButton("导出历史会话");
        history.Click+=async(_,_)=>await ExportHistory();
        exportRow.Controls.AddRange([report,history]);
        layout.Controls.Add(exportRow,0,2);
        layout.Controls.Add(output,0,3);

        void ArrangeColumns()
        {
            var sideBySide=ClientSize.Width>=LogicalToDeviceUnits(740);
            if(sideBySide==(columns.GetColumn(workflows)==1))return;
            columns.SuspendLayout();
            columns.ColumnStyles[0].Width=sideBySide ? 55 : 100;
            columns.ColumnStyles[1].Width=sideBySide ? 45 : 0;
            columns.SetCellPosition(workflows,sideBySide ? new TableLayoutPanelCellPosition(1,0) : new TableLayoutPanelCellPosition(0,1));
            settings.Margin=sideBySide ? new Padding(0,0,LogicalToDeviceUnits(24),0) : Padding.Empty;
            workflows.Margin=sideBySide ? Padding.Empty : new Padding(0,LogicalToDeviceUnits(16),0,0);
            columns.ResumeLayout();
        }
        ResumeLayout();
        SizeChanged+=(_,_)=>ArrangeColumns();
        ArrangeColumns();
    }

    TableLayoutPanel BuildSettings()
    {
        var settings=FieldTable();
        AddHeading(settings,"连接设置",first:true);
        AddField(settings,"游戏目录",root);
        AddField(settings,"喵服网卡",adapter);
        AddField(settings,"朋友虚拟 IP",peers);
        var snapshot=ButtonRow();
        AddAction(snapshot,"刷新网卡并保存快照",Snapshot);
        AddRow(settings,snapshot);

        AddHeading(settings,"测试信息");
        AddField(settings,"测试编号",runId);
        AddField(settings,"角色",role);
        AddField(settings,"网络类型",connection);
        AddHelp(settings,"双方填写相同测试编号，方便对照日志。");

        return settings;
    }

    TableLayoutPanel BuildWorkflows()
    {
        var workflows=FieldTable();
        AddHeading(workflows,"游戏观测",first:true);
        AddHelp(workflows,"观测记录 EOS 调用和 UDP 端点。WG 适配为实验功能，仍可能使用官方中继。");
        var gameActions=ButtonRow();
        AddAction(gameActions,"观测当前游戏",()=>Attach(false));
        AddAction(gameActions,"从 Steam 启动并尝试 WG 适配",()=>Attach(true));
        AddRow(workflows,gameActions);

        AddHeading(workflows,"WG UDP 测试");
        AddHelp(workflows,"测量隧道延迟和丢包。双方使用相同口令，测量前请朋友启动 UDP 应答。");
        AddField(workflows,"诊断口令",key);
        var udpActions=ButtonRow();
        AddAction(udpActions,"生成并复制口令",()=>{
            key.Text=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Clipboard.SetText(key.Text);
            Log("已复制新诊断口令，请双方填写同一个；不会写入报告。");
            return Task.CompletedTask;
        });
        AddAction(udpActions,"启动 UDP 应答",StartResponder);
        AddAction(udpActions,"测量 WG UDP（20 秒）",Measure);
        AddRow(workflows,udpActions);

        AddHeading(workflows,"事件标记");
        AddField(workflows,"事件",marker);
        AddField(workflows,"备注",note);
        var markerRow=ButtonRow();
        var mark=CreateButton("记录时间点");
        mark.Click+=(_,_)=>RecordMarker();
        markerRow.Controls.Add(mark);
        AddRow(workflows,markerRow);
        return workflows;
    }

    static TableLayoutPanel ContentTable() => new()
    {
        Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
        Margin=Padding.Empty
    };

    static TableLayoutPanel FieldTable()
    {
        var table=ContentTable();
        table.ColumnCount=2;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        return table;
    }

    void AddHeading(TableLayoutPanel table,string title,bool first=false) =>
        AddRow(table,new Label
        {
            Text=title, AutoSize=true, Font=new Font(Font,FontStyle.Bold),
            Margin=new Padding(0,first ? 0 : 10,0,5)
        });

    static void AddHelp(TableLayoutPanel table,string text) =>
        AddRow(table,new Label{Text=text,AutoSize=true,Margin=new Padding(0,2,0,6)});

    static void AddRow(TableLayoutPanel table,Control control)
    {
        var row=table.RowCount++;
        table.Controls.Add(control,0,row);
        table.SetColumnSpan(control,table.ColumnCount);
    }

    static void AddField(TableLayoutPanel table,string label,Control field)
    {
        var row=table.RowCount++;
        table.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,0,10,4)},0,row);
        field.Dock=DockStyle.Top;
        field.Margin=new Padding(0,0,0,4);
        field.AccessibleName=label;
        table.Controls.Add(field,1,row);
    }

    static FlowLayoutPanel ButtonRow() => new()
    {
        Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Margin=Padding.Empty
    };

    static Button CreateButton(string text) => new()
    {
        Text=text, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
        Padding=new Padding(7,3,7,3), Margin=new Padding(0,0,7,5)
    };

    void AddAction(FlowLayoutPanel panel,string text,Func<Task> action)
    {
        var b=CreateButton(text);actions.Add(b);panel.Controls.Add(b);
        b.Click+=async(_,_)=>{if(busy)return;busy=true;SetButtons(false);journal.Write("action_start",new{action=text});try{await action();journal.Write("action_complete",new{action=text});}catch(OperationCanceledException){journal.Write("action_cancelled",new{action=text});Log("操作已取消。");}catch(Exception e){journal.Error(text,e);Log("未完成："+e.Message);}finally{busy=false;SetButtons(true);}};
    }
    void SetButtons(bool value){foreach(var b in actions)b.Enabled=value;root.Enabled=adapter.Enabled=peers.Enabled=key.Enabled=value && session==null;}
    bool closing;
    void Log(string value){if(!journal.Write("message",new{message=value})){heartbeat?.Dispose();session?.Dispose();status.Text="日志写入失败，已停止进程内干预。请检查剩余磁盘空间并重启游戏。";}output.AppendText($"[{DateTime.Now:HH:mm:ss}] {value}\n");if(output.TextLength>120000)output.Text=output.Text[^60000..];output.SelectionStart=output.TextLength;output.ScrollToCaret();}
    string NewFolder(){Directory.CreateDirectory(sessionRoot);var f=Path.Combine(sessionRoot,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(f);return f;}
    void SaveContext()
    {
        var id=runId.Text.Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[A-Za-z0-9_-]{1,40}$"))throw new ArgumentException("测试编号仅用 1～40 位字母、数字、下划线或短横线，双方填写相同编号。");
        journal.Context(id,role.Text,connection.Text);
        File.WriteAllText(Path.Combine(journal.Folder,"test-context.json"),JsonSerializer.Serialize(new{testRunId=id,role=role.Text,network=connection.Text,sessionId=journal.SessionId,assistantVersion=SessionJournal.Version},Json.Options));
    }
    void BeginSession(string mode)
    {
        journal.Write("session_switch",new{nextMode=mode});journal.Dispose();folder=NewFolder();journal=new SessionJournal(folder);SaveContext();journal.Write("mode_selected",new{mode});
    }
    void RecordMarker()
    {
        try{SaveContext();journal.Marker(marker.Text,note.Text.Trim(),runId.Text.Trim(),role.Text);Log("已标记："+marker.Text);note.Clear();}
        catch(Exception ex){journal.Error("record_marker",ex);Log("未能记录时间点："+ex.Message);}
    }
    void RefreshAdapters()
    {
        var id=(adapter.SelectedItem as Adapter)?.Index;adapter.Items.Clear();
        foreach(var a in Network.Adapters().Where(a=>a.IsWireGuard && a.Status=="Up" && a.IPv4.Length>0))adapter.Items.Add(a);
        var list=adapter.Items.Cast<Adapter>().ToList();
        var pick=list.FindIndex(a=>a.Index==id);
        if(pick<0)pick=list.FindIndex(a=>a.Name.Equals(Network.NyaAdapterName,StringComparison.OrdinalIgnoreCase));
        if(adapter.Items.Count>0)adapter.SelectedIndex=Math.Max(0,pick);
        status.Text=adapter.Items.Count==0 ? "暂无活动的喵服网卡" : "就绪";
        if(adapter.Items.Count==0 && IsHandleCreated)Log("没有活动的 WG 网卡。请先打开喵服客户端（nya-client）并连接，再刷新。");
    }
    (Adapter,IPAddress[]) Selection()
    {
        var a=adapter.SelectedItem as Adapter??throw new InvalidOperationException("没有选中活动的 WG 网卡。请先在喵服客户端连接并刷新。");
        var p=Network.Peers(peers.Text);Network.Validate(a,p);return(a,p);
    }
    Task Snapshot()
    {
        if(session!=null)throw new InvalidOperationException("请先停止当前观测再刷新。");
        RefreshAdapters();SaveContext();Network.SaveSnapshot(journal.Folder);Log("本机快照已保存："+journal.Folder);
        var clients=Network.TunnelClients();
        Log(clients.Length>0?"检测到隧道客户端："+string.Join("、",clients)+"。":"未检测到喵服客户端 nya-client 进程。");
        if(adapter.SelectedItem is Adapter sel)Log($"当前 WG 网卡：{sel}，MTU {sel.Mtu}。朋友地址请填写对方喵服客户端里显示的虚拟 IP。");
        var proxy=Network.Adapters().Where(a=>a.Status=="Up" && !a.IsWireGuard && (a.Description.Contains("Meta",StringComparison.OrdinalIgnoreCase)||a.Description.Contains("TUN",StringComparison.OrdinalIgnoreCase))).ToArray();
        if(proxy.Length>0)Log("当前存在其他 TUN 接口："+string.Join("、",proxy.Select(a=>a.Name))+"。这只是当前快照；实测时请按平时玩法关闭代理/加速器。");
        return Task.CompletedTask;
    }
    Task StartResponder()
    {
        var (a,p)=Selection();udp?.Dispose();udp=new UdpProbe(IPAddress.Parse(a.IPv4[0]),p,ProbeWire.Key(key.Text),()=>Network.Validate(a,p));
        Log($"UDP 应答已绑定 {a.IPv4[0]}:{ProbeWire.Port}，仅接受朋友列表和匹配口令。若收不到，使用说明中的精确防火墙规则。");return Task.CompletedTask;
    }
    async Task Measure()
    {
        if(session!=null)throw new InvalidOperationException("为避免干扰对局记录，请先停止游戏观测再做助手 UDP 测量。");
        var (_,p)=Selection();BeginSession("helper_udp_only");await StartResponder();pending=new CancellationTokenSource();
        Log("正在测量助手 UDP 测试流量。朋友也需启动 UDP 应答；这不代表游戏已走 WG。");
        try
        {
            var result=await Task.WhenAll(p.Select(ip=>udp!.Measure(ip,cancellation:pending.Token)));
            journal.Write("helper_udp_results",result);
            File.WriteAllText(Path.Combine(folder,"udp-probe.json"),JsonSerializer.Serialize(result,Json.Options));
            foreach(var r in result)Log($"{r.Peer}：收到 {r.Received}/{r.Sent}，超时/丢失 {r.LossPercent:F1}%，RTT P50 {r.P50Ms:F1} / P95 {r.P95Ms:F1} ms，相邻 RTT 差 P95 {r.JitterP95Ms:F1} ms。");
        }
        finally{pending.Dispose();pending=null;}
    }
    async Task Attach(bool candidateMode)
    {
        if(session!=null)throw new InvalidOperationException("当前已有观测会话。请先停止并重启游戏。");
        BeginSession(candidateMode?"wg_candidates_experimental":"observe");
        using var operation=new CancellationTokenSource(TimeSpan.FromMinutes(3));pending=operation;
        try {
        udp?.Dispose();udp=null;
        candidates=candidateMode;sessionIndex=0;sessionPeers=[];Adapter? selected=null;
        if(candidateMode || adapter.SelectedItem is Adapter && !string.IsNullOrWhiteSpace(peers.Text))
        {
            var (a,p)=Selection();selected=a;sessionIndex=a.Index;sessionPeers=p.Select(x=>x.ToString()).ToArray();
        }
        var gameRoot=Path.GetFullPath(root.Text);Log("正在验证游戏和 EOS 文件版本…");
        await Task.Run(()=>Compatibility.Verify(gameRoot));
        operation.Token.ThrowIfCancellationRequested();
        game=Compatibility.Running();
        if(candidateMode && game!=null)throw new InvalidOperationException("候选适配必须先完整退出游戏，以避免 SDK 已缓存旧网卡。然后点击此按钮从 Steam 启动。");
        if(candidateMode)
        {
            Process.Start(new ProcessStartInfo("steam://run/1144200"){UseShellExecute=true});Log("等待 Steam 启动游戏；如 Steam 显示启动选项，请正常选择。");
            while((game=Compatibility.Running())==null)await Task.Delay(150,operation.Token);
        }
        if(game==null)throw new InvalidOperationException("未发现游戏进程。请先通过 Steam 正常启动游戏，再点击观测。");
        if(selected!=null)Network.Validate(selected,sessionPeers.Select(IPAddress.Parse).ToArray());
        await Task.Run(()=>Compatibility.Verify(gameRoot,game));journal.Write("compatibility_verified",new{gamePid=game.Id});
        operation.Token.ThrowIfCancellationRequested();
        await Task.Run(()=>Compatibility.SaveBaseline(gameRoot,folder));Network.SaveSnapshot(folder,sessionPeers.Select(IPAddress.Parse).ToArray());
        File.WriteAllText(Path.Combine(folder,"session.json"),JsonSerializer.Serialize(new{mode=candidateMode?"wg_candidates_experimental":"observe",pid=game.Id,interfaceIndex=sessionIndex,peers=sessionPeers,started=DateTimeOffset.Now},Json.Options));
        operation.Token.ThrowIfCancellationRequested();
        var newSession=await Task.Run(()=>new ProbeSession(game,folder,candidateMode,sessionIndex));
        if(operation.IsCancellationRequested){newSession.Dispose();operation.Token.ThrowIfCancellationRequested();}
        session=newSession;sessionAdapter=selected;
        journal.Write("native_attached",new{gamePid=game.Id,mode=candidateMode?"wg_candidates_experimental":"observe",interfaceIndex=sessionIndex,peers=sessionPeers});
        heartbeat=new System.Threading.Timer(_=>{try{session?.Heartbeat();}catch(ObjectDisposedException){}},null,0,500);
        Log($"已附加 PID {game.Id}。请用原有 Steam 邀请进入私密房间，之后查看证据计数。");
        if(candidateMode)Log("实验性 WG 候选筛选已启用。筛选次数为 0 表示尚未看到 SDK 使用此入口；即使有次数也不能单独证明对局走了 WG。");
        } finally {if(ReferenceEquals(pending,operation))pending=null;}
    }
    async Task Tick()
    {
        if(session==null || finishing)return;
        try
        {
            if(game?.HasExited==true){journal.Write("game_exit",new{gamePid=game.Id});Log("游戏已退出。");await Finish("game_exited");return;}
            if(candidates && ++ticks%2==0)
            {
                if(sessionAdapter==null)throw new InvalidOperationException("缺少原始 WG 接口记录。");Network.Validate(sessionAdapter,sessionPeers.Select(IPAddress.Parse).ToArray());
            }
            var e=EvidenceReader.Read(session.LogPath,sessionPeers,sessionIndex);
            status.Text=$"EOS 发送 {e.Sends} / 接收 {e.Received} · 筛选 {e.Filtered}/{e.AdapterCalls} · WG 端点 UDP ↑{e.PeerTx} ↓{e.PeerRx}\n{e.Verdict}";
            if(e.Restored){Log("进程内模块已自行停止，请查看日志中的恢复原因。");await Finish("native_stopped");}
        }
        catch(Exception e){journal.Error("observation",e);Log("观测中止："+e.Message+" 请离开当前对局；恢复后重新启动游戏。");await Finish("observation_error");}
    }
    async Task Finish(string reason="user_stop")
    {
        if(finishing)return;finishing=true;
        try
        {
            journal.Write("stop_requested",new{reason});
            pending?.Cancel();udp?.Dispose();udp=null;heartbeat?.Dispose();heartbeat=null;
            var active=session;session=null;active?.Dispose();
            if(active!=null)
            {
                await Task.Delay(750);var path=EvidenceReader.Report(active.Folder,sessionPeers,sessionIndex);
                var same=await Task.Run(()=>Compatibility.CheckBaseline(root.Text,active.Folder));
                var restored=EvidenceReader.Read(active.LogPath,sessionPeers,sessionIndex).Restored;
                journal.Write("stop_result",new{originalFilesUnchanged=same,iatRestoreAcknowledged=restored,gameExited=game?.HasExited??true,restartGameRequired=true});
                Log(same?"游戏、EOS 和现有补丁文件哈希未变。":"文件核对出现差异，请查看 files-after.json。");
                Log("已停止干预并生成报告："+path+"。完整恢复原连接：退出并重启游戏。助手未创建系统路由或修改 WG 配置。");
            }
            status.Text="已停止。若本次用过适配，请退出并重启游戏清除 EOS 缓存。";
        }
        catch(Exception e){journal.Error("restore",e);Log("恢复记录未完成："+e.Message+"。请退出并重启游戏以完整清除模块。");}
        finally{finishing=false;SetButtons(!busy);}
    }
    bool exporting;
    async Task ExportHistory()
    {
        if(exporting)return;
        using var choose=new FolderBrowserDialog{Description="选择 sessions 下发生问题的会话文件夹",InitialDirectory=sessionRoot,ShowNewFolderButton=false,UseDescriptionForTitle=true};
        if(choose.ShowDialog(this)!=DialogResult.OK)return;
        var source=Path.GetFullPath(choose.SelectedPath);
        if(!source.StartsWith(Path.GetFullPath(sessionRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){Log("请选择本助手 sessions 下的单次会话目录。");return;}
        exporting=true;
        try
        {
            EvidenceReader.ReportFromMetadata(source);
            var bundle=await Task.Run(()=>DiagnosticBundle.Export(source,Path.Combine(AppContext.BaseDirectory,"exports")));
            Log("历史会话诊断包："+bundle);Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={"/select,",bundle},UseShellExecute=true});
        }
        catch(Exception e){journal.Error("history_export",e);Log("历史会话导出失败："+e.Message);}
        finally{exporting=false;}
    }
    async Task ExportReport()
    {
        if(exporting)return;exporting=true;
        try
        {
            SaveContext();var source=journal.Folder;EvidenceReader.Report(source,sessionPeers,sessionIndex);
            journal.Write("export_requested",new{privacy="redacted"});
            var bundle=await Task.Run(()=>DiagnosticBundle.Export(source,Path.Combine(AppContext.BaseDirectory,"exports")));
            Log("脱敏诊断包已生成："+bundle+"。请只回传此 ZIP；不需要发送整个游戏或喵服客户端目录。");
            Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={"/select,",bundle},UseShellExecute=true});
        }
        catch(Exception e){journal.Error("export",e);Log("导出未完成："+e.Message);}
        finally{exporting=false;}
    }
}
