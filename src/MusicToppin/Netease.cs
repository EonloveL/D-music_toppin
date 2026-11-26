using System.Net;
using System.Text;

namespace MusicToppin;

/// <summary>
/// 网易云取流（游客身份）。2026-11 实测：
///   /api/search/get/web       搜索，带 UA + Referer + os=pc Cookie 即可，不需要 weapi 加密
///   /api/song/enhance/player/url?br=320000   拿 320k 直链（有的歌返回 code=404 表示无版权/要登录）
///   /song/media/outer/url     兜底，302 到 128k 直链；无版权时 302 到 music.163.com/404
/// 取不到就返回 null，由队列跳到下一首。
/// </summary>
internal static class Netease
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string Referer = "https://music.163.com/";
    private const string GuestCookie = "os=pc; appver=8.9.70";

    /// <summary>登录后的 cookie（空 = 游客，只能放免费曲）。</summary>
    public static string Cookie = "";

    public static bool IsLoggedIn
    {
        get { return !string.IsNullOrWhiteSpace(Cookie); }
    }

    private const int MaxCandidates = 3;

    private static string CookieHeader()
    {
        return CookieHeader(null);
    }

    private static string CookieHeader(string cookie)
    {
        var extra = string.IsNullOrWhiteSpace(cookie) ? Cookie : cookie;
        return string.IsNullOrWhiteSpace(extra) ? GuestCookie : GuestCookie + "; " + extra;
    }

    /// <summary>用当前 cookie 查账号，返回昵称；未登录/失效返回 null。</summary>
    public static string CheckAccount()
    {
        return CheckAccount(null);
    }

    /// <summary>用指定 cookie 查账号（播放器检查会用另一份会话试探，不污染全局状态）。</summary>
    public static string CheckAccount(string cookie)
    {
        var json = Get("https://music.163.com/api/nuser/account/get", cookie);
        if (json == null) return null;

        var root = SafeParse(json);
        var profile = root != null ? root["profile"] : null;
        var nickname = profile != null && profile["nickname"] != null ? profile["nickname"].AsString : null;
        return string.IsNullOrWhiteSpace(nickname) ? null : nickname;
    }

    /// <summary>搜索/详情里的一条候选。</summary>
    internal sealed class Candidate
    {
        public long Id;
        public string Name = "";
        public string Artist = "";
        public TimeSpan Duration = TimeSpan.Zero;
        public int Fee;
    }

    public static ResolvedSong Resolve(SongRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Query)) return null;

        var candidates = new List<Candidate>();

        long id;
        if (request.IsId && long.TryParse(request.Query.Trim(), out id))
        {
            var single = LookupById(id);
            if (single != null) candidates.Add(single);
        }
        else
        {
            candidates.AddRange(Search(request.Query.Trim()));
        }

        if (candidates.Count == 0)
        {
            Logger.Write("netease: no candidate for " + request.Query);
            return null;
        }

        // 搜索第一名经常是翻唱/直播版，拿不到流就换下一个
        var tried = 0;
        foreach (var candidate in candidates)
        {
            if (tried++ >= MaxCandidates) break;

            var song = TryResolve(candidate);
            if (song != null) return song;

            Logger.Write("netease: unavailable " + candidate.Id + " " + candidate.Name);
        }

        return null;
    }

    // ---- 取流 ----

    public static ResolvedSong ResolveById(long id, string title, string artist, TimeSpan duration)
    {
        if (id <= 0) return null;

        var url = PlayerUrl(id);
        if (string.IsNullOrEmpty(url)) url = OuterUrl(id);
        if (string.IsNullOrEmpty(url)) return null;

        return new ResolvedSong
        {
            Title = title ?? "",
            Artist = artist ?? "",
            AudioUrl = url,
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            SourceLabel = "网易云",
            PageUrl = "https://music.163.com/song?id=" + id
        };
    }

    /// <summary>读取网易云歌单（默认歌单用）；只取元信息，播放时再逐首取流。</summary>
    public static PlaylistData LoadPlaylist(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var json = Get("https://music.163.com/api/v6/playlist/detail?id=" + Uri.EscapeDataString(id) + "&n=1000");
        return ParsePlaylist(json, id);
    }

    public static PlaylistData ParsePlaylist(string json, string id)
    {
        var root = SafeParse(json);
        var playlist = root != null ? root["playlist"] : null;
        if (playlist == null) return null;

        var data = new PlaylistData
        {
            Source = PlaylistSource.Netease,
            Id = id,
            Name = playlist["name"] != null ? (playlist["name"].AsString ?? "") : ""
        };

        var tracks = playlist["tracks"];
        if (tracks != null && tracks.Kind == JsonKind.Array)
        {
            foreach (var song in tracks.Items)
            {
                var candidate = FromSong(song);
                if (candidate == null) continue;

                data.Tracks.Add(new PlaylistTrack
                {
                    Source = PlaylistSource.Netease,
                    Id = candidate.Id.ToString(),
                    Title = candidate.Name,
                    Artist = candidate.Artist,
                    Duration = candidate.Duration
                });
            }
        }

        return data.Tracks.Count > 0 ? data : null;
    }

    private static ResolvedSong TryResolve(Candidate candidate)
    {
        return ResolveById(candidate.Id, candidate.Name, candidate.Artist, candidate.Duration);
    }

    private static List<Candidate> Search(string keyword)
    {
        var json = Get("https://music.163.com/api/search/get/web?type=1&limit=5&offset=0&s=" +
                       Uri.EscapeDataString(keyword));
        return ParseSearch(json);
    }

    private static Candidate LookupById(long id)
    {
        var json = Get("https://music.163.com/api/song/detail?ids=%5B" + id + "%5D");
        return ParseDetail(json);
    }

    /// <summary>320k 直链；无版权时返回 null（code=404 / url 为空）。</summary>
    private static string PlayerUrl(long id)
    {
        var json = Get("https://music.163.com/api/song/enhance/player/url?ids=%5B" + id + "%5D&br=320000");
        return ParsePlayerUrl(json);
    }

    /// <summary>128k 兜底：跟随 302，只有落到 music.126.net 且是 audio/* 才算成功。</summary>
    private static string OuterUrl(long id)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("https://music.163.com/song/media/outer/url?id=" + id + ".mp3");
            request.UserAgent = UserAgent;
            request.Referer = Referer;
            request.Headers["Cookie"] = CookieHeader();
            request.AllowAutoRedirect = true;
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                var type = response.ContentType ?? "";
                var final = response.ResponseUri != null ? response.ResponseUri.ToString() : "";

                if (type.StartsWith("audio", StringComparison.OrdinalIgnoreCase) &&
                    final.IndexOf("music.126.net", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return final;
                }

                Logger.Write("netease outer: not audio (" + type + ") " + final);
            }
        }
        catch (Exception ex)
        {
            Logger.Write("netease outer: " + ex.Message);
        }
        return null;
    }

    // ---- 解析（纯函数，方便离线测试）----

    public static List<Candidate> ParseSearch(string json)
    {
        var list = new List<Candidate>();

        var root = SafeParse(json);
        if (root == null) return list;

        var result = root["result"];
        var songs = result != null ? result["songs"] : null;
        if (songs == null || songs.Kind != JsonKind.Array) return list;

        foreach (var song in songs.Items)
        {
            var candidate = FromSong(song);
            if (candidate != null) list.Add(candidate);
        }
        return list;
    }

    public static Candidate ParseDetail(string json)
    {
        var root = SafeParse(json);
        if (root == null) return null;

        var songs = root["songs"];
        if (songs == null || songs.Kind != JsonKind.Array || songs.Count == 0) return null;

        return FromSong(songs[0]);
    }

    public static string ParsePlayerUrl(string json)
    {
        var root = SafeParse(json);
        if (root == null) return null;

        var data = root["data"];
        if (data == null || data.Kind != JsonKind.Array || data.Count == 0) return null;

        var first = data[0];
        if (first == null) return null;

        var code = first["code"] != null ? first["code"].AsInt : 0;
        var url = first["url"] != null ? first["url"].AsString : null;
        return code == 200 && !string.IsNullOrEmpty(url) ? url : null;
    }

    private static Candidate FromSong(JsonValue song)
    {
        if (song == null) return null;

        var id = song["id"] != null ? song["id"].AsLong : 0;
        if (id <= 0) return null;

        var name = song["name"] != null ? (song["name"].AsString ?? "") : "";
        var durationValue = song["duration"] ?? song["dt"];
        var duration = durationValue != null ? durationValue.AsLong : 0;
        var fee = song["fee"] != null ? song["fee"].AsInt : 0;

        var artists = song["artists"] ?? song["ar"];
        var artist = "";
        if (artists != null && artists.Kind == JsonKind.Array)
        {
            var names = new List<string>();
            foreach (var item in artists.Items)
            {
                var value = item != null && item["name"] != null ? item["name"].AsString : null;
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                if (names.Count >= 2) break;
            }
            artist = string.Join(" / ", names);
        }

        return new Candidate
        {
            Id = id,
            Name = name,
            Artist = artist,
            Duration = duration > 0 ? TimeSpan.FromMilliseconds(duration) : TimeSpan.Zero,
            Fee = fee
        };
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
            Logger.Write("netease json: " + ex.Message);
            return null;
        }
    }

    private static string Get(string url, string cookie = null)
    {
        try
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Referer = Referer;
            request.Headers["Cookie"] = CookieHeader(cookie);
            request.Accept = "*/*";
            request.Timeout = 12000;
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
            Logger.Write("netease api: " + ex.Message);
            return null;
        }
    }
}
