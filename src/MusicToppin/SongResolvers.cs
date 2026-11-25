namespace MusicToppin;

/// <summary>
/// 按来源分派取流器。加一个新平台 = 这里加一个 case，失败只影响单个源。
/// 目前接入：网易云（含默认「点歌 歌名」）、B 站 BV；QQ / Apple Music 待做。
/// </summary>
internal static class SongResolvers
{
    public static ResolvedSong Resolve(SongRequest request)
    {
        if (request == null) return null;

        switch (request.Source)
        {
            case SongSource.Bilibili:
                return ResolveBilibili(request.Query);

            case SongSource.Netease:
            case SongSource.Auto:
                return Netease.Resolve(request);

            case SongSource.QQ:
                return QQMusic.Resolve(request);

            default:
                Logger.Write("resolver: unsupported source " + request.SourceLabel + " (" + request.Source + ")");
                return null;
        }
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
