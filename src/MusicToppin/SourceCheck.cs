using Microsoft.Win32;

namespace MusicToppin;

/// <summary>一个音乐客户端的安装/运行/登录状态（对应参考工具「播放器检查」那张表）。</summary>
internal sealed class ClientStatus
{
    public string Name = "";
    public string InstallPath = "";
    public string ExePath = "";
    public bool Installed;
    public bool Running;
    public string LoginText = "未知";
}

/// <summary>
/// 检测本机音乐客户端：已安装 / 运行中 / 是否登录（网易云、QQ 音乐；酷狗按需求先移除）。
///
/// 登录态信号（本地记录为主，不猜密码）：
///   网易云  —— 客户端 CEF 的 cookie（MUSIC_U），NeteaseClient 负责解密；检查昵称时会联网
///   QQ 音乐 —— 内嵌 chromium 的 cookie（pt2gguin），兼容 ComData\qmcomdata.ini
/// </summary>
internal static class SourceCheck
{
    public static List<ClientStatus> CheckAll()
    {
        return new List<ClientStatus>
        {
            CheckNetease(),
            CheckQQMusic()
        };
    }

    /// <summary>QQ 音乐客户端是否已登录（读本地 cookie 库，用于自动路由）。</summary>
    public static bool IsQQMusicClientLoggedIn()
    {
        try { return CheckQQMusic().LoginText.StartsWith("已登录", StringComparison.Ordinal); }
        catch { return false; }
    }

    public static string Describe()
    {
        var lines = new List<string>();
        foreach (var status in CheckAll())
        {
            lines.Add(status.Name + "：" + StateText(status) + "（登录：" + status.LoginText + "）");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string StateText(ClientStatus status)
    {
        return (status.Installed ? "已安装" : "未安装") + " · " + (status.Running ? "运行中" : "未运行");
    }

    // ---- 网易云 ----

    private static ClientStatus CheckNetease()
    {
        var status = new ClientStatus { Name = "网易云音乐" };

        status.ExePath = FindProcessPath("cloudmusic") ?? FindExe(
            @"C:\Program Files\Netease\CloudMusic\cloudmusic.exe",
            @"C:\Program Files (x86)\Netease\CloudMusic\cloudmusic.exe");

        status.InstallPath = NeteaseClient.ProfileFolder;
        status.Installed = status.ExePath.Length > 0 || status.InstallPath.Length > 0;
        status.Running = NeteaseClient.IsRunning;

        if (!status.Installed)
        {
            status.LoginText = "未安装";
            return status;
        }

        var cookie = NeteaseClient.ImportCookie();
        if (string.IsNullOrEmpty(cookie))
        {
            status.LoginText = "未登录（客户端里登录后自动同步）";
            return status;
        }

        var nickname = Netease.CheckAccount(cookie);
        status.LoginText = string.IsNullOrEmpty(nickname) ? "已登录（会话已读取）" : "已登录（" + nickname + "）";
        return status;
    }

    // ---- QQ 音乐 ----

    private static ClientStatus CheckQQMusic()
    {
        var status = new ClientStatus { Name = "QQ 音乐" };

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "Tencent", "QQMusic");

        status.InstallPath = dataDir;
        status.ExePath = FindProcessPath("QQMusic") ?? FindExe(
            Path.Combine(localAppData, @"Tencent\QQMusic\QQMusic.exe"),
            @"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe",
            @"C:\Program Files\Tencent\QQMusic\QQMusic.exe");

        status.Installed = Directory.Exists(dataDir) || status.ExePath.Length > 0;
        status.Running = Process.GetProcessesByName("QQMusic").Length > 0;

        if (!status.Installed)
        {
            status.LoginText = "未安装";
            return status;
        }

        // 客户端把当前登录账号明文写在 ComData\qmcomdata.ini 的 [SectQMAPI] uin=
        var comData = Path.Combine(dataDir, "ComData", "qmcomdata.ini");
        var uinText = IniFile.Read(comData, "SectQMAPI", "uin");
        long uin;
        if (long.TryParse(uinText, out uin) && uin > 0)
        {
            status.LoginText = "已登录（账号 " + uin + "）";
            return status;
        }

        // 内嵌 Chromium 的 cookie 库里存着 QQ 登录态（pt2gguin=o<QQ号>）
        var cookieAccount = FindQQMusicAccountFromCookies(dataDir);
        if (cookieAccount.Length > 0)
        {
            status.LoginText = cookieAccount == "-" ? "已登录" : "已登录（账号 " + cookieAccount + "）";
            return status;
        }

        // 老版本没有 cookie 库时，退回按账号目录判断
        var account = FindQQMusicAccount(dataDir);
        status.LoginText = account.Length > 0 ? "已登录（账号 " + account + "）" : "未登录";
        return status;
    }

    /// <summary>QQ 音乐的 qmbrowser（CEF）把登录 cookie 存在 WebkitCache\*\Cookies。
    /// 有 pt2gguin（值形如 o123456）或 uin / qm_keyst 就算登录。</summary>
    private static string FindQQMusicAccountFromCookies(string dataDir)
    {
        try
        {
            var cacheRoot = Path.Combine(dataDir, "QQMusicCache", "WebkitCache");
            if (!Directory.Exists(cacheRoot)) return "";

            foreach (var profile in Directory.GetDirectories(cacheRoot))
            {
                var cookiesPath = Path.Combine(profile, "Cookies");
                if (!File.Exists(cookiesPath)) continue;

                var temp = Path.Combine(Path.GetTempPath(), "musictoppin-qqmusic-cookies.db");
                try { File.Copy(cookiesPath, temp, true); }
                catch { continue; }

                List<SqliteRow> rows;
                try
                {
                    rows = Sqlite.Query(temp, "SELECT name, value FROM cookies");
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }

                var uin = "";
                var hasKey = false;

                foreach (var row in rows)
                {
                    if (row.Text.Length < 2) continue;
                    var name = row.Text[0] ?? "";
                    var value = row.Text[1] ?? "";
                    if (value.Length == 0) continue;

                    if (name == "pt2gguin" || name == "uin" || name == "p_uin")
                    {
                        var normalized = QQMusic.NormalizeUin(value);
                        if (normalized.Length > 0 && normalized != "0") uin = normalized;
                    }
                    else if (name == "qm_keyst" || name == "qqmusic_key") hasKey = true;
                }

                if (uin.Length > 0) return uin;
                if (hasKey) return "-";
            }
        }
        catch (Exception ex)
        {
            Logger.Write("sourcecheck qq cookies: " + ex.Message);
        }
        return "";
    }

    /// <summary>QQ 音乐按 QQ 号建用户目录：UserData\&lt;QQ号&gt;\…，有就是登录过。</summary>
    private static string FindQQMusicAccount(string dataDir)
    {
        try
        {
            var userData = Path.Combine(dataDir, "UserData");
            if (!Directory.Exists(userData)) return "";

            foreach (var dir in Directory.GetDirectories(userData))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 5) continue;

                var digits = true;
                foreach (var c in name)
                {
                    if (c < '0' || c > '9') { digits = false; break; }
                }
                if (digits) return name;
            }
        }
        catch (Exception ex)
        {
            Logger.Write("sourcecheck qq: " + ex.Message);
        }
        return "";
    }

    // ---- 小工具 ----

    private static string FindProcessPath(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule != null ? process.MainModule.FileName : null;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    private static string FindExe(params string[] candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        return "";
    }

    private static string FirstExisting(params string[] candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (Directory.Exists(path)) return path;
            }
            catch { }
        }
        return "";
    }
}
