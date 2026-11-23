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

    /// <summary>当前默认播放 / 录音设备名；没有返回 null。</summary>
    public static string? DefaultName(bool isRender)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(isRender ? DataFlow.Render : DataFlow.Capture, Role.Multimedia).FriendlyName;
        }
        catch { return null; }
    }

    /// <summary>默认设备快照（四个角色的设备 ID），装驱动前记下，装完恢复。</summary>
    public sealed record Snapshot(string? Render, string? RenderComm, string? Capture, string? CaptureComm, string? RenderName, string? CaptureName);

    public static Snapshot Take()
    {
        using var en = new MMDeviceEnumerator();
        MMDevice? Get(DataFlow f, Role r) { try { return en.GetDefaultAudioEndpoint(f, r); } catch { return null; } }
        var r = Get(DataFlow.Render, Role.Multimedia);
        var c = Get(DataFlow.Capture, Role.Multimedia);
        return new Snapshot(r?.ID, Get(DataFlow.Render, Role.Communications)?.ID, c?.ID, Get(DataFlow.Capture, Role.Communications)?.ID, r?.FriendlyName, c?.FriendlyName);
    }

    /// <summary>把默认设备恢复成快照里的样子（只恢复仍然存在的设备）。返回是否有改动。</summary>
    public static bool Restore(Snapshot s)
    {
        using var en = new MMDeviceEnumerator();
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        bool changed = false;
        void Apply(DataFlow f, Role role, string? id, ERole[] roles)
        {
            if (id == null) return;
            string? cur = null;
            try { cur = en.GetDefaultAudioEndpoint(f, role).ID; } catch { }
            if (cur == id) return;
            try { en.GetDevice(id); } catch { return; }
            foreach (var r in roles) changed |= policy.SetDefaultEndpoint(id, r) == 0;
        }
        Apply(DataFlow.Render, Role.Multimedia, s.Render, new[] { ERole.Console, ERole.Multimedia });
        Apply(DataFlow.Render, Role.Communications, s.RenderComm, new[] { ERole.Communications });
        Apply(DataFlow.Capture, Role.Multimedia, s.Capture, new[] { ERole.Console, ERole.Multimedia });
        Apply(DataFlow.Capture, Role.Communications, s.CaptureComm, new[] { ERole.Communications });
        return changed;
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
