using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Setup;

/// <summary>开始翻译前要满足的条件，缺了什么、去哪里修。</summary>
public enum EnvFix
{
    /// <summary>去“设备与接入”填 API Key。</summary>
    ApiKey,
    /// <summary>点“安装环境”装虚拟声卡。</summary>
    InstallDrivers,
    /// <summary>去“设备与接入”重新选设备。</summary>
    Devices,
}

public sealed record EnvIssue(string Title, string Detail, EnvFix Fix);

public static class EnvironmentCheck
{
    public const string SendCableRender = "CABLE Input (VB-Audio Virtual Cable)";
    public const string SendCableCapture = "CABLE Output (VB-Audio Virtual Cable)";
    public const string RecvCableRender = "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)";
    public const string RecvCableCapture = "Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)";

    public sealed record DriverState(bool SendCable, bool RecvCable)
    {
        public bool AllInstalled => SendCable && RecvCable;
    }

    public static DriverState Drivers(IReadOnlyList<string> render, IReadOnlyList<string> capture) => new(
        render.Any(n => n.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            && capture.Any(n => n.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)),
        render.Any(n => n.Contains("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase))
            && capture.Any(n => n.Contains("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase)));

    public static DriverState Drivers() => Drivers(DeviceCatalog.ListRender(), DeviceCatalog.ListCapture());

    /// <summary>
    /// 检查开始翻译需要的全部条件。render / capture / defaults 可以传入（测试用），不传就读本机。
    /// defaultRender / defaultCapture：Windows 当前默认播放 / 录音设备名。
    /// </summary>
    public static List<EnvIssue> Run(AppConfig cfg, bool hasApiKey,
        IReadOnlyList<string>? render = null, IReadOnlyList<string>? capture = null,
        string? defaultRender = null, string? defaultCapture = null)
    {
        render ??= DeviceCatalog.ListRender();
        capture ??= DeviceCatalog.ListCapture();
        defaultRender ??= DefaultDevices.DefaultName(isRender: true);
        defaultCapture ??= DefaultDevices.DefaultName(isRender: false);
        var issues = new List<EnvIssue>();

        if (!hasApiKey)
            issues.Add(new("还没有配置 API Key", "翻译要用阿里云百炼的接口，先创建一个 Key 并保存到本软件。", EnvFix.ApiKey));

        var d = Drivers(render, capture);
        if (!d.SendCable && !d.RecvCable)
            issues.Add(new("缺少虚拟声卡 VB-CABLE 和 Hi-Fi Cable", "一条把英文译音送进游戏，一条接收队友的声音。点“安装环境”一次装好。", EnvFix.InstallDrivers));
        else if (!d.SendCable)
            issues.Add(new("缺少虚拟声卡 VB-CABLE", "用来把英文译音送进游戏的麦克风。点“安装环境”自动安装。", EnvFix.InstallDrivers));
        else if (!d.RecvCable)
            issues.Add(new("缺少虚拟声卡 Hi-Fi Cable", "用来接收游戏里队友的声音。点“安装环境”自动安装。", EnvFix.InstallDrivers));

        // 设备都装了，再看具体选择是否还能找到、有没有选错
        void Check(string configured, IReadOnlyList<string> list, string? fallbackDefault, string what, bool mustBeVirtual, bool driverOk)
        {
            if (!driverOk && mustBeVirtual) return; // 已经报了缺声卡
            var name = string.IsNullOrWhiteSpace(configured) ? fallbackDefault : list.FirstOrDefault(n => n.Contains(configured, StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                issues.Add(new($"找不到“{what}”", string.IsNullOrWhiteSpace(configured)
                    ? "Windows 没有可用的默认设备。请插好设备后重新选择。"
                    : $"之前选的“{configured}”现在不在了（可能拔掉了）。请重新选择。", EnvFix.Devices));
                return;
            }
            bool isVirtual = DeviceCatalog.LooksVirtualCable(name);
            if (!mustBeVirtual && isVirtual)
                issues.Add(new($"“{what}”选成了虚拟声卡", $"现在是“{name}”。请改成你真实的{(what.Contains("耳机") ? "耳机" : "麦克风")}，否则会听不到声音或形成回环。", EnvFix.Devices));
            else if (mustBeVirtual && !isVirtual)
                issues.Add(new($"“{what}”应该选虚拟声卡", $"现在是“{name}”。请改回对应的虚拟声卡。", EnvFix.Devices));
        }

        Check(cfg.Send.MicDevice, capture, defaultCapture, "我的麦克风", mustBeVirtual: false, driverOk: true);
        Check(cfg.Send.OutputDevice, render, defaultRender, "英文译音发送到", mustBeVirtual: true, driverOk: d.SendCable);
        Check(cfg.Receive.SourceDevice, capture, defaultCapture, "队友声音来源", mustBeVirtual: true, driverOk: d.RecvCable);
        Check(cfg.Receive.HeadphoneDevice, render, defaultRender, "我的耳机", mustBeVirtual: false, driverOk: true);
        return issues;
    }
}
