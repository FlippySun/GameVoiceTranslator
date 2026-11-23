using System.IO;
using System.Threading;
using System.Windows;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.App;

public partial class App : Application
{
    private const string InstanceMutexName = "GVT_GameVoiceTranslator_SingleInstance";
    public const string ShowEventName = "GVT_GameVoiceTranslator_ShowMainWindow";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 以管理员身份运行的“安装虚拟声卡”助手：只装驱动，不开界面
        var hi = Array.IndexOf(e.Args, DriverInstaller.HelperArg);
        if (hi >= 0)
        {
            Shutdown(DriverInstaller.RunHelper(e.Args));
            return;
        }

        // 只允许运行一个：再次打开时把已经在运行（可能缩在托盘里）的窗口叫出来
        bool debugShot = e.Args.Contains("--shot");
        if (!debugShot)
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool first);
            if (!first)
            {
                try { using var show = EventWaitHandle.OpenExisting(ShowEventName); show.Set(); } catch { }
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("未处理异常：" + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "出错了");
            ex.Handled = true;
        };

        // 如果上次崩溃后守护进程还在转发原声，现在由主程序接管
        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, "GVT_Watchdog_StopPassthrough");
            stop.Set();
        }
        catch { }

        var cfg = AppConfig.LoadOrCreate();
        VoiceCatalog.LoadCustom(cfg);
        var glossaryPath = cfg.ResolveGlossaryPath();
        var defaultGlossary = Path.Combine(AppContext.BaseDirectory, "glossary.default.json");
        if (!File.Exists(glossaryPath) && File.Exists(defaultGlossary)) File.Copy(defaultGlossary, glossaryPath);

        Log.Info($"配置目录：{AppConfig.ConfigDirectory}");
        var window = new MainWindow(cfg);
        if (!debugShot) window.ListenForShowRequests(ShowEventName);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
