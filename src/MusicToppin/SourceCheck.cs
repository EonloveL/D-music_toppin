namespace MusicToppin;

/// <summary>一个音乐客户端的安装/运行/登录状态（对应参考工具「播放器检查」那张表）。</summary>
internal sealed class ClientStatus
{
    public string Name = "";
    public string InstallPath = "";
    public bool Installed;
    public bool Running;
    public string LoginText = "未知";
}

/// <summary>
/// 检测本机音乐客户端：已安装 / 运行中 / 是否登录。
/// 网易云的登录状态可以直接读客户端 cookie 判断；QQ / 酷狗暂不读，只报安装与运行。
/// </summary>
internal static class SourceCheck
{
    public static List<ClientStatus> CheckAll()
    {
        var list = new List<ClientStatus>
        {
            CheckNetease(),
            CheckQQMusic(),
            CheckKugou()
        };
        return list;
    }

    public static string Describe()
    {
        var lines = new List<string>();
        foreach (var status in CheckAll())
        {
            var state = new List<string>
            {
                status.Installed ? "已安装" : "未安装",
                status.Running ? "运行中" : "未运行",
                "登录：" + status.LoginText
            };
            lines.Add(status.Name + "：" + string.Join(" · ", state));
            if (status.Installed && !status.Running)
            {
                lines.Add("    已安装但没运行，启动客户端并登录后即可点歌");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static ClientStatus CheckNetease()
    {
        var status = new ClientStatus
        {
            Name = "网易云音乐",
            Installed = NeteaseClient.IsInstalled,
            Running = NeteaseClient.IsRunning
        };

        if (status.Installed)
        {
            status.InstallPath = NeteaseClient.ProfileFolder ?? "";
            var cookie = NeteaseClient.ImportCookie();
            status.LoginText = string.IsNullOrEmpty(cookie) ? "未登录（客户端里登录后会自动同步）" : "已登录（可读客户端会话）";
        }
        else
        {
            status.LoginText = "未安装";
        }

        return status;
    }

    private static ClientStatus CheckQQMusic()
    {
        var status = new ClientStatus { Name = "QQ 音乐" };
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tencent", "QQMusic"),
            @"C:\Program Files (x86)\Tencent\QQMusic",
            @"C:\Program Files\Tencent\QQMusic"
        };

        foreach (var path in candidates)
        {
            if (!Directory.Exists(path)) continue;
            status.Installed = true;
            status.InstallPath = path;
            break;
        }

        status.Running = Process.GetProcessesByName("QQMusic").Length > 0;
        status.LoginText = status.Running ? "未知（用应用内登录）" : "—";
        return status;
    }

    private static ClientStatus CheckKugou()
    {
        var status = new ClientStatus { Name = "酷狗音乐" };
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kugou"),
            @"C:\Program Files (x86)\KuGou",
            @"C:\Program Files\KuGou"
        };

        foreach (var path in candidates)
        {
            if (!Directory.Exists(path)) continue;
            status.Installed = true;
            status.InstallPath = path;
            break;
        }

        status.Running = Process.GetProcessesByName("KuGou").Length > 0;
        status.LoginText = "—";
        return status;
    }
}
