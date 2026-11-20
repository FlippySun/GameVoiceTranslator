using System.IO;
using System.Threading;
using System.Windows;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        var glossaryPath = cfg.ResolveGlossaryPath();
        var defaultGlossary = Path.Combine(AppContext.BaseDirectory, "glossary.default.json");
        if (!File.Exists(glossaryPath) && File.Exists(defaultGlossary)) File.Copy(defaultGlossary, glossaryPath);

        Log.Info($"配置目录：{AppConfig.ConfigDirectory}");
        new MainWindow(cfg).Show();
    }
}
