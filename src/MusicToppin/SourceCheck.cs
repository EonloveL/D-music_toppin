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
/// 检测本机音乐客户端：已安装 / 运行中 / 是否登录。
///
/// 登录态信号（都用客户端自己的本地记录，不联网、不猜密码）：
///   网易云  —— 客户端 CEF 的 cookie（MUSIC_U），NeteaseClient 负责解密
///   QQ 音乐 —— %APPDATA%\Tencent\QQMusic\ComData\qmcomdata.ini 的 [SectQMAPI] uin=
///              （老版本没有这个文件时，退回看 UserData\&lt;QQ号&gt; 账号目录）
///   酷狗   —— HKCU\Software\KuGoo3 的 LastLoginUserID / UserID，或 kugou.ini
/// </summary>
internal static class SourceCheck
{
    public static List<ClientStatus> CheckAll()
    {
        return new List<ClientStatus>
        {
            CheckNetease(),
            CheckQQMusic(),
            CheckKugou()
        };
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

        // 老版本没有 qmcomdata.ini，退回按账号目录判断
        var account = FindQQMusicAccount(dataDir);
        status.LoginText = account.Length > 0 ? "已登录（账号 " + account + "）" : "未登录";
        return status;
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

    // ---- 酷狗 ----

    private static ClientStatus CheckKugou()
    {
        var status = new ClientStatus { Name = "酷狗音乐" };

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        status.InstallPath = FirstExisting(
            Path.Combine(appData, "KuGou8"),
            Path.Combine(appData, "Kugou8"),
            Path.Combine(localAppData, "KuGou"),
            @"C:\Program Files (x86)\KuGou",
            @"C:\Program Files\KuGou");

        status.ExePath = FindProcessPath("KuGou") ?? FindProcessPath("KGMusic") ?? FindExe(
            @"C:\Program Files (x86)\KuGou\KuGou.exe",
            @"C:\Program Files\KuGou\KuGou.exe");

        status.Installed = status.InstallPath.Length > 0 || status.ExePath.Length > 0;
        status.Running = Process.GetProcessesByName("KuGou").Length > 0 || Process.GetProcessesByName("KGMusic").Length > 0;

        if (!status.Installed)
        {
            status.LoginText = "未安装";
            return status;
        }

        var uid = FindKugouAccount();
        if (uid > 0)
        {
            status.LoginText = "已登录（账号 " + uid + "）";
            return status;
        }

        status.LoginText = "未知（没找到登录标记，可在客户端里看一眼）";
        return status;
    }

    /// <summary>酷狗把上次登录的账号写在注册表 / kugou.ini 里。</summary>
    private static long FindKugouAccount()
    {
        var names = new[] { "LastLoginUserID", "UserID", "userid", "LoginUserID", "LastUserID" };

        foreach (var subKey in new[] { @"Software\KuGoo3", @"Software\Kugou", @"Software\KuGou" })
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (key == null) continue;

                    foreach (var name in names)
                    {
                        var value = key.GetValue(name);
                        if (value is int i && i > 0) return i;
                        if (value is long l && l > 0) return l;

                        var text = value as string;
                        long parsed;
                        if (!string.IsNullOrEmpty(text) && long.TryParse(text, out parsed) && parsed > 0) return parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("sourcecheck kugou: " + ex.Message);
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var ini in new[]
                 {
                     Path.Combine(appData, "Kugou8", "kugou.ini"),
                     Path.Combine(appData, "KuGou", "kugou.ini"),
                     Path.Combine(appData, "KuGou8", "KuGou.ini")
                 })
        {
            foreach (var name in names)
            {
                long parsed;
                if (long.TryParse(IniFile.Read(ini, "", name), out parsed) && parsed > 0) return parsed;
            }
        }

        return 0;
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
