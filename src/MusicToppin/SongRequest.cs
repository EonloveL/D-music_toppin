using System.Text.RegularExpressions;

namespace MusicToppin;

internal enum SongSource
{
    Auto = 0,        // 未指定前缀，默认网易云
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
/// 点歌指令解析。格式（和主流点歌机一致）：
///   点歌 起风了                 → 默认网易云关键词搜索
///   点歌 wy 起风了              → 网易云
///   点歌 qq 起风了              → QQ 音乐
///   点歌 am 起风了              → Apple Music
///   点歌 id:123456              → 网易云歌曲 ID
///   点歌 BV1xx411c7mD           → B站视频点播
/// 支持繁体「點歌」，必须以「点歌 + 空格」开头，纯表情/纯空白不处理。
/// </summary>
internal static class SongRequestParser
{
    private static readonly Regex BvPattern = new Regex(@"^bv[0-9a-z]{10}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShortLinkPattern = new Regex(@"^https?://b23\.tv/\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdPattern = new Regex(@"^id[:：](\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string text, string requester, long requesterUid, out SongRequest request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("点歌", StringComparison.Ordinal) &&
            !trimmed.StartsWith("點歌", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Length <= 2) return false;

        var rest = trimmed.Substring(2);
        // 必须以空白分隔，避免「点歌姬」这类词被误判
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0])) return false;

        rest = rest.Trim();
        if (rest.Length == 0) return false;

        var source = SongSource.Auto;
        var query = rest;
        var isId = false;

        var space = rest.IndexOfAny(new[] { ' ', '\t', '\u3000' });
        var head = space > 0 ? rest.Substring(0, space) : rest;
        var tail = space > 0 ? rest.Substring(space + 1).Trim() : "";

        var headLower = head.ToLowerInvariant();

        // 前缀：wy / qq / am / 网易云 / QQ音乐 / Apple Music
        if (tail.Length > 0)
        {
            switch (headLower)
            {
                case "wy":
                case "wyy":
                case "网易":
                case "网易云":
                    source = SongSource.Netease;
                    query = tail;
                    break;
                case "qq":
                case "qqmusic":
                case "qq音乐":
                    source = SongSource.QQ;
                    query = tail;
                    break;
                case "am":
                case "apple":
                case "applemusic":
                case "apple music":
                    source = SongSource.AppleMusic;
                    query = tail;
                    break;
                case "bv":
                case "bili":
                case "bilibili":
                case "b站":
                    source = SongSource.Bilibili;
                    query = tail;
                    break;
                default:
                    break;
            }
        }

        // 前缀后面直接跟 BV 号 / b23 短链：无论前缀是什么都当视频点播
        if (tail.Length > 0 && (BvPattern.IsMatch(tail) || ShortLinkPattern.IsMatch(tail)))
        {
            request = Build(text, SongSource.Bilibili, tail, requester, requesterUid, isId: false, isVideo: true);
            return true;
        }

        // BV 号 / b23 短链：无论有没有前缀都当视频点播
        if (BvPattern.IsMatch(query) || BvPattern.IsMatch(head))
        {
            source = SongSource.Bilibili;
            query = BvPattern.IsMatch(head) ? head : query;
            request = Build(text, source, query, requester, requesterUid, isId: false, isVideo: true);
            return true;
        }

        if (ShortLinkPattern.IsMatch(query) || ShortLinkPattern.IsMatch(head))
        {
            source = SongSource.Bilibili;
            query = ShortLinkPattern.IsMatch(head) ? head : query;
            request = Build(text, source, query, requester, requesterUid, isId: false, isVideo: true);
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

        request = Build(text, source, query, requester, requesterUid, isId, isVideo: false);
        return true;
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
