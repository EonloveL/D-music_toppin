using System.Text.RegularExpressions;

namespace MusicToppin;

internal enum SongSource
{
    Auto = 0,        // 未指定前缀，按登录情况自动选
    Netease = 1,     // wy
    QQ = 2,          // qq
    AppleMusic = 3,  // am
    Bilibili = 4     // BV 号 / b23 短链
}

/// <summary>一条点歌请求（弹幕解析产物）。</summary>
internal sealed class SongRequest
{
    public string Raw;
    public SongSource Source;
    public string Query;
    public string Requester;
    public long RequesterUid;
    public DateTime ReceivedAtUtc = DateTime.UtcNow;

    /// <summary>query 是平台 id（id:123）而不是关键词。</summary>
    public bool IsId;

    /// <summary>B站视频点播（query 是 BV 号）。</summary>
    public bool IsVideo;

    public string SourceLabel
    {
        get
        {
            switch (Source)
            {
                case SongSource.Netease: return "网易云";
                case SongSource.QQ: return "QQ音乐";
                case SongSource.AppleMusic: return "Apple Music";
                case SongSource.Bilibili: return "B站";
                default: return "网易云";
            }
        }
    }

    /// <summary>用于去重的键：同一来源同一首歌算重复。</summary>
    public string DedupeKey
    {
        get { return ((int)Source) + "|" + (IsVideo ? "v:" : "s:") + (IsId ? "id:" : "") + Query.Trim().ToLowerInvariant(); }
    }
}

/// <summary>
/// 点歌指令解析。两种写法都认：
///   点歌 起风了 / 點歌 qq 夜に駆ける / 点歌 id:123 / 点歌 BV1xx411c7mD / 点歌 b23.tv/xxxx
///   qq I don't care Ed Sheeran    ← 平台前缀直接开头（不带「点歌」）
/// 平台前缀：wy/网易云、qq/qq音乐、am/apple music、bv/bili/b站。
/// 都以「前缀 + 空格 + 内容」分隔，纯表情/纯空白不处理；「点歌姬」这类词不会被误判。
/// </summary>
internal static class SongRequestParser
{
    private static readonly Regex BvPattern = new Regex(@"^bv[0-9a-z]{10}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShortLinkPattern = new Regex(@"^https?://b23\.tv/\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdPattern = new Regex(@"^id[:：](\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly char[] Separators = { ' ', '\t', '\u3000' };

    public static bool TryParse(string text, string requester, long requesterUid, out SongRequest request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        // 1) 「点歌 xxx」/「點歌 xxx」
        if (trimmed.StartsWith("点歌", StringComparison.Ordinal) ||
            trimmed.StartsWith("點歌", StringComparison.Ordinal))
        {
            if (trimmed.Length <= 2) return false;

            var rest = trimmed.Substring(2);

            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            {
                if (IsSeparator(rest[0]))
                {
                    // 「点歌：起风了」「点歌,起风了」
                    rest = rest.Substring(1);
                }
                else if (!LooksLikePlatformHead(rest))
                {
                    // 「点歌姬 你好」这类不能误判
                    return false;
                }
            }

            rest = rest.Trim();
            if (rest.Length == 0) return false;

            return TryBuild(trimmed, rest, SongSource.Auto, requester, requesterUid, out request);
        }

        // 2) 「qq 歌名」这类平台前缀（不带「点歌」）
        return TryParsePlatformCommand(trimmed, requester, requesterUid, out request);
    }

    private static bool TryParsePlatformCommand(string trimmed, string requester, long uid, out SongRequest request)
    {
        request = null;

        var space = trimmed.IndexOfAny(Separators);
        if (space <= 0) return false;

        var head = trimmed.Substring(0, space);
        var tail = trimmed.Substring(space + 1).Trim();
        if (tail.Length == 0) return false;

        SongSource source;
        if (!TryMapPrefix(head.ToLowerInvariant(), out source)) return false;

        return TryBuild(trimmed, tail, source, requester, uid, out request);
    }

    /// <summary>rest 已经去掉「点歌」前缀，可能还带平台前缀。</summary>
    private static bool TryBuild(string raw, string rest, SongSource source, string requester, long uid, out SongRequest request)
    {
        request = null;

        var space = rest.IndexOfAny(Separators);
        var head = space > 0 ? rest.Substring(0, space) : rest;
        var tail = space > 0 ? rest.Substring(space + 1).Trim() : "";
        var headLower = head.ToLowerInvariant();

        var query = rest;
        var isId = false;

        SongSource mapped;
        if (tail.Length > 0 && TryMapPrefix(headLower, out mapped))
        {
            source = mapped;
            query = tail;
        }

        // BV 号 / b23 短链：无论前缀是什么都当视频点播
        var videoCandidate = tail.Length > 0 ? tail : query;
        if (BvPattern.IsMatch(videoCandidate) || ShortLinkPattern.IsMatch(videoCandidate))
        {
            request = Build(raw, SongSource.Bilibili, videoCandidate, requester, uid, isId: false, isVideo: true);
            return true;
        }

        if (BvPattern.IsMatch(head) || ShortLinkPattern.IsMatch(head))
        {
            request = Build(raw, SongSource.Bilibili, head, requester, uid, isId: false, isVideo: true);
            return true;
        }

        // id:123456
        var idMatch = IdPattern.Match(query);
        if (idMatch.Success)
        {
            source = source == SongSource.Auto ? SongSource.Netease : source;
            query = idMatch.Groups[1].Value;
            isId = true;
        }

        if (query.Length == 0) return false;
        if (query.Length > 120) query = query.Substring(0, 120);

        request = Build(raw, source, query, requester, uid, isId, isVideo: false);
        return true;
    }

    /// <summary>「点歌」后面直接跟平台前缀（少了空格）也认：点歌qq 起风了。</summary>
    private static bool LooksLikePlatformHead(string rest)
    {
        var space = rest.IndexOfAny(Separators);
        var head = (space > 0 ? rest.Substring(0, space) : rest).ToLowerInvariant();

        SongSource ignored;
        return TryMapPrefix(head, out ignored);
    }

    private static bool TryMapPrefix(string head, out SongSource source)
    {
        switch (head)
        {
            case "wy":
            case "wyy":
            case "网易":
            case "网易云":
                source = SongSource.Netease;
                return true;

            case "qq":
            case "qqmusic":
            case "qq音乐":
                source = SongSource.QQ;
                return true;

            case "am":
            case "apple":
            case "applemusic":
                source = SongSource.AppleMusic;
                return true;

            case "bv":
            case "bili":
            case "bilibili":
            case "b站":
                source = SongSource.Bilibili;
                return true;

            default:
                source = SongSource.Auto;
                return false;
        }
    }

    private static bool IsSeparator(char c)
    {
        return c == ':' || c == '：' || c == ',' || c == '，' || c == '-' || c == '、';
    }

    private static SongRequest Build(string raw, SongSource source, string query, string requester, long uid, bool isId, bool isVideo)
    {
        return new SongRequest
        {
            Raw = raw,
            Source = source,
            Query = query.Trim(),
            Requester = string.IsNullOrWhiteSpace(requester) ? "未知" : requester.Trim(),
            RequesterUid = uid,
            IsId = isId,
            IsVideo = isVideo
        };
    }
}
