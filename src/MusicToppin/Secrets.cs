using System.Security.Cryptography;
using System.Text;

namespace MusicToppin;

/// <summary>
/// 注册表里的登录凭据（网易云 MUSIC_U / QQ qm_keyst）用 DPAPI 加密，只有当前 Windows 用户能解。
/// 旧版本写的明文会在读取时原样返回，下次保存自动升级成加密格式。
/// </summary>
internal static class Secrets
{
    private const string Prefix = "dpapi:";

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

        try
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // 宁可让用户重新登录，也不要把 cookie 明文写进注册表
            Logger.Write("secret protect: " + ex.Message);
            return "";
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;   // 兼容旧明文

        try
        {
            var encrypted = Convert.FromBase64String(value.Substring(Prefix.Length));
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Logger.Write("secret unprotect: " + ex.Message);
            return "";
        }
    }
}
