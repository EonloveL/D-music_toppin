using System.Text;
using System.Text.RegularExpressions;

namespace MusicToppin;

internal sealed class LyricLine
{
    public TimeSpan Time;
    public string Text = "";
}

/// <summary>LRC 歌词解析：支持一行多个时间戳，纯函数可测。</summary>
internal static class LyricsParser
{
    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;

        lrc = lrc.TrimStart('﻿');

        foreach (var raw in lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = raw;
            var times = new List<TimeSpan>();

            while (text.StartsWith("[", StringComparison.Ordinal))
            {
                var end = text.IndexOf(']');
                if (end < 0) break;

                var tag = text.Substring(1, end - 1);
                text = text.Substring(end + 1);

                TimeSpan t;
                if (TryParseTime(tag, out t)) times.Add(t);
            }

            var content = text.Trim();
            if (content.Length == 0 || times.Count == 0) continue;

            foreach (var t in times)
            {
                lines.Add(new LyricLine { Time = t, Text = content });
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static bool TryParseTime(string tag, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var parts = tag.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var minutes)) return false;

        double seconds;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            return false;
        }

        value = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}

internal sealed class LyricsSnapshot
{
    public string Key = "";
    public string Title = "";
    public string Artist = "";
    public int Version;
    public List<LyricLine> Lines = new List<LyricLine>();
}

/// <summary>当前歌曲的歌词缓存：按 source + id/mid 取一次，切歌重取。</summary>
internal static class LyricsService
{
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, List<LyricLine>> Cache = new Dictionary<string, List<LyricLine>>(StringComparer.Ordinal);

    private static string _key = "";
    private static string _title = "";
    private static string _artist = "";
    private static List<LyricLine> _lines = new List<LyricLine>();
    private static int _version;
    private static string _loadingKey = "";
    private static string _failedKey = "";

    public static void SetSong(string title, string artist, string sourceLabel, string pageUrl)
    {
        SetSongKey(GetKey(sourceLabel, pageUrl), title, artist);
    }

    /// <summary>外部播放器（Spotify / Apple Music / 网易云客户端等）没有应用内直链时按「歌名 + 歌手」搜歌词。</summary>
    public static void SetExternalSong(string title, string artist)
    {
        var key = "ext:" + NormalizeSongText(title) + "|" + NormalizeSongText(artist);
        if (key == "ext:|") return;
        SetSongKey(key, title, artist);
    }

    private static void SetSongKey(string key, string title, string artist)
    {
        lock (Sync)
        {
            // 没有可解析的来源：保持空状态，不要每个播放 tick 都自增版本号
            if (key.Length == 0)
            {
                if (_key.Length == 0)
                {
                    _title = title ?? "";
                    _artist = artist ?? "";
                    return;
                }

                _key = "";
                _title = title ?? "";
                _artist = artist ?? "";
                _lines = new List<LyricLine>();
                _version++;
                return;
            }

            if (key == _key && (_lines.Count > 0 || _failedKey == key || _loadingKey == key))
            {
                _title = title ?? "";
                _artist = artist ?? "";
                return;
            }

            _key = key;
            _title = title ?? "";
            _artist = artist ?? "";
            _lines = new List<LyricLine>();
            _version++;

            List<LyricLine> cached;
            if (Cache.TryGetValue(key, out cached))
            {
                _lines = cached;
                _failedKey = "";
                return;
            }

            // 这首歌已经失败过：不要再每秒重试
            if (_failedKey == key) return;
            if (_loadingKey == key) return;
            _loadingKey = key;
        }

        ThreadPool.QueueUserWorkItem(_ => Load(key));
    }

    public static void Clear()
    {
        lock (Sync)
        {
            if (_key.Length == 0 && _lines.Count == 0 && _loadingKey.Length == 0) return;

            _key = "";
            _title = "";
            _artist = "";
            _lines = new List<LyricLine>();
            _loadingKey = "";
            _version++;
        }
    }

    public static LyricsSnapshot Snapshot()
    {
        lock (Sync)
        {
            return new LyricsSnapshot
            {
                Key = _key,
                Title = _title,
                Artist = _artist,
                Version = _version,
                Lines = new List<LyricLine>(_lines)
            };
        }
    }

    private static void Load(string key)
    {
        List<LyricLine> lines = null;
        try
        {
            if (key.StartsWith("ext:", StringComparison.Ordinal))
                lines = LoadExternal(key);
            else if (key.StartsWith("netease:", StringComparison.Ordinal))
                lines = LyricsParser.Parse(Netease.GetLyric(long.Parse(key.Substring(8))));
            else if (key.StartsWith("qq:", StringComparison.Ordinal))
                lines = LyricsParser.Parse(QQMusic.GetLyric(key.Substring(3)));
        }
        catch (Exception ex)
        {
            Logger.Write("lyrics load: " + ex.Message);
        }

        lock (Sync)
        {
            if (lines != null)
            {
                // 成功但无歌词也缓存，避免无歌词歌曲每秒重试
                if (Cache.Count >= 200) Cache.Clear();
                Cache[key] = lines;
                _failedKey = "";
            }
            else
            {
                // 这次请求失败：记住它，不再反复打接口
                _failedKey = key;
            }

            if (_key == key)
            {
                _lines = lines ?? new List<LyricLine>();
                _version++;
            }

            if (_loadingKey == key) _loadingKey = "";
        }
    }

    private static List<LyricLine> LoadExternal(string key)
    {
        var body = key.Substring(4);
        var split = body.IndexOf('|');
        if (split <= 0) return new List<LyricLine>();
        var title = body.Substring(0, split);
        var artist = body.Substring(split + 1);
        var query = (title + " " + artist).Trim();

        try
        {
            var candidates = Netease.SearchTracks(query);
            var best = PickNetease(candidates, title, artist);
            if (best != null)
            {
                var lines = LyricsParser.Parse(Netease.GetLyric(best.Id));
                if (lines.Count > 0) return lines;
            }
        }
        catch (Exception ex) { Logger.Write("external lyrics netease: " + ex.Message); }

        try
        {
            var candidates = QQMusic.SearchTracks(query);
            var best = PickQQ(candidates, title, artist);
            if (best != null)
            {
                var lines = LyricsParser.Parse(QQMusic.GetLyric(best.Mid));
                if (lines.Count > 0) return lines;
            }
        }
        catch (Exception ex) { Logger.Write("external lyrics qq: " + ex.Message); }

        return new List<LyricLine>();
    }

    private static Netease.Candidate PickNetease(List<Netease.Candidate> candidates, string title, string artist)
    {
        if (candidates == null || candidates.Count == 0) return null;
        var best = candidates[0];
        var bestScore = -1;
        foreach (var candidate in candidates)
        {
            var score = MatchScore(candidate.Name, candidate.Artist, title, artist);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return bestScore >= 0 ? best : null;
    }

    private static QQMusic.Candidate PickQQ(List<QQMusic.Candidate> candidates, string title, string artist)
    {
        if (candidates == null || candidates.Count == 0) return null;
        var best = candidates[0];
        var bestScore = -1;
        foreach (var candidate in candidates)
        {
            var score = MatchScore(candidate.Name, candidate.Artist, title, artist);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return bestScore >= 0 ? best : null;
    }

    private static int MatchScore(string candidateName, string candidateArtist, string title, string artist)
    {
        var cName = NormalizeSongText(candidateName);
        var cArtist = NormalizeSongText(candidateArtist);
        var score = 0;
        if (cName.Length > 0 && cName == title) score += 6;
        else if (cName.Length > 0 && title.Length > 1 && (cName.Contains(title) || title.Contains(cName))) score += 3;
        if (cArtist.Length > 0 && artist.Length > 0 && (cArtist.Contains(artist) || artist.Contains(cArtist))) score += 2;
        if (cName.Length == 0) score -= 1;
        return score;
    }

    /// <summary>去掉括号内容/常见后缀并转小写，用于跨平台歌词匹配。</summary>
    private static string NormalizeSongText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.ToLowerInvariant();
        text = Regex.Replace(text, @"\s*[\-–—]\s*(single|remaster(ed)?|live|radio edit|version|deluxe)\b.*$", "");
        text = Regex.Replace(text, @"\s*[\(\[（【].*?[\)\]）】]", "");
        text = Regex.Replace(text, @"[^\p{L}\p{N}]+", "");
        return text.Trim();
    }

    private static string GetKey(string sourceLabel, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return "";

        if (!string.IsNullOrWhiteSpace(sourceLabel) &&
            sourceLabel.IndexOf("网易云", StringComparison.Ordinal) >= 0)
        {
            var m = Regex.Match(pageUrl, @"[?&]id=(\d+)");
            if (m.Success) return "netease:" + m.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(sourceLabel) &&
            sourceLabel.IndexOf("QQ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var idx = pageUrl.IndexOf("songDetail/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var tail = pageUrl.Substring(idx + "songDetail/".Length);
                var end = tail.IndexOfAny(new[] { '?', '#', '/' });
                var mid = end >= 0 ? tail.Substring(0, end) : tail;
                if (mid.Length > 0) return "qq:" + mid;
            }
        }

        return "";
    }
}
