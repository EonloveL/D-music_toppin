using System.Text;

namespace MusicToppin;

/// <summary>
/// QQ 音乐取流。2026-11 实测：
///   搜索：c.y.qq.com/soso/fcgi-bin/client_search_cp（匿名可用）
///   取流：u.y.qq.com/cgi-bin/musicu.fcg → vkey.GetVkeyServer
///         匿名（uin=0）拿不到 purl，必须先在「曲源 → QQ 音乐登录」里登录
///         （登录后 cookie 里的 uin + qm_keyst / qqmusic_key 一起带上）
/// </summary>
internal static class QQMusic
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string Referer = "https://y.qq.com/";

    /// <summary>登录后的 cookie 串（空 = 游客，只能搜索不能播放）。</summary>
    public static string Cookie = "";

    /// <summary>登录后的 QQ 号（vkey 接口要用，不带 o0 前缀）。</summary>
    public static string Uin = "";

    private const int MaxCandidates = 3;

    internal sealed class Candidate
    {
        public string Mid = "";
        public string Name = "";
        public string Artist = "";
        public TimeSpan Duration = TimeSpan.Zero;
    }

    public static bool IsLoggedIn
    {
        get { return !string.IsNullOrWhiteSpace(Cookie) && !string.IsNullOrWhiteSpace(Uin) && Uin != "0"; }
    }

    /// <summary>只登录了 QQ 音乐客户端（取流 key 不在应用内，但路由可以优先走 QQ）。</summary>
    public static bool ClientLoggedIn;

    /// <summary>自动路由认为「QQ 可用」：应用内登录或客户端已登录。</summary>
    public static bool Available
    {
        get { return IsLoggedIn || ClientLoggedIn; }
    }

    public static ResolvedSong Resolve(SongRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Query)) return null;

        var candidates = Search(request.Query.Trim());
        if (candidates.Count == 0)
        {
            Logger.Write("qq: no candidate for " + request.Query);
            return null;
        }

        if (!IsLoggedIn) Logger.Write("qq: not logged in, vkey will likely be empty");

        var tried = 0;
        foreach (var candidate in candidates)
        {
            if (tried++ >= MaxCandidates) break;

            var url = Vkey(candidate.Mid);
            if (string.IsNullOrEmpty(url))
            {
                Logger.Write("qq: no vkey for " + candidate.Mid + " " + candidate.Name);
                continue;
            }

            return new ResolvedSong
            {
                Title = candidate.Name,
                Artist = candidate.Artist,
                AudioUrl = url,
                Duration = candidate.Duration,
                SourceLabel = "QQ音乐",
                PageUrl = "https://y.qq.com/n/ryqq/songDetail/" + candidate.Mid
            };
        }

        return null;
    }

    // ---- 搜索 ----

    private static List<Candidate> Search(string keyword)
    {
        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&inCharset=utf8&outCharset=utf-8" +
                  "&cr=1&g_tk=5381&t=0&aggr=1&lossless=0&flag_qc=0&p=1&n=10&w=" +
                  Uri.EscapeDataString(keyword);

        return ParseSearch(Get(url));
    }

    /// <summary>vkey 拿播放地址；成功返回完整 URL（sip 前缀 + purl）。</summary>
    private static string Vkey(string mid)
    {
        if (string.IsNullOrWhiteSpace(mid)) return null;

        var uin = string.IsNullOrWhiteSpace(Uin) ? "0" : Uin;
        var guid = Guid.NewGuid().ToString("N").Substring(0, 10);

        var body =
            "{\"req_0\":{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{" +
            "\"guid\":\"" + guid + "\",\"songmid\":[\"" + mid + "\"],\"songtype\":[0]," +
            "\"uin\":\"" + uin + "\",\"loginflag\":1,\"platform\":\"20\"}}," +
            "\"comm\":{\"uin\":\"" + uin + "\",\"format\":\"json\",\"ct\":24,\"cv\":0}}";

        return ParseVkey(Post("https://u.y.qq.com/cgi-bin/musicu.fcg", body));
    }

    // ---- 解析（纯函数，便于离线测试）----

    public static List<Candidate> ParseSearch(string json)
    {
        var list = new List<Candidate>();

        var root = SafeParse(json);
        if (root == null) return list;

        var data = root["data"];
        var song = data != null ? data["song"] : null;
        var songs = song != null ? song["list"] : null;
        if (songs == null || songs.Kind != JsonKind.Array) return list;

        foreach (var item in songs.Items)
        {
            if (item == null) continue;

            var mid = item["songmid"] != null ? item["songmid"].AsString : null;
            if (string.IsNullOrWhiteSpace(mid)) continue;

            var name = item["songname"] != null ? (item["songname"].AsString ?? "") : "";
            var interval = item["interval"] != null ? item["interval"].AsLong : 0;

            var artists = item["singer"];
            var names = new List<string>();
            if (artists != null && artists.Kind == JsonKind.Array)
            {
                foreach (var singer in artists.Items)
                {
                    var value = singer != null && singer["name"] != null ? singer["name"].AsString : null;
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                    if (names.Count >= 2) break;
                }
            }

            list.Add(new Candidate
            {
                Mid = mid,
                Name = name,
                Artist = string.Join(" / ", names),
                Duration = interval > 0 ? TimeSpan.FromSeconds(interval) : TimeSpan.Zero
            });
        }

        return list;
    }

    public static string ParseVkey(string json)
    {
        var root = SafeParse(json);
        if (root == null) return null;

        var req = root["req_0"];
        var data = req != null ? req["data"] : null;
        if (data == null) return null;

        var baseUrl = "";
        var sip = data["sip"];
        if (sip != null && sip.Kind == JsonKind.Array && sip.Count > 0 && sip[0] != null)
        {
            baseUrl = sip[0].AsString ?? "";
        }

        var infos = data["midurlinfo"];
        if (infos == null || infos.Kind != JsonKind.Array || infos.Count == 0) return null;

        var purl = infos[0] != null && infos[0]["purl"] != null ? infos[0]["purl"].AsString : null;
        if (string.IsNullOrWhiteSpace(purl)) return null;

        if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://ws.stream.qqmusic.qq.com/";
        return baseUrl + purl;
    }

    /// <summary>从 QQ 登录 cookie 里拿 uin（cookie 里常写成 o0123456789）。</summary>
    public static string NormalizeUin(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var value = raw.Trim();
        if (value.StartsWith("o", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);

        foreach (var c in value)
        {
            if (c < '0' || c > '9') return "";
        }
        return value;
    }

    private static JsonValue SafeParse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return Json.Parse(json);
        }
        catch (Exception ex)
        {
            Logger.Write("qq json: " + ex.Message);
            return null;
        }
    }

    private static string Get(string url)
    {
        try
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Referer = Referer;
            request.Accept = "*/*";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            request.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;

            using (var response = (System.Net.HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Logger.Write("qq api: " + ex.Message);
            return null;
        }
    }

    private static string Post(string url, string body)
    {
        try
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }

            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.Method = "POST";
            request.UserAgent = UserAgent;
            request.Referer = Referer;
            request.ContentType = "application/json";
            request.Accept = "*/*";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            if (!string.IsNullOrWhiteSpace(Cookie)) request.Headers["Cookie"] = Cookie;

            var payload = Encoding.UTF8.GetBytes(body);
            request.ContentLength = payload.Length;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }

            using (var response = (System.Net.HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Logger.Write("qq vkey: " + ex.Message);
            return null;
        }
    }
}
