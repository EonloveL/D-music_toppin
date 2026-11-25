using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicToppin;

/// <summary>
/// 从「网易云音乐」官方客户端读取已登录的 cookie（MUSIC_U / __csrf / NMTID），
/// 让用户不用手动复制 cookie：客户端登录过，点歌就自动用他的账号（会员曲也能放）。
///
/// 客户端是 CEF（Chromium）内核，cookie 存在：
///   %LOCALAPPDATA%\Netease\CloudMusic\webapp*xx*\Cookies        （SQLite）
///   %LOCALAPPDATA%\Netease\CloudMusic\webapp*xx*\LocalPrefs.json （os_crypt.encrypted_key）
/// 加密方式：DPAPI 包住 32 字节 AES 密钥，cookie 用 AES-256-GCM（v10 + nonce + 密文 + tag）。
/// 读取过程完全在本机，不上传；读不到就返回 null，由调用方回退到「应用内登录」。
/// </summary>
internal static class NeteaseClient
{
    private const string ClientFolder = "CloudMusic";

    public static bool IsRunning
    {
        get
        {
            try { return Process.GetProcessesByName("cloudmusic").Length > 0; }
            catch { return false; }
        }
    }

    public static bool IsInstalled
    {
        get
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Netease", ClientFolder);
                return Directory.Exists(root) && FindProfile(root) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>客户端存放 cookie 的那个配置目录（找不到返回空串）。</summary>
    public static string ProfileFolder
    {
        get
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Netease", ClientFolder);
                return FindProfile(root) ?? "";
            }
            catch { return ""; }
        }
    }

    /// <summary>返回可直接放进请求头的 cookie 串；失败返回 null。</summary>
    public static string ImportCookie()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Netease", ClientFolder);
            var profile = FindProfile(root);
            if (profile == null)
            {
                Logger.Write("netease client: profile not found");
                return null;
            }

            var prefsPath = Path.Combine(profile, "LocalPrefs.json");
            var cookiesPath = Path.Combine(profile, "Cookies");
            if (!File.Exists(prefsPath) || !File.Exists(cookiesPath))
            {
                Logger.Write("netease client: prefs/cookies missing");
                return null;
            }

            var key = ReadAesKey(File.ReadAllText(prefsPath));
            if (key == null) return null;

            var tempDb = Path.Combine(Path.GetTempPath(), "musictoppin-netease-cookies.db");
            File.Copy(cookiesPath, tempDb, true);

            var cookies = ReadCookies(tempDb, key);
            try { File.Delete(tempDb); } catch { }

            if (cookies == null || !cookies.ContainsKey("MUSIC_U"))
            {
                Logger.Write("netease client: MUSIC_U not found");
                return null;
            }

            var parts = new List<string>();
            foreach (var name in new[] { "MUSIC_U", "__csrf", "NMTID" })
            {
                string value;
                if (cookies.TryGetValue(name, out value) && !string.IsNullOrEmpty(value))
                {
                    parts.Add(name + "=" + value);
                }
            }

            Logger.Write("netease client: cookie imported");
            return string.Join("; ", parts);
        }
        catch (Exception ex)
        {
            Logger.Write("netease client: " + ex.Message);
            return null;
        }
    }

    /// <summary>客户端目录下可能有多个 webapp 配置目录，挑一个有 LocalPrefs + Cookies 的。</summary>
    private static string FindProfile(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;

            foreach (var dir in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "LocalPrefs.json")) &&
                    File.Exists(Path.Combine(dir, "Cookies")) &&
                    File.ReadAllText(Path.Combine(dir, "LocalPrefs.json")).Contains("os_crypt"))
                {
                    return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Write("netease client scan: " + ex.Message);
        }
        return null;
    }

    private static byte[] ReadAesKey(string prefsJson)
    {
        var match = Regex.Match(prefsJson, "\"encrypted_key\"\\s*:\\s*\"([^\"]+)\"");
        if (!match.Success) return null;

        var wrapped = Convert.FromBase64String(match.Groups[1].Value);
        if (wrapped.Length <= 5) return null;

        // 开头 5 字节是 "DPAPI"
        var key = DpapiUnprotect(wrapped, 5);
        if (key == null || key.Length != 32)
        {
            Logger.Write("netease client: unexpected key length " + (key == null ? -1 : key.Length));
            return null;
        }
        return key;
    }

    private static Dictionary<string, string> ReadCookies(string dbPath, byte[] key)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Sqlite.Available) return null;

        var rows = Sqlite.Query(dbPath,
            "SELECT name, encrypted_value, value FROM cookies WHERE host_key LIKE '%music.163.com%'", blobColumn: 1);

        foreach (var row in rows)
        {
            var name = row.Text.Length > 0 ? row.Text[0] : null;
            if (string.IsNullOrEmpty(name)) continue;

            var value = row.Blob != null && row.Blob.Length > 0 ? AesGcmDecrypt(key, row.Blob) : null;
            if (string.IsNullOrEmpty(value) && row.Text.Length > 2) value = row.Text[2];
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        return result;
    }

    // ---- DPAPI ----

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] DpapiUnprotect(byte[] data, int offset)
    {
        var length = data.Length - offset;
        var input = new DataBlob { cbData = length, pbData = Marshal.AllocHGlobal(length) };
        Marshal.Copy(data, offset, input.pbData, length);

        var entropy = new DataBlob { cbData = 0, pbData = IntPtr.Zero };
        DataBlob output;

        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out output))
            {
                Logger.Write("netease client: dpapi " + Marshal.GetLastWin32Error());
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            LocalFree(output.pbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    // ---- AES-256-GCM（.NET Framework 4.8 没有 AesGcm，走 BCrypt）----

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthInfo
    {
        public int cbSize;
        public int dwInfoVersion;
        public IntPtr pbNonce;
        public int cbNonce;
        public IntPtr pbAuthData;
        public int cbAuthData;
        public IntPtr pbTag;
        public int cbTag;
        public IntPtr pbMacContext;
        public int cbMacContext;
        public int cbAAD;
        public long cbData;
        public int dwFlags;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string algId, string impl, int flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptSetProperty(IntPtr obj, string property, byte[] input, int inputLength, int flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptGenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject,
        int keyObjectLength, byte[] secret, int secretLength, int flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputLength, ref AuthInfo paddingInfo,
        byte[] iv, int ivLength, byte[] output, int outputLength, out int result, int flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptDestroyKey(IntPtr key);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);

    private static string AesGcmDecrypt(byte[] key, byte[] blob)
    {
        if (blob.Length < 31 || blob[0] != (byte)'v' || blob[1] != (byte)'1' || blob[2] != (byte)'0') return null;

        var nonce = new byte[12];
        Buffer.BlockCopy(blob, 3, nonce, 0, 12);

        var cipherLength = blob.Length - 31;
        var cipher = new byte[cipherLength];
        Buffer.BlockCopy(blob, 15, cipher, 0, cipherLength);

        var tag = new byte[16];
        Buffer.BlockCopy(blob, blob.Length - 16, tag, 0, 16);

        IntPtr algorithm;
        if (BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0) != 0) return null;

        try
        {
            var mode = Encoding.Unicode.GetBytes("ChainingModeGCM");
            if (BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0) != 0) return null;

            IntPtr aesKey;
            if (BCryptGenerateSymmetricKey(algorithm, out aesKey, IntPtr.Zero, 0, key, key.Length, 0) != 0) return null;

            try
            {
                var output = new byte[cipherLength];
                var nonceHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                var tagHandle = GCHandle.Alloc(tag, GCHandleType.Pinned);

                var info = new AuthInfo
                {
                    cbSize = Marshal.SizeOf(typeof(AuthInfo)),
                    dwInfoVersion = 1,
                    pbNonce = nonceHandle.AddrOfPinnedObject(),
                    cbNonce = nonce.Length,
                    pbTag = tagHandle.AddrOfPinnedObject(),
                    cbTag = tag.Length
                };

                try
                {
                    int written;
                    if (BCryptDecrypt(aesKey, cipher, cipher.Length, ref info, null, 0, output, output.Length, out written, 0) != 0)
                    {
                        return null;
                    }

                    var plain = new byte[written];
                    Buffer.BlockCopy(output, 0, plain, 0, written);
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    nonceHandle.Free();
                    tagHandle.Free();
                }
            }
            finally
            {
                BCryptDestroyKey(aesKey);
            }
        }
        finally
        {
            BCryptCloseAlgorithmProvider(algorithm, 0);
        }
    }

}