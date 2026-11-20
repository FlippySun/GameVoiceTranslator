using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GVT.App;

/// <summary>
/// 用 Raw Input（RIDEV_INPUTSINK）在后台读取键盘和鼠标按键：游戏在前台时也能收到按下/松开，
/// 不注入游戏进程，也不用低级键盘钩子（钩子在本程序卡顿时会拖慢整个系统的输入）。
/// </summary>
public sealed class RawInputHotkeys : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    private readonly HwndSource _source;
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(按键名, 是否按下)。在 UI 线程上触发。</summary>
    public event Action<string, bool>? KeyChanged;

    public RawInputHotkeys()
    {
        var p = new HwndSourceParameters("GVT.RawInput") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        var devices = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = _source.Handle }, // 键盘
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = _source.Handle }, // 鼠标
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("注册 Raw Input 失败，错误码 " + Marshal.GetLastWin32Error());
    }

    /// <summary>配置里的按键名是否与事件名相同（支持 WPF Key 枚举的别名，如 CapsLock/Capital）。</summary>
    public static bool Matches(string configured, string actual)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        if (string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase)) return true;
        return Enum.TryParse<Key>(configured, true, out var a) && Enum.TryParse<Key>(actual, true, out var b) && a == b;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            var body = buffer + (int)headerSize;
            if (header.Type == RIM_TYPEKEYBOARD)
            {
                ushort flags = (ushort)Marshal.ReadInt16(body, 2);
                ushort vkey = (ushort)Marshal.ReadInt16(body, 6);
                if (vkey is 0 or 0xFF) return IntPtr.Zero;
                bool up = (flags & 0x01) != 0; // RI_KEY_BREAK
                var name = KeyInterop.KeyFromVirtualKey(vkey).ToString();
                Report(name, !up);
            }
            else if (header.Type == RIM_TYPEMOUSE)
            {
                ushort b = (ushort)Marshal.ReadInt16(body, 4); // usButtonFlags
                if ((b & 0x0010) != 0) Report("MiddleButton", true);
                if ((b & 0x0020) != 0) Report("MiddleButton", false);
                if ((b & 0x0040) != 0) Report("XButton1", true);
                if ((b & 0x0080) != 0) Report("XButton1", false);
                if ((b & 0x0100) != 0) Report("XButton2", true);
                if ((b & 0x0200) != 0) Report("XButton2", false);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return IntPtr.Zero;
    }

    private void Report(string name, bool down)
    {
        if (down) { if (!_down.Add(name)) return; } // 忽略按住时的自动重复
        else if (!_down.Remove(name)) return;
        KeyChanged?.Invoke(name, down);
    }

    public void Dispose() => _source.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
