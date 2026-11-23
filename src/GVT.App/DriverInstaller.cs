using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;
using GVT.Core.Setup;

namespace GVT.App;

/// <summary>
/// 一键安装虚拟声卡（VB-Audio 的 VB-CABLE 和 Hi-Fi Cable）。
///
/// VB-Audio 的安装包不允许随本软件再分发，所以不打包进来：点“安装环境”时从 VB-Audio 官网下载，
/// 校验 Authenticode 签名（必须有效，且签名者是 VB-Audio 作者 Vincent Burel）后再安装。
/// （自己编译时如果在程序目录的 drivers 文件夹里放了安装包，就直接用本地的。）
///
/// 主程序以管理员身份再启动自己一次（--install-drivers），只弹一次 UAC；管理员进程运行安装程序前会再校验一次签名。
/// 装完由主程序把 Windows 默认设备恢复成装之前的样子（驱动安装会把默认播放设备切到虚拟线），再把本软件的设备选好。
/// </summary>
public static class DriverInstaller
{
    public const string HelperArg = "--install-drivers";
    private const string DirArg = "--dir";

    public enum Outcome { Installed, NeedsRestart, Cancelled, Failed }

    public sealed record Result(Outcome Outcome, string Message);

    /// <summary>主程序里调用：下载（或用本地的）安装包、安装缺的驱动、恢复默认设备、选好本软件的设备。</summary>
    public static async Task<Result> InstallAsync(AppConfig cfg, IProgress<string>? status = null)
    {
        var before = EnvironmentCheck.Drivers();
        if (before.AllInstalled) { AssignDevices(cfg, DefaultDevices.Take()); return new(Outcome.Installed, "虚拟声卡已经装好了。"); }

        var need = DriverPackages.All.Where(p => p.Part == "vbcable" ? !before.SendCable : !before.RecvCable).ToList();
        string dir;
        try
        {
            dir = await DriverPackages.PrepareAsync(need, status).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            return new(Outcome.Failed, "下载虚拟声卡安装包失败，请检查网络后再试。（" + ex.Message + "）");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(Outcome.Failed, ex.Message);
        }

        status?.Report("正在安装…");
        var snapshot = DefaultDevices.Take();
        int code;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add(HelperArg);
            psi.ArgumentList.Add(string.Join(',', need.Select(p => p.Part)));
            psi.ArgumentList.Add(DirArg);
            psi.ArgumentList.Add(dir);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动安装程序");
            await p.WaitForExitAsync().ConfigureAwait(true);
            code = p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(Outcome.Cancelled, "已取消。安装虚拟声卡需要管理员授权，请在弹出的窗口里点“是”。");
        }
        Log.Info($"虚拟声卡安装程序已结束（{code}）");
        if (code == 5) return new(Outcome.Failed, "安装包签名校验没有通过，已停止安装。请重新点“安装环境”下载。");

        // 驱动装好后设备要过几秒才出现
        EnvironmentCheck.DriverState after = before;
        for (int i = 0; i < 30; i++)
        {
            after = EnvironmentCheck.Drivers();
            if (after.AllInstalled) break;
            await Task.Delay(500).ConfigureAwait(true);
        }

        // Windows 往往会把默认播放设备切到刚装的虚拟线：恢复。有时切换来得晚，过两秒再恢复一次
        DefaultDevices.Restore(snapshot);
        await Task.Delay(2000).ConfigureAwait(true);
        if (DefaultDevices.Restore(snapshot)) Log.Info("已把 Windows 默认设备恢复成安装前的设备");

        AssignDevices(cfg, snapshot);
        if (after.AllInstalled) return new(Outcome.Installed, "虚拟声卡已装好，软件里的设备也已自动选好。");
        if (code == 0) return new(Outcome.NeedsRestart, "安装程序已运行完，但系统里还看不到虚拟声卡。重启电脑后再打开本软件即可。");
        return new(Outcome.Failed, $"安装没有成功（代码 {code}）。可以再试一次，或重启电脑后再试。");
    }

    /// <summary>选好本软件的四个设备：两条虚拟线固定，麦克风和耳机用安装前的 Windows 默认设备（不是虚拟线时）。</summary>
    public static void AssignDevices(AppConfig cfg, DefaultDevices.Snapshot snapshot)
    {
        var render = DeviceCatalog.ListRender();
        var capture = DeviceCatalog.ListCapture();
        string? Pick(IReadOnlyList<string> list, string name) => list.FirstOrDefault(n => n.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (Pick(render, "VB-Audio Virtual Cable") is { } sendOut) cfg.Send.OutputDevice = sendOut;
        if (Pick(capture, "VB-Audio Hi-Fi Cable") is { } source) cfg.Receive.SourceDevice = source;
        bool Real(string? n) => !string.IsNullOrEmpty(n) && !DeviceCatalog.LooksVirtualCable(n);
        bool Missing(string configured, IReadOnlyList<string> list) => string.IsNullOrWhiteSpace(configured) || Pick(list, configured) == null || DeviceCatalog.LooksVirtualCable(configured);
        if (Missing(cfg.Send.MicDevice, capture) && Real(snapshot.CaptureName)) cfg.Send.MicDevice = snapshot.CaptureName!;
        if (Missing(cfg.Receive.HeadphoneDevice, render) && Real(snapshot.RenderName)) cfg.Receive.HeadphoneDevice = snapshot.RenderName!;
        cfg.Save();
    }

    /// <summary>
    /// 以管理员身份运行的那个进程：运行前再校验一次签名（下载目录用户可写），然后依次静默安装。
    /// 返回 0 = 安装程序都正常结束，5 = 签名校验失败。
    /// </summary>
    public static int RunHelper(string[] args)
    {
        var hi = Array.IndexOf(args, HelperArg);
        var parts = hi >= 0 && hi + 1 < args.Length ? args[hi + 1] : "vbcable,hificable";
        var di = Array.IndexOf(args, DirArg);
        var dir = di >= 0 && di + 1 < args.Length ? args[di + 1] : DriverPackages.BundledDir;

        var log = new List<string>();
        int result = 0;
        foreach (var part in parts.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pkg = DriverPackages.All.FirstOrDefault(p => p.Part == part);
            var exe = pkg == null ? null : Path.Combine(dir, pkg.Part, pkg.Setup);
            if (exe == null || !File.Exists(exe)) { log.Add($"{part}: 找不到安装包"); result = 2; continue; }
            if (!DriverPackages.IsTrusted(exe)) { log.Add($"{part}: 签名校验失败，未运行"); result = 5; continue; }
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "-i -h")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                })!;
                if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { log.Add($"{part}: 超时"); result = 3; continue; }
                log.Add($"{part}: 退出码 {p.ExitCode}");
            }
            catch (Exception ex) { log.Add($"{part}: {ex.Message}"); result = 4; }
        }
        try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "gvt-driver-install.log"), log); } catch { }
        return result;
    }

}
