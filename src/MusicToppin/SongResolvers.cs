namespace MusicToppin;

/// <summary>
/// 按来源分派取流器。加一个新平台 = 这里加一个 case，失败只影响单个源。
/// 目前接入：网易云、QQ 音乐、B 站 BV。
/// 没写前缀的「点歌 歌名」按登录情况自动选平台：只登录了一个就用它，
/// 都登录了就网易云优先、QQ 兜底。
/// </summary>
internal static class SongResolvers
{
    public static ResolvedSong Resolve(SongRequest request)
    {
        if (request == null) return null;

        switch (request.Source)
        {
            case SongSource.Auto:
                return ResolveAuto(request);

            case SongSource.Netease:
                return Netease.Resolve(request);

            case SongSource.QQ:
                return QQMusic.Resolve(request);

            case SongSource.Bilibili:
                return ResolveBilibili(request.Query);

            default:
                Logger.Write("resolver: unsupported source " + request.SourceLabel + " (" + request.Source + ")");
                return null;
        }
    }

    /// <summary>自动模式的尝试顺序（纯函数，方便离线测试）。</summary>
    public static List<SongSource> AutoOrder(bool neteaseLoggedIn, bool qqLoggedIn)
    {
        var order = new List<SongSource>();

        // 只登录了一个平台：先它；另一个只当兜底
        if (qqLoggedIn && !neteaseLoggedIn)
        {
            order.Add(SongSource.QQ);
            order.Add(SongSource.Netease);
            return order;
        }

        // 都登录 / 都没登录：网易云优先（游客也能放免费曲），QQ 兜底
        order.Add(SongSource.Netease);
        order.Add(SongSource.QQ);
        return order;
    }

    private static ResolvedSong ResolveAuto(SongRequest request)
    {
        var order = AutoOrder(Netease.IsLoggedIn, QQMusic.IsLoggedIn);

        foreach (var source in order)
        {
            var song = source == SongSource.QQ ? QQMusic.Resolve(request) : Netease.Resolve(request);
            if (song == null) continue;

            // 让 HUD / 日志显示真正生效的平台
            request.Source = source;
            Logger.Write("resolver: auto -> " + source);
            return song;
        }

        return null;
    }

    private static ResolvedSong ResolveBilibili(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var text = query.Trim();
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            text = Bilibili.ResolveShortLink(text);
            if (string.IsNullOrWhiteSpace(text)) return null;
        }

        return Bilibili.Resolve(text);
    }
}
