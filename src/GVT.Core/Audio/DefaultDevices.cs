using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GVT.Core.Audio;

/// <summary>
/// 读取/设置 Windows 默认音频设备。安装虚拟声卡后 Windows 有时会把默认设备切到虚拟线，
/// 需要恢复成真实耳机和麦克风。设置默认设备没有公开 API，这里使用系统自带的 PolicyConfig 接口（音量合成器同款）。
/// </summary>
public static class DefaultDevices
{
    public static string Describe()
    {
        using var en = new MMDeviceEnumerator();
        string Get(DataFlow f, Role r)
        {
            try { return en.GetDefaultAudioEndpoint(f, r).FriendlyName; } catch { return "（无）"; }
        }
        return $"默认播放：{Get(DataFlow.Render, Role.Multimedia)}\n" +
               $"默认通信播放：{Get(DataFlow.Render, Role.Communications)}\n" +
               $"默认录音：{Get(DataFlow.Capture, Role.Multimedia)}\n" +
               $"默认通信录音：{Get(DataFlow.Capture, Role.Communications)}";
    }

    /// <summary>把名称等于（优先）或包含 name 的设备设为默认设备。communications=null 表示所有角色。</summary>
    public static string Set(DataFlow flow, string name, bool? communications = null)
    {
        using var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
        var dev = all.FirstOrDefault(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceNotFoundException($"找不到设备：{name}");
        var roles = communications switch
        {
            true => new[] { ERole.Communications },
            false => new[] { ERole.Console, ERole.Multimedia },
            null => new[] { ERole.Console, ERole.Multimedia, ERole.Communications },
        };
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        foreach (var role in roles)
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(dev.ID, role));
        return dev.FriendlyName;
    }

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, bool defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, bool defaultPeriod, IntPtr defPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility(string id, bool visible);
    }
}
