using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MusicToppin;

internal sealed class PlaybackState
{
    public bool Playing;
    public bool Ended;
    public TimeSpan Position;
    public TimeSpan Duration;
}

/// <summary>
/// 用隐藏的 WebView2 当音频宿主：Chromium 能直接播 B 站 DASH(fMP4) 音频，
/// 并且可以通过 WebResourceRequested 注入 Referer，绕过热链保护。
/// 窗口放在屏幕外，不打扰用户，也不出现在任务栏。
/// </summary>
internal sealed class WebView2Player : IDisposable
{
    private const string Html =
        "<html><body style='margin:0;background:#000'>" +
        "<audio id='a' preload='auto'></audio>" +
        "<script>" +
        "function playAudio(u){window.__playError=0;var a=document.getElementById('a');a.src=u;var p=a.play();if(p&&p.catch)p.catch(function(){window.__playError=1;});}" +
        "function stopAudio(){var a=document.getElementById('a');a.pause();a.removeAttribute('src');a.load();}" +
        "function seekTo(t){var a=document.getElementById('a');try{a.currentTime=t;}catch(e){}}" +
        "function setVolume(v){var a=document.getElementById('a');try{a.volume=Math.max(0,Math.min(1,v));}catch(e){}}" +
        "function getState(){var a=document.getElementById('a');return JSON.stringify({t:a.currentTime||0,d:a.duration||0,p:a.paused,e:(a.error?a.error.code:(window.__playError?1:0)),ended:a.ended});}" +
        "</script></body></html>";

    private static readonly Regex StateRegex = new Regex(
        "\"t\":([0-9.]+).*?\"d\":([0-9.]+).*?\"p\":(true|false).*?\"e\":(-?\\d+).*?\"ended\":(true|false)",
        RegexOptions.Compiled);

    private Form _host;
    private WebView2 _web;
    private System.Windows.Forms.Timer _poll;
    private Task _initTask;
    private bool _ready;
    private bool _backupTried;
    private bool _failureReported;
    private ResolvedSong _current;
    private DateTime _lastProgressUtc = DateTime.MinValue;
    private TimeSpan _lastPosition;
    private Task _stopTask;
    private int _playToken;
    private int _lastStopToken;
    private int _lastPlayToken;
    private bool _polling;
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _playGate = new SemaphoreSlim(1, 1);

    public event Action<PlaybackState> StateChanged;
    public event Action<string> Failed;

    public ResolvedSong Current
    {
        get { return _current; }
    }

    public bool IsReady
    {
        get { return _ready; }
    }

    /// <summary>音量 0..1（默认 0.6，直播时别用满音量）。</summary>
    public float Volume { get; set; } = 0.6f;

    public async Task EnsureReadyAsync()
    {
        if (_ready) return;

        // 同时来两次播放请求时共用同一个初始化任务，避免重复创建窗口
        if (_initTask == null) _initTask = InitializeAsync();
        try
        {
            await _initTask;
        }
        catch
        {
            _initTask = null;   // 失败可重试（比如用户刚装好 WebView2 运行时）
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            _host = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(2, 2),
                Text = "Music Toppin Player"
            };

            _web = new WebView2 { Dock = DockStyle.Fill };
            _host.Controls.Add(_web);
            _host.Show();

            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicToppin", "WebView2");
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            core.NavigateToString(Html);
            await Task.Delay(400);

            _poll = new System.Windows.Forms.Timer { Interval = 500 };
            _poll.Tick += (_, _) => PollState();

            _ready = true;
        }
        catch (Exception ex)
        {
            Logger.Write("webview2 init: " + ex.Message);

            // 初始化失败要把已创建的隐藏窗口/控件清掉，否则每次重试都会多一个
            try { _web?.Dispose(); } catch { }
            try { if (_host != null) { _host.Close(); _host.Dispose(); } } catch { }
            _web = null;
            _host = null;
            _ready = false;

            throw new InvalidOperationException(
                "需要 WebView2 运行时（Windows 11 自带；Windows 10 请安装 Edge WebView2 Runtime）。\n" + ex.Message, ex);
        }
    }

    private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri ?? "";
            if (uri.IndexOf("bilivideo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.IndexOf("bilibili.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.IndexOf("mountaintoys", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.IndexOf("hdslb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.IndexOf("akamaized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.IndexOf("szbdyd.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Request.Headers.SetHeader("Referer", "https://www.bilibili.com");
                e.Request.Headers.SetHeader("Origin", "https://www.bilibili.com");
            }
            else if (uri.IndexOf("music.126.net", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 网易云 CDN：实测不校验 Referer，但带上更稳
                e.Request.Headers.SetHeader("Referer", "https://music.163.com/");
            }
        }
        catch { }
    }

    public async Task PlayAsync(ResolvedSong song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.AudioUrl)) return;

        // 串行化起播：避免两次 PlayAsync 的 playAudio 交错执行，后发的旧歌盖住新歌。
        await _playGate.WaitAsync();
        try
        {
            await PlayCoreAsync(song);
        }
        finally
        {
            _playGate.Release();
        }
    }

    private async Task PlayCoreAsync(ResolvedSong song)
    {
        var token = Interlocked.Increment(ref _playToken);
        Volatile.Write(ref _lastPlayToken, token);

        // 等上一次 StopAsync 真正执行完 stopAudio，避免旧脚本把新歌停掉
        var stopTask = _stopTask;
        if (stopTask != null)
        {
            try { await stopTask; } catch { }
        }

        if (token != Volatile.Read(ref _playToken)) return;

        await EnsureReadyAsync();
        if (token != Volatile.Read(ref _playToken)) return;

        _poll.Stop();
        _current = null;
        _backupTried = false;
        _failureReported = false;

        await ApplyVolumeAsync();
        if (token != Volatile.Read(ref _playToken)) return;

        await _web.CoreWebView2.ExecuteScriptAsync("playAudio(" + ToJsString(song.AudioUrl) + ")");
        if (token != Volatile.Read(ref _playToken))
        {
            // 起播期间被停止/切歌：如果最后发生的是「停止」，补一次 stopAudio，
            // 避免已经发出的 playAudio 在停止之后才真正开始出声。
            if (Volatile.Read(ref _lastStopToken) > Volatile.Read(ref _lastPlayToken))
            {
                try { await _web.CoreWebView2.ExecuteScriptAsync("stopAudio()"); } catch { }
            }
            return;
        }

        _current = song;
        _lastProgressUtc = DateTime.UtcNow;
        _lastPosition = TimeSpan.Zero;
        _poll.Start();
    }

    public Task StopAsync()
    {
        var token = Interlocked.Increment(ref _playToken);
        Volatile.Write(ref _lastStopToken, token);

        // 先清当前项，避免「停止」的异步收尾把下一首的 Current 抹掉
        _current = null;
        _failureReported = false;

        var task = StopCoreAsync();
        _stopTask = task;
        return task;
    }

    private async Task StopCoreAsync()
    {
        try
        {
            _poll.Stop();
            if (_ready) await _web.CoreWebView2.ExecuteScriptAsync("stopAudio()");
        }
        catch { }
    }

    public async Task SetVolumeAsync(float volume)
    {
        Volume = Math.Max(0f, Math.Min(1f, volume));
        await ApplyVolumeAsync();
    }

    private async Task ApplyVolumeAsync()
    {
        if (!_ready) return;

        try
        {
            var value = Math.Max(0f, Math.Min(1f, Volume));
            await _web.CoreWebView2.ExecuteScriptAsync("setVolume(" + value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")");
            Logger.Write("player volume: " + (int)Math.Round(value * 100) + "%");
        }
        catch (Exception ex)
        {
            Logger.Write("player volume: " + ex.Message);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (!_ready) return;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync("seekTo(" + position.TotalSeconds.ToString("0.###") + ")");
        }
        catch { }
    }

    private async void PollState()
    {
        if (!_ready || _web == null || _polling) return;
        _polling = true;
        try
        {
            var token = Volatile.Read(ref _playToken);
            var song = _current;
            var json = await _web.CoreWebView2.ExecuteScriptAsync("getState()");
            if (string.IsNullOrEmpty(json)) return;
            if (token != Volatile.Read(ref _playToken) || !ReferenceEquals(song, _current)) return;

            // ExecuteScriptAsync 返回的是 JSON 字符串字面量，先解一层
            json = json.Trim();
            if (json.Length >= 2 && json[0] == '"')
            {
                json = Regex.Unescape(json.Substring(1, json.Length - 2));
            }

            var match = StateRegex.Match(json);
            if (!match.Success) return;

            var position = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var duration = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var paused = match.Groups[3].Value == "true";
            var error = int.Parse(match.Groups[4].Value);
            var ended = match.Groups[5].Value == "true";

            if (error != 0 && _current != null && !_backupTried && !string.IsNullOrEmpty(_current.BackupUrl))
            {
                _backupTried = true;
                _lastProgressUtc = DateTime.UtcNow;
                _lastPosition = TimeSpan.Zero;
                await _web.CoreWebView2.ExecuteScriptAsync("playAudio(" + ToJsString(_current.BackupUrl) + ")");
                return;
            }

            // 主地址和备用地址都失败：报一次，让上层跳到下一首，而不是卡住
            if (error != 0 && _current != null && !_failureReported)
            {
                _failureReported = true;
                var failed = Failed;
                if (failed != null)
                {
                    try { failed("音频加载失败（直链可能已过期，或这一条需要登录 B 站）"); }
                    catch (Exception ex) { Logger.Write("player failed handler: " + ex.Message); }
                }

                // 上层收到 Failed 后会停止/切歌；本拍状态已过期，不再推送
                if (token != Volatile.Read(ref _playToken)) return;
            }

            var now = DateTime.UtcNow;
            if (position > _lastPosition.TotalSeconds + 0.2)
            {
                _lastPosition = TimeSpan.FromSeconds(position);
                _lastProgressUtc = now;
            }

            // 自动播放被拦、WebView2 渲染进程卡住、或网络一直起不来：按不同情况设看门狗
            var stuckSeconds = paused && position < 0.2 ? 8 : 20;
            if (_current != null && error == 0 && !ended &&
                (now - _lastProgressUtc).TotalSeconds > stuckSeconds)
            {
                _failureReported = true;
                var failed = Failed;
                if (failed != null)
                {
                    try { failed(paused && position < 0.2
                        ? "音频没有开始播放（可能被自动播放策略拦截）"
                        : "音频播放卡住（网络或 WebView2 无响应）"); }
                    catch (Exception ex) { Logger.Write("player failed handler: " + ex.Message); }
                }

                if (token != Volatile.Read(ref _playToken)) return;
            }

            var state = new PlaybackState
            {
                Playing = !paused && !ended && error == 0,
                Ended = ended,
                Position = TimeSpan.FromSeconds(position),
                Duration = TimeSpan.FromSeconds(duration > 0 ? duration : _current != null ? _current.Duration.TotalSeconds : 0)
            };

            var handler = StateChanged;
            if (handler != null) handler(state);
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private static string ToJsString(string value)
    {
        // Json.Escape 已经带引号，直接当 JS 字符串字面量用
        return Json.Escape(value ?? "");
    }

    public void Dispose()
    {
        try { _poll?.Stop(); _poll?.Dispose(); } catch { }
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); _host?.Dispose(); } catch { }
    }
}
