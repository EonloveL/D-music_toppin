using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MusicToppin;

/// <summary>给浏览器源用的数据快照。</summary>
internal sealed class OverlayState
{
    public string Title = "";
    public string Artist = "";
    public string Requester = "";
    public string LyricsTheme = "siamese";
    public int Style;
    public bool Playing;
    public double Position;
    public double Duration;
    public bool HasTimeline;
    public string PrevTitle = "";
    public string PrevArtist = "";
    public string Accent = "#FF3B30";
    public string TitleColor = "#F5F8FC";
    public string ArtistColor = "#9FB0C6";
    public double Opacity = 0.96;
    public bool Show;

    /// <summary>点歌队列：下一首 + 排队数。</summary>
    public string NextTitle = "";
    public string NextArtist = "";
    public int QueueCount;

    /// <summary>给观众看的提示（点歌成功 / 放不了），6 秒内有效；ToastId 变化时前端才重新弹。</summary>
    public string Toast = "";
    public int ToastId;

    /// <summary>视频点播：要显示在直播画面小窗里的 B 站视频（空 = 不显示）。</summary>
    public string VideoBvid = "";

    /// <summary>视频声音是否跟随视频（true = iframe 出声、音画同步；false = 静音，声音走应用播放器）。</summary>
    public bool VideoSound;

    /// <summary>视频重开标记：变一次就强制前端重建 iframe（用于「上一首」重播视频）。</summary>
    public int VideoNonce;

    /// <summary>手写 JSON（.NET Framework 4.8 没有 System.Text.Json）。</summary>
    public string ToJson()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        AppendString(builder, "title", Title);
        AppendString(builder, "artist", Artist);
        AppendString(builder, "requester", Requester);
        AppendString(builder, "accent", Accent);
        AppendString(builder, "titleColor", TitleColor);
        AppendString(builder, "artistColor", ArtistColor);
        AppendString(builder, "prevTitle", PrevTitle);
        AppendString(builder, "prevArtist", PrevArtist);
        AppendString(builder, "nextTitle", NextTitle);
        AppendString(builder, "nextArtist", NextArtist);
        AppendString(builder, "toast", Toast);
        AppendString(builder, "videoBvid", VideoBvid);
        builder.Append(",\"videoSound\":").Append(VideoSound ? "true" : "false");
        builder.Append(",\"videoNonce\":").Append(VideoNonce);
        builder.Append(",\"queueCount\":").Append(QueueCount);
        builder.Append(",\"toastId\":").Append(ToastId);
        builder.Append(",\"style\":").Append(Style);
        builder.Append(",\"playing\":").Append(Playing ? "true" : "false");
        builder.Append(",\"hasTimeline\":").Append(HasTimeline ? "true" : "false");
        builder.Append(",\"show\":").Append(Show ? "true" : "false");
        builder.Append(",\"position\":").Append(Position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"duration\":").Append(Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"opacity\":").Append(Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    internal static void AppendString(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 1) builder.Append(',');
        builder.Append('"').Append(key).Append("\":\"");
        var text = value ?? "";
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < 32) builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    else builder.Append(ch);
                    break;
            }
        }
        builder.Append('"');
    }
}

/// <summary>
/// 本地 HTTP 叠加层：给 OBS「浏览器源」用。
/// 桌面浮窗可以随时隐藏，浏览器源仍然独立显示——正好满足"主播看不到、观众看得到"。
/// 用 TcpListener 自己实现最小 HTTP，避免 HttpListener 需要 URL ACL（普通权限跑不起来）。
/// </summary>
internal sealed class OverlayServer : IDisposable
{
    private readonly Func<OverlayState> _stateProvider;
    private TcpListener _listener;
    private Thread _thread;
    private volatile bool _running;

    public int Port { get; private set; }
    public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

    public OverlayServer(Func<OverlayState> stateProvider)
    {
        _stateProvider = stateProvider;
    }

    /// <summary>启动本地 HTTP 服务（`/` 和 `/video` 共用同一个端口）；优先上次的端口，被占用就自动往后找。</summary>
    public bool Start(int preferredPort = 0)
    {
        if (_running) return true;

        var ports = new List<int>();
        if (preferredPort >= 1024 && preferredPort <= 65535) ports.Add(preferredPort);
        for (var port = 4681; port <= 4750; port++)
        {
            if (!ports.Contains(port)) ports.Add(port);
        }

        foreach (var port in ports)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                Port = port;
                _running = true;

                _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "OverlayServer" };
                _thread.Start();
                Logger.Write("overlay server started at " + Url);
                return true;
            }
            catch (SocketException)
            {
                // 端口被占，换下一个
            }
            catch (Exception ex)
            {
                Logger.Write("overlay start: " + ex.Message);
                break;
            }
        }

        return false;
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client = null;
            try
            {
                client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
            catch
            {
                if (!_running) return;
            }
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 3000;
                var request = ReadRequest(stream);
                if (request == null) return;

                if (request.Path != "/state" && request.Path != "/state.json")
                {
                    Logger.Write("overlay req: " + request.Method + " " + request.Path + " host=" + request.Host);
                }

                // 只接受本机回环 Host，挡住 DNS rebinding（外部页面把域名解析到 127.0.0.1 来读 /state）
                if (!IsAllowedHost(request.Host))
                {
                    WriteResponse(stream, "403 Forbidden", "text/plain; charset=utf-8", "forbidden",
                        !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase));
                    return;
                }

                if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", "method not allowed",
                        !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase));
                    return;
                }

                var includeBody = !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);

                var path = request.Path;
                var queryIndex = path.IndexOf('?');
                if (queryIndex >= 0) path = path.Substring(0, queryIndex);

                string body;
                string contentType;

                if (path == "/state" || path == "/state.json")
                {
                    body = CurrentStateJson();
                    contentType = "application/json; charset=utf-8";
                }
                else if (path == "/" || path == "/index.html")
                {
                    body = Page;
                    contentType = "text/html; charset=utf-8";
                }
                else if (path == "/video/stream")
                {
                    ProxyVideo(stream, request);
                    return;
                }
                else if (path == "/video")
                {
                    body = VideoPage;
                    contentType = "text/html; charset=utf-8";
                }
                else if (path == "/lyrics/state")
                {
                    // 端点名是历史沿用：独立歌词页已移除，此数据端点供守望先锋模式取歌词
                    body = CurrentLyricsJson();
                    contentType = "application/json; charset=utf-8";
                }
                else if (path.StartsWith("/assets/themes/", StringComparison.OrdinalIgnoreCase))
                {
                    var name = path.Substring("/assets/themes/".Length);
                    ServeThemeAsset(stream, name, includeBody);
                    return;
                }
                else if (path == "/ping")
                {
                    body = "ok";
                    contentType = "text/plain; charset=utf-8";
                }
                else
                {
                    WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "not found", includeBody);
                    return;
                }

                WriteResponse(stream, "200 OK", contentType, body, includeBody);
            }
        }
        catch
        {
            try { client?.Close(); } catch { }
        }
    }

    // ---- 纯净视频：/video/stream?bvid=... 本地缓存代理 ----

    private static string ExtractQueryValue(string path, string name)
    {
        var index = path.IndexOf('?');
        if (index < 0) return "";

        foreach (var part in path.Substring(index + 1).Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;

            if (string.Equals(part.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
            {
                try { return Uri.UnescapeDataString(part.Substring(eq + 1)); }
                catch { return part.Substring(eq + 1); }
            }
        }

        return "";
    }

    /// <summary>把 B 站 CDN 的 MP4 原样转发给页面（带 Referer / Range），页面只看到纯净画面，没有 B 站播放器 UI。</summary>
    private void ProxyVideo(Stream client, HttpRequestInfo request)
    {
        var bvid = ExtractQueryValue(request.Path, "bvid");
        if (string.IsNullOrWhiteSpace(bvid))
        {
            WriteResponse(client, "400 Bad Request", "text/plain; charset=utf-8", "bvid required");
            return;
        }

        if (!IsValidBvid(bvid))
        {
            WriteResponse(client, "400 Bad Request", "text/plain; charset=utf-8", "invalid bvid");
            return;
        }

        var entry = VideoCache.Get(bvid);
        if (entry == null || entry.Failed)
        {
            VideoCache.RemoveIfSame(bvid, entry);
            WriteResponse(client, "404 Not Found", "text/plain; charset=utf-8", "video not found");
            return;
        }

        // 等解析出总长度（或失败）；下载本身在后台继续，页面可以先播已缓存的部分
        var wait = Stopwatch.StartNew();
        while (!entry.Failed && entry.Total <= 0 && wait.Elapsed < TimeSpan.FromSeconds(20))
        {
            lock (entry.Gate) Monitor.Wait(entry.Gate, 100);
        }

        if (entry.Failed)
        {
            VideoCache.RemoveIfSame(bvid, entry);
            WriteResponse(client, "404 Not Found", "text/plain; charset=utf-8", "video not found");
            return;
        }

        var total = entry.Total > 0 ? entry.Total : entry.Downloaded;
        var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);

        // Range 起点越界：不要回 200 从头重发，按 416 拒绝
        long rangeStart;
        if (total > 0 && TryGetRangeStart(request.Range, out rangeStart) && rangeStart >= total)
        {
            WriteResponse(client, "416 Requested Range Not Satisfiable", "text/plain; charset=utf-8", "range out of bounds");
            return;
        }

        // 先缓冲一小段再开始回包：游戏抢带宽时，播放端拿到的是已落盘的稳定数据
        if (!isHead)
        {
            var minBuffer = Math.Min(total, 8L * 1024 * 1024);
            while (!entry.Failed && entry.Downloaded < minBuffer && wait.Elapsed < TimeSpan.FromSeconds(25))
            {
                lock (entry.Gate) Monitor.Wait(entry.Gate, 100);
            }
        }

        long start;
        long end;
        var hasRange = TryParseRange(request.Range, total, out start, out end);
        Logger.Write("video serve: range=" + request.Range + " total=" + total + " start=" + start + " end=" + end + " downloaded=" + entry.Downloaded + " rangeResp=" + hasRange);

        var header = new StringBuilder();
        header.Append(hasRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        header.Append("Content-Type: video/mp4\r\n");
        header.Append("Content-Length: ").Append(end - start + 1).Append("\r\n");
        if (hasRange)
        {
            header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
        }
        header.Append("Accept-Ranges: bytes\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        client.Write(headerBytes, 0, headerBytes.Length);

        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase)) return;

        ServeCachedVideo(client, entry, start, end);
    }

    /// <summary>从本地缓存文件按区间输出；未下载到的部分等后台下载追上。</summary>
    private static void ServeCachedVideo(Stream client, VideoCacheEntry entry, long start, long end)
    {
        FileStream file = null;
        var buffer = new byte[64 * 1024];

        try
        {
            var openWait = Stopwatch.StartNew();
            while (file == null && openWait.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (entry.Failed && entry.Downloaded <= 0) return;

                try
                {
                    file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (FileNotFoundException)
                {
                    lock (entry.Gate) Monitor.Wait(entry.Gate, 50);
                }
                catch (IOException)
                {
                    lock (entry.Gate) Monitor.Wait(entry.Gate, 50);
                }
            }

            if (file == null) return;

            file.Seek(start, SeekOrigin.Begin);
            var position = start;

            while (position <= end)
            {
                long available;
                lock (entry.Gate) available = entry.Downloaded;

                if (position < available)
                {
                    var want = (int)Math.Min(buffer.Length, Math.Min(available - position, end - position + 1));
                    var read = file.Read(buffer, 0, want);
                    if (read <= 0)
                    {
                        Thread.Sleep(15);
                        continue;
                    }

                    client.Write(buffer, 0, read);
                    position += read;
                }
                else if (entry.Complete || entry.Failed)
                {
                    break;
                }
                else
                {
                    lock (entry.Gate) Monitor.Wait(entry.Gate, 100);
                }
            }

            client.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { if (file != null) file.Dispose(); } catch { }
        }
    }

    internal static bool TryGetRangeStart(string range, out long start)
    {
        start = 0;
        if (string.IsNullOrWhiteSpace(range)) return false;

        var text = range.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var spec = text.Substring("bytes=".Length).Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var startText = spec.Substring(0, dash).Trim();
        return startText.Length > 0 && long.TryParse(startText, out start);
    }

    internal static bool TryParseRange(string range, long total, out long start, out long end)
    {
        start = 0;
        end = total > 0 ? total - 1 : 0;

        if (total <= 0 || string.IsNullOrWhiteSpace(range)) return false;

        var text = range.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var spec = text.Substring("bytes=".Length).Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var startText = spec.Substring(0, dash).Trim();
        var endText = spec.Substring(dash + 1).Trim();

        long s;
        long e;

        if (startText.Length == 0)
        {
            // bytes=-N：最后 N 字节
            if (!long.TryParse(endText, out e)) return false;
            s = Math.Max(0, total - e);
            e = total - 1;
        }
        else
        {
            if (!long.TryParse(startText, out s)) return false;
            if (endText.Length == 0) e = total - 1;
            else if (!long.TryParse(endText, out e)) return false;
        }

        if (s < 0) s = 0;
        if (e >= total) e = total - 1;
        if (e < s) return false;

        start = s;
        end = e;
        return true;
    }

    /// <summary>只接受 BV1 + 10 位字母数字，避免任意字符串拼进 B 站接口。</summary>
    internal static bool IsValidBvid(string bvid)
    {
        if (bvid == null || bvid.Length != 12) return false;
        if (bvid[0] != 'B' && bvid[0] != 'b') return false;
        if (bvid[1] != 'V' && bvid[1] != 'v') return false;

        for (var i = 2; i < bvid.Length; i++)
        {
            var c = bvid[i];
            if (!char.IsLetterOrDigit(c) || c > 127) return false;
        }

        return true;
    }

    private static void WriteResponse(Stream stream, string status, string contentType, string body, bool includeBody = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        var header =
            "HTTP/1.1 " + status + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + bytes.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (includeBody) stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;   // HTTP/1.0 不带 Host

        var value = host.Trim().ToLowerInvariant();
        var colon = value.LastIndexOf(':');
        var name = colon > 0 ? value.Substring(0, colon) : value;

        return name == "127.0.0.1" || name == "localhost" || name == "[::1]";
    }

    private string CurrentStateJson()
    {
        try
        {
            var provider = _stateProvider;
            var state = provider == null ? new OverlayState() : provider();
            return state == null ? new OverlayState().ToJson() : state.ToJson();
        }
        catch (Exception ex)
        {
            Logger.Write("overlay state: " + ex.Message);
            return new OverlayState().ToJson();
        }
    }

    private string CurrentLyricsJson()
    {
        OverlayState state = null;
        try
        {
            state = _stateProvider == null ? null : _stateProvider();
        }
        catch (Exception ex)
        {
            Logger.Write("overlay lyrics state: " + ex.Message);
        }
        if (state == null) state = new OverlayState();

        var snap = LyricsService.Snapshot();
        var builder = new StringBuilder();
        builder.Append('{');
        OverlayState.AppendString(builder, "key", snap.Key);
        OverlayState.AppendString(builder, "title", string.IsNullOrEmpty(state.Title) ? snap.Title : state.Title);
        OverlayState.AppendString(builder, "artist", string.IsNullOrEmpty(state.Artist) ? snap.Artist : state.Artist);
        OverlayState.AppendString(builder, "requester", state.Requester ?? "");
        OverlayState.AppendString(builder, "nextTitle", state.NextTitle ?? "");
        OverlayState.AppendString(builder, "nextArtist", state.NextArtist ?? "");
        OverlayState.AppendString(builder, "theme", state.LyricsTheme ?? "siamese");
        OverlayState.AppendString(builder, "accent", state.Accent ?? "#FF3B30");
        OverlayState.AppendString(builder, "titleColor", state.TitleColor ?? "#F5F8FC");
        OverlayState.AppendString(builder, "artistColor", state.ArtistColor ?? "#9FB0C6");
        builder.Append(",\"opacity\":").Append(state.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"style\":").Append(state.Style);
        builder.Append(",\"playing\":").Append(state.Playing ? "true" : "false");
        builder.Append(",\"show\":").Append(snap.Lines.Count > 0 ? "true" : "false");
        builder.Append(",\"position\":").Append(state.Position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"duration\":").Append(state.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"queueCount\":").Append(state.QueueCount);
        builder.Append(",\"version\":").Append(snap.Version);
        builder.Append(",\"lines\":[");
        for (var i = 0; i < snap.Lines.Count; i++)
        {
            var line = snap.Lines[i];
            if (i > 0) builder.Append(',');
            builder.Append("{\"t\":").Append(line.Time.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(',');
            OverlayState.AppendString(builder, "text", line.Text);
            builder.Append('}');
        }
        builder.Append("]}");
        return builder.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> AssetCache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

    private static void ServeThemeAsset(Stream client, string name, bool includeBody)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }

        var file = ThemeAssetPath(name);
        if (file == null || !File.Exists(file))
        {
            WriteResponse(client, "404 Not Found", "text/plain; charset=utf-8", "not found", includeBody);
            return;
        }

        var bytes = AssetCache.GetOrAdd(file, File.ReadAllBytes);
        var header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: image/png\r\n" +
            "Content-Length: " + bytes.Length + "\r\n" +
            "Cache-Control: public, max-age=86400\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        client.Write(headerBytes, 0, headerBytes.Length);
        if (!includeBody) return;
        client.Write(bytes, 0, bytes.Length);
    }

    private static string ThemeAssetPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return null;
        if (!RegexSafeName(name)) return null;

        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "themes");
        var path = Path.Combine(baseDir, name + ".png");
        return path;
    }

    private static bool RegexSafeName(string name)
    {
        if (name.Length == 0 || name.Length > 40) return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    private sealed class HttpRequestInfo
    {
        public string Method = "GET";
        public string Path = "/";
        public string Host = "";
        public string Range = "";
    }

    /// <summary>
    /// 读完整个请求头再回包：只读第一行就关闭连接，内核会发 RST，
    /// 客户端报 "connection was forcibly closed"。
    /// </summary>
    private static HttpRequestInfo ReadRequest(Stream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = stream.Read(buffer, length, Math.Min(512, buffer.Length - length));
            if (read <= 0) break;
            length += read;
            if (length >= 4 &&
                buffer[length - 4] == (byte)'\r' && buffer[length - 3] == (byte)'\n' &&
                buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
            {
                break;
            }
        }

        if (length == 0) return null;

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0) return null;

        var info = new HttpRequestInfo();
        var parts = lines[0].Split(' ');
        if (parts.Length >= 2)
        {
            info.Method = parts[0];
            info.Path = parts[1];
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                info.Host = line.Substring(5).Trim();
                continue;
            }

            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                info.Range = line.Substring(6).Trim();
            }
        }

        return info;
    }

    /// <summary>叠加层页面：透明背景 + 和桌面浮窗一致的卡片刻法。</summary>
    private const string Page = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin Overlay</title>
<style>
  html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;
    font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif;-webkit-font-smoothing:antialiased;}
  #wrap{display:none;padding:6px 6px 2px 6px;width:388px;box-sizing:border-box;}
  #wrap.on{display:block;}
  .card{position:relative;border-radius:10px;padding:12px 14px 17px 16px;
    background:rgba(14,17,23,.96);border:1px solid rgba(255,255,255,.14);
    box-shadow:0 10px 30px rgba(0,0,0,.45);overflow:hidden;}
  .accent{position:absolute;left:0;top:0;bottom:0;width:3px;background:var(--accent);}
  .row1{display:flex;align-items:center;gap:8px;margin-bottom:8px;}
  .dot{width:7px;height:7px;border-radius:50%;background:var(--accent);box-shadow:0 0 0 3px rgba(255,59,48,.16),0 0 10px var(--accent);}
  .tag{font-size:11px;font-weight:700;letter-spacing:.14em;color:var(--accent);}
  .time{margin-left:auto;font-family:Consolas,monospace;font-size:11px;color:rgba(159,176,198,.62);}
  .title{font-size:19px;line-height:1.36;font-weight:600;color:var(--title);word-break:break-word;}
  .artist{margin-top:5px;font-size:14px;line-height:1.4;color:var(--artist);word-break:break-word;}
  .req{margin-top:4px;font-size:11.5px;line-height:1.4;color:rgba(159,176,198,.78);word-break:break-word;}
  .req:empty{display:none;}
  .divider{height:1px;background:rgba(255,255,255,.08);margin:11px 0 9px;}
  .nav{display:flex;align-items:baseline;gap:7px;font-size:12.5px;color:rgba(245,248,252,.86);}
  .nav .k{font-size:10px;letter-spacing:.1em;color:rgba(159,176,198,.62);}
  .nav .v{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .nav.next{margin-top:4px;display:none;}
  .nav.next.on{display:flex;}
  #toast{display:none;margin-top:8px;padding:8px 14px;border-radius:999px;
    background:rgba(14,17,23,.94);border:1px solid rgba(255,255,255,.16);color:#F5F8FC;font-size:13px;
    text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  #toast.on{display:block;}
  .bar{position:absolute;left:0;right:0;bottom:0;height:2px;background:rgba(255,255,255,.08);}
  .bar i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--accent),#FF7A45);}
  .prev{margin-top:9px;border-radius:10px;padding:10px 14px;
    background:rgba(16,19,25,.9);border:1px solid rgba(255,255,255,.1);opacity:.66;display:none;}
  .prev.on{display:block;}
  .prev .tag2{font-size:10.5px;color:rgba(159,176,198,.62);}
  .prev .t2{margin-top:6px;font-size:16px;font-weight:700;color:rgba(245,248,252,.55);word-break:break-word;}
  .prev .a2{margin-top:3px;font-size:12.5px;color:rgba(159,176,198,.55);word-break:break-word;}

  /* 守望先锋模式（显示歌词）：1920×1080 画布，中央圆圈留空，左右卡片 */
  .hidden{display:none!important;}
  #ow{display:none;position:fixed;inset:0;}
  #ow.on{display:block;}
  .ow-stage{position:absolute;left:50%;top:50%;width:1920px;height:1080px;transform-origin:center;--accent:#8fb8ff;}
  .ow-hud{position:absolute;left:0;right:0;bottom:0;height:210px;display:flex;align-items:center;justify-content:center;}
  .ow-circle{position:relative;width:148px;height:148px;border-radius:50%;}
  .ow-halo{position:absolute;inset:0;border-radius:50%;border:1px solid rgba(255,255,255,.12);box-shadow:0 0 18px color-mix(in srgb, var(--accent) 28%, transparent);}
  .ow-cat{position:absolute;left:50%;top:-88px;transform:translateX(-50%);width:230px;height:230px;pointer-events:none;filter:drop-shadow(0 4px 8px rgba(0,0,0,.35));}
  .ow-deco span{position:absolute;left:50%;top:50%;font-size:24px;pointer-events:none;filter:drop-shadow(0 2px 4px rgba(0,0,0,.35));}
  .ow-card{position:absolute;bottom:34px;width:340px;padding:16px 18px;border-radius:18px;background:rgba(10,15,24,.70);
    box-shadow:0 0 0 1px rgba(255,255,255,.10),0 0 0 1px color-mix(in srgb, var(--accent) 14%, transparent),0 10px 26px rgba(0,0,0,.38);
    backdrop-filter:blur(4px);color:#eaf2ff;transition:opacity .2s;}
  .ow-left{left:500px;text-align:right;}
  .ow-right{left:1080px;text-align:left;}
  .ow-tag{font-size:12px;color:color-mix(in srgb, var(--accent) 75%, #9fb0c6);letter-spacing:1px;margin-bottom:6px;}
  .ow-title{font-size:25px;font-weight:700;line-height:1.25;margin-bottom:4px;}
  .ow-artist{font-size:16px;color:#a9bcd3;margin-bottom:10px;}
  .ow-meta{font-size:12px;color:#8ea1b8;margin-bottom:8px;}
  .ow-bar{height:4px;border-radius:2px;background:rgba(255,255,255,.14);overflow:hidden;margin:8px 0 6px;}
  .ow-fill{height:100%;width:0;background:linear-gradient(90deg,var(--accent),#ff8a5c);}
  .ow-time{font-size:12px;color:#9fb0c6;display:flex;justify-content:space-between;}
  .ow-lyric{font-size:18px;line-height:1.6;color:#eaf2ff;transition:opacity .18s;}
  .ow-lyric.next{opacity:.72;font-size:16px;margin-top:6px;}
</style>
</head>
<body>
<div id=""wrap"">
  <div class=""card"" id=""card"">
    <div class=""accent""></div>
    <div class=""row1"">
      <span class=""dot""></span><span class=""tag"" id=""tag"">正在播放</span>
      <span class=""time"" id=""time""></span>
    </div>
    <div class=""title"" id=""title""></div>
    <div class=""artist"" id=""artist""></div>
    <div class=""req"" id=""req""></div>
    <div class=""divider"" id=""divider""></div>
    <div class=""nav""><span class=""k"">上一首</span><span class=""v"" id=""prev"">—</span></div>
    <div class=""nav next"" id=""nextRow""><span class=""k"">下一首</span><span class=""v"" id=""next"">—</span></div>
    <div class=""bar"" id=""bar"" style=""display:none""><i id=""fill""></i></div>
  </div>
  <div class=""prev"" id=""prevCard"">
    <div class=""tag2"">播放完毕</div>
    <div class=""t2"" id=""prevTitle""></div>
    <div class=""a2"" id=""prevArtist""></div>
  </div>
  <div id=""toast""></div>
</div>
<div id=""ow"">
  <div class=""ow-stage"" id=""owStage"">
    <div class=""ow-hud"">
      <div class=""ow-circle""><div class=""ow-halo""></div><div class=""ow-deco"" id=""owDeco""></div><img class=""ow-cat"" id=""owCat"" alt=""""></div>
    </div>
    <div class=""ow-card ow-left"" id=""owLeft"">
      <div class=""ow-tag"" id=""owTag""></div>
      <div class=""ow-title"" id=""owTitle""></div>
      <div class=""ow-artist"" id=""owArtist""></div>
      <div class=""ow-meta"" id=""owMeta""></div>
      <div class=""ow-bar""><div class=""ow-fill"" id=""owFill""></div></div>
      <div class=""ow-time""><span id=""owPos"">0:00</span><span id=""owDur"">0:00</span></div>
    </div>
    <div class=""ow-card ow-right"" id=""owRight"">
      <div class=""ow-tag"">当前歌词</div>
      <div class=""ow-lyric"" id=""owLyric1""></div>
      <div class=""ow-lyric next"" id=""owLyric2""></div>
      <div class=""ow-meta"" id=""owNext""></div>
    </div>
  </div>
</div>
<script>
(function(){
  var wrap=document.getElementById('wrap');
  var ow=document.getElementById('ow');
  var card=document.getElementById('card');
  var lastToast=0, toastTimer=null;
  function showToast(text){
    var el=document.getElementById('toast');
    el.textContent=text; el.className='on';
    if(toastTimer) clearTimeout(toastTimer);
    toastTimer=setTimeout(function(){ el.className=''; },4500);
  }
  function pad(n){return (n<10?'0':'')+n;}
  function fmt(s){s=Math.max(0,s||0);return pad(Math.floor(s/60))+':'+pad(Math.floor(s%60));}

  var owStage=document.getElementById('owStage');
  var owLeft=document.getElementById('owLeft');
  var owRight=document.getElementById('owRight');
  var owTag=document.getElementById('owTag');
  var owTitle=document.getElementById('owTitle');
  var owArtist=document.getElementById('owArtist');
  var owMeta=document.getElementById('owMeta');
  var owFill=document.getElementById('owFill');
  var owPos=document.getElementById('owPos');
  var owDur=document.getElementById('owDur');
  var owLyric1=document.getElementById('owLyric1');
  var owLyric2=document.getElementById('owLyric2');
  var owNext=document.getElementById('owNext');
  var owCat=document.getElementById('owCat');
  var owDeco=document.getElementById('owDeco');
  var owThemeNames={none:'无装饰',siamese:'暹罗猫',dachshund:'腊肠狗',devon:'德文猫',spring:'春节',xmas:'圣诞',halloween:'万圣',midautumn:'中秋',valentine:'情人'};
  var owFestAccent={spring:'#ff4d4d',xmas:'#4ddf88',halloween:'#ff9f43',midautumn:'#ffd666',valentine:'#ff6ba9'};
  var owFestIcons={
    spring:[['🏮',-74,-40],['🧨',70,-36],['🧧',62,66],['💮',-70,56],['✨',10,-70]],
    xmas:[['❄️',-74,-40],['🎄',70,-36],['🎁',62,66],['🔔',-70,56],['☃️',10,-70]],
    halloween:[['🎃',-74,-40],['🦇',70,-36],['👻',62,66],['🍬',-70,56],['✦️',10,-70]],
    midautumn:[['🌕',-74,-40],['🐇',70,-36],['🥮',62,66],['🌙',-70,56],['✨',10,-70]],
    valentine:[['💗',-74,-40],['💘',70,-36],['🌹',62,66],['💝',-70,56],['✨',10,-70]]
  };
  var lastOwTheme='';

  function owThemeUrl(theme){
    if(theme==='none' || owFestIcons[theme]) return '';
    return '/assets/themes/' + theme + '.png?v=150';
  }

  function resizeOw(){
    var s=Math.min(innerWidth/1920,innerHeight/1080);
    owStage.style.transform='translate(-50%,-50%) scale('+s+')';
  }
  addEventListener('resize',resizeOw); resizeOw();

  function apply(st){
    if(st && st.style === 3){
      wrap.className='';
      ow.className='on';
      applyOw();
      return;
    }

    ow.className='';
    if(!st.show){wrap.className='';return;}
    wrap.className='on';
    document.documentElement.style.setProperty('--accent',st.accent||'#FF3B30');
    document.documentElement.style.setProperty('--title',st.titleColor||'#F5F8FC');
    document.documentElement.style.setProperty('--artist',st.artistColor||'#9FB0C6');
    card.style.background='rgba(14,17,23,'+(st.opacity!=null?st.opacity:0.96)+')';
    document.getElementById('tag').textContent=st.playing?'正在播放':'已暂停';
    document.getElementById('tag').style.color=st.playing?(st.accent||'#FF3B30'):'#7C8694';
    document.querySelector('.dot').style.background=st.playing?(st.accent||'#FF3B30'):'#7C8694';
    document.getElementById('title').textContent=st.title||'';
    document.getElementById('artist').textContent=st.artist||'';
    document.getElementById('req').textContent=st.requester?('点歌人：'+st.requester):'';
    document.getElementById('time').textContent=st.hasTimeline?fmt(st.position)+' / '+fmt(st.duration):'';
    var bar=document.getElementById('bar');
    if(st.hasTimeline&&st.duration>0){bar.style.display='block';document.getElementById('fill').style.width=Math.min(100,st.position/st.duration*100)+'%';}
    else{bar.style.display='none';}
    var pv=document.getElementById('prev');
    pv.textContent=st.prevTitle?(st.prevTitle+(st.prevArtist?' — '+st.prevArtist:'')):'—';
    var pc=document.getElementById('prevCard');
    if(st.prevTitle){pc.className='prev on';document.getElementById('prevTitle').textContent=st.prevTitle;document.getElementById('prevArtist').textContent=st.prevArtist||'';}
    else{pc.className='prev';}
    var nr=document.getElementById('nextRow');
    if(st.nextTitle){
      nr.className='nav next on';
      document.getElementById('next').textContent=st.nextTitle+
        (st.queueCount>1?'（队列 '+st.queueCount+'）':'')+
        (st.nextArtist?' — '+st.nextArtist:'');
    }else{nr.className='nav next';}
    if(st.toast&&st.toastId&&st.toastId!==lastToast){lastToast=st.toastId;showToast(st.toast);}
  }

  function applyOw(){
    fetch('/lyrics/state?t='+Date.now()).then(function(r){return r.json();}).then(function(st){
      if(!st || st.style !== 3){ ow.className=''; return; }

      var theme = st.theme || 'siamese';
      if(!owThemeNames[theme]) theme = 'siamese';

      owStage.style.setProperty('--accent', owFestAccent[theme] || st.accent || '#8fb8ff');
      owTitle.style.color = st.titleColor || '#eaf2ff';
      owArtist.style.color = st.artistColor || '#a9bcd3';

      var show=!!st.show && (st.lines||[]).length>0;
      owLeft.classList.toggle('hidden', !st.title);
      owRight.classList.toggle('hidden', !show);

      owTag.textContent = st.playing ? '正在播放' : '已暂停';
      if(st.requester) owTag.textContent += ' · 点歌人 ' + st.requester;
      owTitle.textContent = st.title || '';
      owArtist.textContent = st.artist || '';

      if(theme !== lastOwTheme){
        lastOwTheme = theme;
        owDeco.innerHTML = '';
        var icons = owFestIcons[theme];
        if(icons){
          owCat.classList.add('hidden');
          owCat.removeAttribute('src');
          for(var k=0;k<icons.length;k++){
            var d=document.createElement('span');
            d.textContent=icons[k][0];
            d.style.transform='translate(-50%,-50%) translate('+icons[k][1]+'px,'+icons[k][2]+'px)';
            owDeco.appendChild(d);
          }
        } else {
          var url = owThemeUrl(theme);
          if(!url){
            owCat.classList.add('hidden');
            owCat.removeAttribute('src');
          } else {
            owCat.classList.remove('hidden');
            if(owCat.getAttribute('src') !== url) owCat.src = url;
          }
        }
      }
      owMeta.textContent = '守望先锋 · 主题：' + (owThemeNames[theme] || theme);

      var p = st.duration > 0 ? Math.min(1, (st.position||0)/st.duration) : 0;
      owFill.style.width = (p*100).toFixed(1) + '%';
      owPos.textContent = fmt(st.position||0);
      owDur.textContent = fmt(st.duration||0);

      if(!show){
        owLyric1.textContent='';
        owLyric2.textContent='';
        owNext.textContent='';
        return;
      }

      var lines=st.lines;
      var idx=0;
      for(var i=0;i<lines.length;i++){ if((lines[i].t||0) <= (st.position||0)) idx=i; }
      owLyric1.textContent=(st.position||0) < (lines[0].t||0) ? '' : (lines[idx].text||'');
      owLyric2.textContent=(idx+1<lines.length)?(lines[idx+1].text||''):'';
      owNext.textContent = st.nextTitle ? ('下一首：' + st.nextTitle + (st.nextArtist ? ' · ' + st.nextArtist : '')) : '';
    }).catch(function(){});
  }

  function tick(){
    fetch('/state?t='+Date.now()).then(function(r){return r.json();}).then(apply).catch(function(){});
  }
  var preview=location.search.indexOf('preview=1')>=0;
  if(preview){
    apply({show:true,playing:true,title:'预览模式：叠加层工作正常',artist:'Music Toppin',hasTimeline:true,position:15,duration:200,prevTitle:'',prevArtist:'',nextTitle:'示例：下一首',nextArtist:'',queueCount:1,toast:'',toastId:0});
  }else{
    tick(); setInterval(tick,400);
  }
})();
</script>
</body>
</html>";

    /// <summary>
    /// 视频小窗：OBS 里再加一个「浏览器源」指向 /video。
    /// 平时透明；点歌点到 B 站视频时，这里用小窗播放（静音，声音仍走应用自己的播放器）。
    /// 它只存在于浏览器源里，所以主播自己的桌面上不会出现，只有直播画面里能看到。
    /// </summary>
    private const string VideoPage = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin Video</title>
<style>
  html,body{margin:0;padding:0;width:100%;height:100%;background:transparent;overflow:hidden;}
  #stage{position:absolute;left:0;top:0;width:100%;height:100%;display:none;border-radius:12px;overflow:hidden;
    box-sizing:border-box;background:transparent;border:1px solid rgba(255,255,255,.14);box-shadow:0 12px 40px rgba(0,0,0,.55);}
  #stage.on{display:block;}
  #player{width:100%;height:100%;border:0;display:block;object-fit:contain;background:transparent;}
  #fallback{width:100%;height:100%;border:0;display:none;background:#000;}
</style>
</head>
<body>
<div id=""stage"">
  <video id=""player"" playsinline preload=""auto""></video>
  <iframe id=""fallback"" allow=""autoplay; fullscreen"" allowfullscreen></iframe>
</div>
<script>
(function(){
  var stage=document.getElementById('stage');
  var player=document.getElementById('player');
  var fallback=document.getElementById('fallback');
  var current='';
  var currentBv='';
  var currentSound=true;
  function fitStage(){
    // 按视频真实宽高比缩放到浏览器源内，留白保持透明，避免 letterbox 黑边
    var vw=player.videoWidth||0, vh=player.videoHeight||0;
    if(!vw||!vh) return;
    var W=window.innerWidth||480, H=window.innerHeight||270;
    var scale=Math.min(W/vw,H/vh);
    var w=Math.max(1,Math.round(vw*scale));
    var h=Math.max(1,Math.round(vh*scale));
    stage.style.width=w+'px';
    stage.style.height=h+'px';
    stage.style.left=Math.round((W-w)/2)+'px';
    stage.style.top=Math.round((H-h)/2)+'px';
  }
  function resetStage(){
    stage.style.left='0px';
    stage.style.top='0px';
    stage.style.width='100%';
    stage.style.height='100%';
  }
  player.addEventListener('loadedmetadata',fitStage);
  window.addEventListener('resize',fitStage);
  function hideFallback(){
    fallback.style.display='none';
    fallback.removeAttribute('src');
    player.style.display='block';
  }
  function showFallback(){
    if(!currentBv) return;
    resetStage();
    fallback.src='https://player.bilibili.com/player.html?bvid='+encodeURIComponent(currentBv)+
      '&p=1&autoplay=1&muted='+(currentSound?'0':'1')+'&danmaku=0&high_quality=1&as_wide=1';
    fallback.style.display='block';
    player.style.display='none';
  }
  player.addEventListener('error', showFallback);
  var stallTimer=null;
  function clearStall(){ if(stallTimer){clearTimeout(stallTimer);stallTimer=null;} }
  function armStall(){ clearStall(); stallTimer=setTimeout(function(){ if(player.readyState<3) showFallback(); },15000); }
  player.addEventListener('waiting',armStall);
  player.addEventListener('stalled',armStall);
  player.addEventListener('playing',clearStall);
  player.addEventListener('canplay',clearStall);
  player.addEventListener('pause',clearStall);
  function apply(st){
    var bv=st.videoBvid||'';
    var sound=!!st.videoSound;
    var key=bv+(sound?'|1':'|0')+'|'+(st.videoNonce||0);
    if(key===current) return;
    current=key;
    currentBv=bv;
    currentSound=sound;
    if(!bv){stage.className='';resetStage();try{player.pause();}catch(e){}player.removeAttribute('src');player.load();hideFallback();return;}
    stage.className='on';
    resetStage();
    hideFallback();
    player.muted=!sound;
    player.src='/video/stream?bvid='+encodeURIComponent(bv);
    var p=player.play();
    if(p&&p.catch)p.catch(function(){});
  }
  function tick(){
    fetch('/state?t='+Date.now()).then(function(r){return r.json();}).then(apply).catch(function(){});
  }
  if(location.search.indexOf('debug=1')>=0){
    setInterval(function(){
      document.title='rs='+player.readyState+' t='+player.currentTime.toFixed(1)+' vw='+player.videoWidth+' err='+(player.error?player.error.code:'-');
    },500);
  }
  var preview=location.search.indexOf('preview=1')>=0;
  if(preview){
    stage.className='on';
  }else{
    tick();setInterval(tick,600);
  }
})();
</script>
</body>
</html>";

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }
}
