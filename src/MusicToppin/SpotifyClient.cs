using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MusicToppin;

/// <summary>
/// Spotify Web API（PKCE + loopback 回调）。只负责搜索 / 加队列 / 状态；
/// 播放仍在 Spotify 客户端里，应用通过 SMTC 读当前曲目。
/// </summary>
internal sealed class SpotifyClient
{
    public const int RedirectPort = 4683;
    public static string RedirectUri
    {
        get { return "http://127.0.0.1:" + RedirectPort + "/callback"; }
    }

    private static readonly HttpClient Http = CreateHttp();
    private string _accessToken = "";
    private DateTime _accessExpiryUtc = DateTime.MinValue;

    public string ClientId = "";
    public string RefreshToken = "";

    public bool HasLogin
    {
        get { return !string.IsNullOrWhiteSpace(RefreshToken); }
    }

    private static HttpClient CreateHttp()
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicToppin/1.7");
        return client;
    }

    public void Logout()
    {
        RefreshToken = "";
        _accessToken = "";
        _accessExpiryUtc = DateTime.MinValue;
    }

    /// <summary>走浏览器授权；成功返回空字符串，失败返回给用户看的原因。</summary>
    public async Task<string> LoginAsync(string clientId)
    {
        ClientId = (clientId ?? "").Trim();
        if (ClientId.Length == 0) return "请先填写 Spotify Client ID";

        var verifier = RandomToken(64);
        var challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomToken(16);

        var listener = new TcpListener(IPAddress.Loopback, RedirectPort);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return "本地回调端口 " + RedirectPort + " 被占用：" + ex.Message;
        }

        try
        {
            var url = "https://accounts.spotify.com/authorize" +
                      "?client_id=" + Uri.EscapeDataString(ClientId) +
                      "&response_type=code" +
                      "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                      "&code_challenge_method=S256" +
                      "&code_challenge=" + Uri.EscapeDataString(challenge) +
                      "&state=" + Uri.EscapeDataString(state) +
                      "&scope=" + Uri.EscapeDataString("user-modify-playback-state user-read-playback-state user-read-currently-playing");

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                return "无法打开浏览器：" + ex.Message;
            }

            var accept = listener.AcceptTcpClientAsync();
            var finished = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMinutes(3))).ConfigureAwait(false);
            if (finished != accept)
                return "等待 Spotify 授权超时（3 分钟），请重试";

            string path;
            using (var client = accept.Result)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                var firstLine = request.Split('\n')[0].Trim();
                var parts = firstLine.Split(' ');
                path = parts.Length >= 2 ? parts[1] : "";

                var body = "<html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;padding:40px\">" +
                           "<h2>Music Toppin · Spotify</h2><p>授权已收到，可以关闭这个页面回到应用。</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                             bytes.Length + "\r\nConnection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }

            var query = ParseQuery(path);
            string code = query.ContainsKey("code") ? query["code"] : "";
            string returnedState = query.ContainsKey("state") ? query["state"] : "";
            if (string.IsNullOrEmpty(code)) return "Spotify 没有返回授权码（可能点了取消）";
            if (returnedState != state) return "Spotify 回调 state 不匹配，请重试";

            var form = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "client_id", ClientId },
                { "code_verifier", verifier }
            };
            var token = await PostTokenAsync(form).ConfigureAwait(false);
            if (token.ContainsKey("error")) return "Spotify 换取 token 失败：" + token["error"];
            if (!token.ContainsKey("access_token")) return "Spotify 没有返回 access_token";

            _accessToken = token["access_token"];
            RefreshToken = token.ContainsKey("refresh_token") ? token["refresh_token"] : "";
            _accessExpiryUtc = DateTime.UtcNow.AddSeconds(ParseInt(token, "expires_in", 3600));
            return "";
        }
        catch (Exception ex)
        {
            Logger.Write("spotify login: " + ex.Message);
            return "Spotify 授权失败：" + ex.Message;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    public async Task<SpotifyStatus> GetStatusAsync()
    {
        var status = new SpotifyStatus();
        if (!await EnsureTokenAsync().ConfigureAwait(false))
        {
            status.Error = "Spotify 未登录或 token 过期，请重新授权";
            return status;
        }

        var result = await RequestAsync(HttpMethod.Get, "https://api.spotify.com/v1/me", null).ConfigureAwait(false);
        if (result.Status != 200)
        {
            status.Error = SpotifyError(result.Body, result.Status);
            return status;
        }

        var root = Json.Parse(result.Body);
        status.Product = root != null && root["product"] != null ? root["product"].AsString : "";
        return status;
    }

    public async Task<SpotifyTrack> SearchTrackAsync(string query)
    {
        var track = new SpotifyTrack();
        if (!await EnsureTokenAsync().ConfigureAwait(false))
        {
            track.Error = "Spotify 未登录或 token 过期，请重新授权";
            return track;
        }

        var url = "https://api.spotify.com/v1/search?type=track&limit=1&q=" + Uri.EscapeDataString(query ?? "");
        var result = await RequestAsync(HttpMethod.Get, url, null).ConfigureAwait(false);
        if (result.Status != 200)
        {
            track.Error = SpotifyError(result.Body, result.Status);
            return track;
        }

        var root = Json.Parse(result.Body);
        var items = root != null && root["tracks"] != null ? root["tracks"]["items"] : null;
        if (items == null || items.Kind != JsonKind.Array || items.Count == 0)
        {
            track.Error = "Spotify 没搜到这首歌";
            return track;
        }

        var first = items[0];
        track.Name = first["name"] != null ? first["name"].AsString : "";
        track.Uri = first["uri"] != null ? first["uri"].AsString : "";
        var artists = first["artists"];
        if (artists != null && artists.Kind == JsonKind.Array)
        {
            var names = new List<string>();
            for (var i = 0; i < artists.Count; i++)
            {
                var name = artists[i]["name"] != null ? artists[i]["name"].AsString : "";
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            track.Artists = string.Join(", ", names.ToArray());
        }
        if (string.IsNullOrEmpty(track.Uri)) track.Error = "Spotify 返回的歌曲缺少 URI";
        return track;
    }

    /// <summary>加入 Spotify 队列；成功返回空字符串。</summary>
    public async Task<string> QueueAsync(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "歌曲 URI 为空";
        if (!await EnsureTokenAsync().ConfigureAwait(false))
            return "Spotify 未登录或 token 过期，请重新授权";

        var url = "https://api.spotify.com/v1/me/player/queue?uri=" + Uri.EscapeDataString(uri);
        var result = await RequestAsync(HttpMethod.Post, url, null).ConfigureAwait(false);
        if (result.Status == 204 || result.Status == 200) return "";
        if (result.Status == 404) return "Spotify 当前没有活动设备：先打开 Spotify 播放任意歌曲再点歌";
        if (result.Status == 403) return "Spotify 拒绝控制播放：需要 Premium 账号";
        return SpotifyError(result.Body, result.Status);
    }

    private async Task<bool> EnsureTokenAsync()
    {
        if (_accessToken.Length > 0 && DateTime.UtcNow < _accessExpiryUtc.AddSeconds(-60)) return true;
        if (string.IsNullOrWhiteSpace(RefreshToken) || string.IsNullOrWhiteSpace(ClientId)) return false;

        var form = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", RefreshToken },
            { "client_id", ClientId }
        };
        var token = await PostTokenAsync(form).ConfigureAwait(false);
        if (token.ContainsKey("error") || !token.ContainsKey("access_token")) return false;
        _accessToken = token["access_token"];
        if (token.ContainsKey("refresh_token") && !string.IsNullOrEmpty(token["refresh_token"]))
            RefreshToken = token["refresh_token"];
        _accessExpiryUtc = DateTime.UtcNow.AddSeconds(ParseInt(token, "expires_in", 3600));
        return true;
    }

    private async Task<Dictionary<string, string>> PostTokenAsync(Dictionary<string, string> form)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync("https://accounts.spotify.com/api/token", content).ConfigureAwait(false))
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = Json.Parse(text);
                if (root == null) { dict["error"] = "空响应"; return dict; }
                if (root["error"] != null) { dict["error"] = root["error"].AsString; return dict; }
                if (root["access_token"] != null) dict["access_token"] = root["access_token"].AsString;
                if (root["refresh_token"] != null) dict["refresh_token"] = root["refresh_token"].AsString;
                if (root["expires_in"] != null) dict["expires_in"] = root["expires_in"].AsInt.ToString();
            }
        }
        catch (Exception ex)
        {
            dict["error"] = ex.Message;
        }
        return dict;
    }

    private async Task<HttpResult> RequestAsync(HttpMethod method, string url, string json)
    {
        var first = await SendAsync(method, url, json, _accessToken).ConfigureAwait(false);
        if (first.Status != 401) return first;

        // access token 期间过期：刷新后再试一次
        _accessExpiryUtc = DateTime.MinValue;
        if (!await EnsureTokenAsync().ConfigureAwait(false)) return first;
        return await SendAsync(method, url, json, _accessToken).ConfigureAwait(false);
    }

    private static async Task<HttpResult> SendAsync(HttpMethod method, string url, string json, string token)
    {
        var result = new HttpResult();
        try
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (json != null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    result.Status = (int)response.StatusCode;
                    result.Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            result.Status = 0;
            result.Body = ex.Message;
        }
        return result;
    }

    private static string SpotifyError(string body, int status)
    {
        try
        {
            var root = Json.Parse(body);
            if (root != null && root["error"] != null && root["error"]["message"] != null)
                return "Spotify 错误：" + root["error"]["message"].AsString;
        }
        catch { }
        return "Spotify 请求失败（HTTP " + status + "）";
    }

    private static int ParseInt(Dictionary<string, string> dict, string key, int fallback)
    {
        int value;
        return dict.ContainsKey(key) && int.TryParse(dict[key], out value) ? value : fallback;
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = path.IndexOf('?');
        if (q < 0) return dict;
        foreach (var pair in path.Substring(q + 1).Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair.Substring(0, eq));
            var value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
            dict[key] = value;
        }
        return dict;
    }

    private static string RandomToken(int bytes)
    {
        var data = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(data);
        return Base64Url(data);
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class HttpResult
    {
        public int Status;
        public string Body = "";
    }
}

internal sealed class SpotifyTrack
{
    public string Name = "";
    public string Artists = "";
    public string Uri = "";
    public string Error = "";
}

internal sealed class SpotifyStatus
{
    public string Product = "";
    public string Error = "";

    public bool IsPremium
    {
        get { return string.Equals(Product, "premium", StringComparison.OrdinalIgnoreCase); }
    }
}
