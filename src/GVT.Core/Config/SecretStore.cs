using System.Security.Cryptography;
using System.Text;

namespace GVT.Core.Config;

/// <summary>
/// 用 Windows DPAPI（当前用户）加密保存 API Key，不以明文落盘。
/// 也支持环境变量 DASHSCOPE_API_KEY，环境变量优先。
/// </summary>
public static class SecretStore
{
    private static string FilePath => Path.Combine(AppConfig.ConfigDirectory, "dashscope.key");

    public static string? GetApiKey()
    {
        var env = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (!File.Exists(FilePath)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveApiKey(string key)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
    }

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());
}
