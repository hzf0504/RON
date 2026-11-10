using System.Diagnostics;
using System.Text.Json;

namespace RonWg;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if(args.Length>0)
        {
            try { RunCli(args).GetAwaiter().GetResult(); }
            catch(Exception ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(_,e)=>Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>Fatal(e.ExceptionObject as Exception??new Exception("Unknown unhandled failure"));
        try{Application.Run(new MainForm());}catch(Exception e){Fatal(e);}
    }
    static async Task RunCli(string[] args)
    {
        if(args[0]=="--version"){Console.WriteLine($"RON WG Assistant {SessionJournal.Version}; log schema {SessionJournal.Schema}");return;}
        if(args[0]=="--snapshot" && args.Length==2){Console.WriteLine(Network.SaveSnapshot(Path.GetFullPath(args[1])));return;}
        if(args[0]=="--restore" && args.Length==2){ProbeSession.StopExisting(int.Parse(args[1]));Console.WriteLine("已请求恢复。完整恢复请退出并重启游戏。");return;}
        if(args[0]=="--export" && args.Length==3){EvidenceReader.ReportFromMetadata(args[1]);Console.WriteLine(DiagnosticBundle.Export(args[1],args[2]));return;}
        if(args[0]=="--observe" && args.Length==4)
        {
            var root=Path.GetFullPath(args[1]);var folder=Path.GetFullPath(args[2]);var seconds=int.Parse(args[3]);
            if(seconds is <1 or >3600)throw new ArgumentException("时长应为 1～3600 秒。");
            var p=Compatibility.Running()??throw new InvalidOperationException("请先正常启动 RON。");
            Compatibility.Verify(root,p);Directory.CreateDirectory(folder);Compatibility.SaveBaseline(root,folder);Network.SaveSnapshot(folder);
            using(var session=new ProbeSession(p,folder,false,0))
                for(var i=0;i<seconds*2 && !p.HasExited;i++){session.Heartbeat();await Task.Delay(500);}
            await Task.Delay(750);Compatibility.CheckBaseline(root,folder);Console.WriteLine(EvidenceReader.Report(folder,[],0));return;
        }
        throw new ArgumentException("用法：--snapshot <目录> / --observe <游戏根目录> <会话目录> <秒数> / --restore <PID> / --export <会话目录> <导出目录>");
    }
    static void Fatal(Exception error)
    {
        string? crashFolder=null;
        try
        {
            foreach(var process in Process.GetProcessesByName("ReadyOrNotSteam-Win64-Shipping"))
                using(process){try{ProbeSession.StopExisting(process.Id);}catch{}try{LatencyCapture.StopExisting(process.Id);}catch{}}
            crashFolder=Path.Combine(AppContext.BaseDirectory,"sessions","error-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
            using var journal=new SessionJournal(crashFolder);journal.Error("unhandled",error);
        }
        catch { /* Do not recurse on a full disk or non-writable installation directory. */ }
        MessageBox.Show("助手出现错误，已请求停止进程内干预。请退出并重启游戏。\n"+error.Message+"\n"+(crashFolder==null?"日志目录不可写，请解压到有写入权限的文件夹。":"错误日志："+crashFolder),"RON WG 助手",MessageBoxButtons.OK,MessageBoxIcon.Error);
        Environment.ExitCode=1;Application.Exit();
    }
}
