using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using GVT.Core.Pipeline;

namespace GVT.Core.Setup;

/// <summary>
/// VB-Audio 虚拟声卡安装包：官网下载地址、本地目录、Authenticode 签名校验。
/// VB-Audio 的安装包不允许随本软件再分发，所以运行时从官网下载，签名有效且签名者是 VB-Audio 作者 Vincent Burel 才会运行。
/// </summary>
public static class DriverPackages
{
    public sealed record Package(string Part, string Url, string Setup);

    public static readonly Package[] All =
    {
        new("vbcable", "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
            Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe"),
        new("hificable", "https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip", "HiFiCableAsioBridgeSetup.exe"),
    };

    /// <summary>签名者名称里必须出现的字样（两个安装包分别用 “BUREL VINCENT” 和 “Vincent Burel” 签名）。</summary>
    private static readonly string[] TrustedSigners = { "BUREL VINCENT", "Vincent Burel" };

    /// <summary>自己编译时可以把安装包放在程序目录的 drivers 文件夹里，优先使用。</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "drivers");
    public static string DownloadDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameVoiceTranslator", "drivers");

    public static string SetupPath(string dir, Package p) => Path.Combine(dir, p.Part, p.Setup);

    /// <summary>准备好安装包所在目录：优先用程序目录里自带的，否则从官网下载并解压、校验签名。返回目录（其下按 Part 分子目录）。</summary>
    public static async Task<string> PrepareAsync(IReadOnlyList<Package> need, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (need.All(p => File.Exists(SetupPath(BundledDir, p)))) return BundledDir;

        Directory.CreateDirectory(DownloadDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameVoiceTranslator/1.0");
        foreach (var p in need)
        {
            var target = Path.Combine(DownloadDir, p.Part);
            var setup = SetupPath(DownloadDir, p);
            if (File.Exists(setup) && IsTrusted(setup)) continue;

            status?.Report(p.Part == "vbcable" ? "正在下载 VB-CABLE…" : "正在下载 Hi-Fi Cable…");
            var zip = Path.Combine(DownloadDir, p.Part + ".zip");
            await using (var s = await http.GetStreamAsync(p.Url, ct).ConfigureAwait(false))
            await using (var f = File.Create(zip))
                await s.CopyToAsync(f, ct).ConfigureAwait(false);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            ZipFile.ExtractToDirectory(zip, target);
            File.Delete(zip);
            if (!File.Exists(setup)) throw new InvalidDataException($"下载的安装包里没有 {p.Setup}");
            if (!IsTrusted(setup)) throw new UnauthorizedAccessException("下载的安装包签名校验没有通过，已停止安装。");
            Log.Info($"已从 VB-Audio 官网下载 {p.Part}，签名校验通过");
        }
        return DownloadDir;
    }

    /// <summary>Authenticode 签名有效（WinVerifyTrust），且签名者是 VB-Audio 作者。</summary>
    public static bool IsTrusted(string file)
    {
        try
        {
            if (!WinVerifyTrustOk(file)) return false;
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return TrustedSigners.Any(s => cert.Subject.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool WinVerifyTrustOk(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE（离线也能校验）
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwProvFlags = 0x00000010,  // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            int r = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            return r == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
