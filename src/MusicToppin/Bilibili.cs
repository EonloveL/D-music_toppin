using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicToppin;

/// <summary>
/// B 站取流：view 拿 cid/标题/时长 → playurl(fnval=16) 拿 DASH 音频直链。
/// 直链有热链保护，播放时必须带 Referer，由播放器注入。
/// </summary>
internal static class Bilibili
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>纯净视频窗口用：直接给一个 MP4（durl）直链，避免 B 站 iframe 的播放器 UI。</summary>
    internal sealed class VideoStream
    {
        public string Url = "";
        public string Title = "";
        public string Artist = "";
        public TimeSpan Duration;
    }

    public static VideoStream ResolveVideoStream(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return null;

        var viewJson = Get("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid);
        if (viewJson == null) return null;
        if (JsonNumber(viewJson, "\"code\":") != 0) return null;

        var cid = JsonNumber(viewJson, "\"cid\":");
        if (cid <= 0) return null;

        var title = StripTag(JsonString(viewJson, "\"title\":"));
        var owner = JsonString(viewJson, "\"name\":");
        var duration = JsonNumber(viewJson, "\"duration\":");

        // fnval=1 返回 durl（音视频一体的 MP4），游客也能拿到 360/480p，页面用 <video> 直接播，没有任何 B 站 UI
        var playJson = Get("https://api.bilibili.com/x/player/playurl?bvid=" + bvid + "&cid=" + cid +
                           "&fnval=1&fnver=0&fourk=0&qn=64&platform=html5&high_quality=1");
        if (playJson == null) return null;
        if (JsonNumber(playJson, "\"code\":") != 0) return null;

        var url = PickFirstDurl(playJson);
        if (string.IsNullOrEmpty(url)) return null;

        return new VideoStream
        {
            Url = url,
            Title = title,
            Artist = owner,
            Duration = TimeSpan.FromSeconds(duration > 0 ? duration : 0)
        };
    }

    private static string PickFirstDurl(string json)
    {
        // 优先用 JSON 解析器：B 站会把 & 转义成 \u0026，手写截取很容易得到坏 URL
        try
        {
            var root = Json.Parse(json);
            var data = root != null ? root["data"] : null;
            var durl = data != null ? data["durl"] : null;
            if (durl != null && durl.Kind == JsonKind.Array && durl.Items.Count > 0)
            {
                var url = durl.Items[0]["url"];
                if (url != null && !string.IsNullOrEmpty(url.AsString)) return url.AsString;
            }
        }
        catch { }

        var index = json.IndexOf("\"durl\":[", StringComparison.Ordinal);
        if (index < 0) return null;

        var urlIndex = json.IndexOf("\"url\":\"", index, StringComparison.Ordinal);
        if (urlIndex < 0) return null;

        var start = urlIndex + "\"url\":\"".Length;
        var end = json.IndexOf('"', start);
        if (end <= start) return null;

        return UnescapeJsonString(json.Substring(start, end - start));
    }

    private static string UnescapeJsonString(string text)
    {
        return text.Replace("\\/", "/")
            .Replace("\\u0026", "&")
            .Replace("\\u003d", "=")
            .Replace("\\u003D", "=")
            .Replace("\\u003f", "?")
            .Replace("\\u003F", "?");
    }

    private static readonly Regex BvRegex = new Regex(@"BV[0-9A-Za-z]{10}", RegexOptions.Compiled);

    public static bool TryExtractBvid(string text, out string bvid)
    {
        bvid = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = BvRegex.Match(text);
        if (match.Success)
        {
            bvid = match.Value;
            return true;
        }
        return false;
    }

    /// <summary>b23.tv 短链 → 真实地址 → BV 号。</summary>
    public static string ResolveShortLink(string url)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string bvid;
                if (TryExtractBvid(response.ResponseUri.ToString(), out bvid)) return bvid;
            }
        }
        catch (Exception ex)
        {
            Logger.Write("b23 resolve: " + ex.Message);
        }
        return null;
    }

    /// <summary>解析 BV 号 → 可播放音频。</summary>
    public static ResolvedSong Resolve(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return null;

        var viewJson = Get("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid);
        if (viewJson == null) return null;

        if (JsonNumber(viewJson, "\"code\":") != 0) return null;

        var title = StripTag(JsonString(viewJson, "\"title\":"));
        var owner = JsonString(viewJson, "\"name\":");
        var cid = JsonNumber(viewJson, "\"cid\":");
        var durationSeconds = JsonNumber(viewJson, "\"duration\":");
        if (cid <= 0) return null;

        var playJson = Get("https://api.bilibili.com/x/player/playurl?bvid=" + bvid + "&cid=" + cid + "&fnval=16&fourk=1");
        if (playJson == null) return null;
        if (JsonNumber(playJson, "\"code\":") != 0) return null;

        var audio = PickBestAudio(playJson);
        if (audio == null) return null;

        return new ResolvedSong
        {
            Title = title.Length > 0 ? title : bvid,
            Artist = owner,
            AudioUrl = audio[0],
            BackupUrl = audio.Length > 1 ? audio[1] : "",
            Duration = durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : TimeSpan.Zero,
            PageUrl = "https://www.bilibili.com/video/" + bvid
        };
    }

    /// <summary>在 dash.audio 数组里挑码率最高的一条，返回 [baseUrl, backupUrl?]。</summary>
    private static string[] PickBestAudio(string json)
    {
        var audioIndex = json.IndexOf("\"audio\":[", StringComparison.Ordinal);
        if (audioIndex < 0) return null;

        // 粗略按对象切分，取所有 baseUrl，配合 bandwidth 选最大
        var segment = json.Substring(audioIndex);
        long bestBandwidth = -1;
        string bestUrl = null;
        string bestBackup = null;

        var position = 0;
        while (true)
        {
            var objStart = segment.IndexOf("\"baseUrl\":\"", position, StringComparison.Ordinal);
            if (objStart < 0) break;

            var urlStart = objStart + "\"baseUrl\":\"".Length;
            var urlEnd = segment.IndexOf('"', urlStart);
            if (urlEnd < 0) break;

            var url = segment.Substring(urlStart, urlEnd - urlStart).Replace("\\u0026", "&").Replace("\\/", "/");

            var bandwidth = JsonNumber(segment, "\"bandwidth\":", objStart);
            var backup = "";
            var backupIndex = segment.IndexOf("\"backupUrl\":[\"", objStart, StringComparison.Ordinal);
            if (backupIndex > 0 && backupIndex - objStart < 4000)
            {
                var bStart = backupIndex + "\"backupUrl\":[\"".Length;
                var bEnd = segment.IndexOf('"', bStart);
                if (bEnd > bStart) backup = segment.Substring(bStart, bEnd - bStart).Replace("\\u0026", "&").Replace("\\/", "/");
            }

            if (bandwidth > bestBandwidth)
            {
                bestBandwidth = bandwidth;
                bestUrl = url;
                bestBackup = backup;
            }

            position = urlEnd;
        }

        if (bestUrl == null) return null;
        return new[] { bestUrl, bestBackup ?? "" };
    }

    private static string StripTag(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        return title.Replace("<em class=\"keyword\">", "").Replace("</em>", "").Trim();
    }

    private static long JsonNumber(string json, string key, int startAt = 0)
    {
        var index = json.IndexOf(key, startAt, StringComparison.Ordinal);
        if (index < 0) return -1;
        index += key.Length;
        var end = index;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        long value;
        return long.TryParse(json.Substring(index, end - index), out value) ? value : -1;
    }

    private static string JsonString(string json, string key)
    {
        var index = json.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return "";
        index += key.Length;
        if (index >= json.Length || json[index] != '"') return "";
        index++;
        var end = index;
        while (end < json.Length && json[end] != '"')
        {
            if (json[end] == '\\') end++;
            end++;
        }
        if (end >= json.Length) return "";
        return Regex.Unescape(json.Substring(index, end - index));
    }

    private static string Get(string url)
    {
        try
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Referer = "https://www.bilibili.com";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 12000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Logger.Write("bilibili api: " + ex.Message);
            return null;
        }
    }
}
