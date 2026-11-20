using System.Diagnostics;
using System.Runtime.InteropServices;
using GVT.Core.Audio;

// 崩溃守护进程：主程序正常退出时会通知它一起退出；
// 如果主程序意外消失，就把“队友声音来源”（虚拟线 B）直接转发到耳机，保证仍能听到队友原声。
// 发送方向什么也不做：虚拟麦克风保持静音，不会把中文原声发出去。

static string Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}

if (!int.TryParse(Arg(args, "--parent"), out var parentPid)) return 2;
var captureName = Arg(args, "--capture");
var renderName = Arg(args, "--render");

using var graceful = new EventWaitHandle(false, EventResetMode.ManualReset, $"GVT_Watchdog_Graceful_{parentPid}");
using var stopPassthrough = new EventWaitHandle(false, EventResetMode.AutoReset, "GVT_Watchdog_StopPassthrough");

Process parent;
try { parent = Process.GetProcessById(parentPid); }
catch { return 0; }

var parentExited = new ManualResetEvent(false);
parent.EnableRaisingEvents = true;
parent.Exited += (_, _) => parentExited.Set();
if (parent.HasExited) parentExited.Set();

int which = WaitHandle.WaitAny(new WaitHandle[] { graceful, parentExited });
if (which == 0 || graceful.WaitOne(0)) return 0; // 正常退出

// ---- 主程序意外退出：恢复原声 ----
Native.AllocConsole();
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "游戏语音翻译 · 原声恢复";
Console.WriteLine("翻译程序意外退出了。为了让你仍能听到队友，已把队友原声直接转到耳机。");
Console.WriteLine("英文译音不会再发送；游戏里的麦克风现在是静音的，需要说话请把游戏麦克风改回真实麦克风。");
Console.WriteLine("重新打开翻译程序，或关闭此窗口，即可停止转发。");

try
{
    using var cap = new CaptureSource(DeviceCatalog.FindCapture(captureName));
    using var mixer = new OutputMixer(DeviceCatalog.FindRender(renderName));
    var pt = new PassthroughProvider(mixer.SampleRate, mixer.Channels) { TargetGain = 1f };
    mixer.AddInput(pt);
    cap.Data += s => pt.Write(s, cap.Channels, cap.SampleRate);
    mixer.Start();
    cap.Start();
    Console.WriteLine($"转发中：{cap.DeviceName} → {mixer.DeviceName}");
    stopPassthrough.WaitOne();
}
catch (Exception ex)
{
    Console.WriteLine("恢复原声失败：" + ex.Message);
    Console.WriteLine("请在游戏里把“语音聊天输出设备”改回耳机。按回车关闭。");
    Console.ReadLine();
}
return 1;

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();
}
