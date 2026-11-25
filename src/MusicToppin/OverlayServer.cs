using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MusicToppin;

/// <summary>给浏览器源用的数据快照。</summary>
internal sealed class OverlayState
{
    public string Title = "";
    public string Artist = "";
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

    /// <summary>手写 JSON（.NET Framework 4.8 没有 System.Text.Json）。</summary>
    public string ToJson()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        AppendString(builder, "title", Title);
        AppendString(builder, "artist", Artist);
        AppendString(builder, "accent", Accent);
        AppendString(builder, "titleColor", TitleColor);
        AppendString(builder, "artistColor", ArtistColor);
        AppendString(builder, "prevTitle", PrevTitle);
        AppendString(builder, "prevArtist", PrevArtist);
        builder.Append(",\"playing\":").Append(Playing ? "true" : "false");
        builder.Append(",\"hasTimeline\":").Append(HasTimeline ? "true" : "false");
        builder.Append(",\"show\":").Append(Show ? "true" : "false");
        builder.Append(",\"position\":").Append(Position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"duration\":").Append(Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"opacity\":").Append(Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendString(StringBuilder builder, string key, string value)
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

    public bool Start()
    {
        if (_running) return true;

        for (var port = 4681; port <= 4710; port++)
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
                var requestLine = ReadRequest(stream);
                if (requestLine == null) return;

                var path = "/";
                var parts = requestLine.Split(' ');
                if (parts.Length >= 2) path = parts[1];
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
                else
                {
                    body = "not found";
                    contentType = "text/plain; charset=utf-8";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                var header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch
        {
            try { client?.Close(); } catch { }
        }
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

    /// <summary>
    /// 读完整个请求头再回包：只读第一行就关闭连接，内核会发 RST，
    /// 客户端报 "connection was forcibly closed"。
    /// </summary>
    private static string ReadRequest(Stream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = stream.Read(buffer, length, 1);
            if (read <= 0) break;
            length++;
            if (length >= 4 &&
                buffer[length - 4] == (byte)'\r' && buffer[length - 3] == (byte)'\n' &&
                buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
            {
                break;
            }
        }

        if (length == 0) return null;
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = text.IndexOf('\r');
        return lineEnd > 0 ? text.Substring(0, lineEnd) : text.Trim();
    }

    /// <summary>叠加层页面：透明背景 + 和桌面浮窗一致的卡片刻法。</summary>
    private const string Page = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<title>Music Toppin Overlay</title>
<style>
  html,body{margin:0;padding:0;background:transparent;overflow:hidden;
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
  .divider{height:1px;background:rgba(255,255,255,.08);margin:11px 0 9px;}
  .nav{display:flex;align-items:baseline;gap:7px;font-size:12.5px;color:rgba(245,248,252,.86);}
  .nav .k{font-size:10px;letter-spacing:.1em;color:rgba(159,176,198,.62);}
  .nav .v{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .bar{position:absolute;left:0;right:0;bottom:0;height:2px;background:rgba(255,255,255,.08);}
  .bar i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--accent),#FF7A45);}
  .prev{margin-top:9px;border-radius:10px;padding:10px 14px;
    background:rgba(16,19,25,.9);border:1px solid rgba(255,255,255,.1);opacity:.66;display:none;}
  .prev.on{display:block;}
  .prev .tag2{font-size:10.5px;color:rgba(159,176,198,.62);}
  .prev .t2{margin-top:6px;font-size:16px;font-weight:700;color:rgba(245,248,252,.55);word-break:break-word;}
  .prev .a2{margin-top:3px;font-size:12.5px;color:rgba(159,176,198,.55);word-break:break-word;}
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
    <div class=""divider"" id=""divider""></div>
    <div class=""nav""><span class=""k"">上一首</span><span class=""v"" id=""prev"">—</span></div>
    <div class=""bar"" id=""bar"" style=""display:none""><i id=""fill""></i></div>
  </div>
  <div class=""prev"" id=""prevCard"">
    <div class=""tag2"">播放完毕</div>
    <div class=""t2"" id=""prevTitle""></div>
    <div class=""a2"" id=""prevArtist""></div>
  </div>
</div>
<script>
(function(){
  var wrap=document.getElementById('wrap');
  var card=document.getElementById('card');
  function pad(n){return (n<10?'0':'')+n;}
  function fmt(s){s=Math.max(0,s||0);return pad(Math.floor(s/60))+':'+pad(Math.floor(s%60));}
  function apply(st){
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
    document.getElementById('time').textContent=st.hasTimeline?fmt(st.position)+' / '+fmt(st.duration):'';
    var bar=document.getElementById('bar');
    if(st.hasTimeline&&st.duration>0){bar.style.display='block';document.getElementById('fill').style.width=Math.min(100,st.position/st.duration*100)+'%';}
    else{bar.style.display='none';}
    var pv=document.getElementById('prev');
    pv.textContent=st.prevTitle?(st.prevTitle+(st.prevArtist?' — '+st.prevArtist:'')):'—';
    var pc=document.getElementById('prevCard');
    if(st.prevTitle){pc.className='prev on';document.getElementById('prevTitle').textContent=st.prevTitle;document.getElementById('prevArtist').textContent=st.prevArtist||'';}
    else{pc.className='prev';}
  }
  function tick(){
    fetch('/state?t='+Date.now()).then(function(r){return r.json();}).then(apply).catch(function(){});
  }
  tick(); setInterval(tick,400);
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
