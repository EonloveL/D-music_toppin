using System.Text.RegularExpressions;
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
        "function playAudio(u){var a=document.getElementById('a');a.src=u;a.play().catch(function(){});}" +
        "function stopAudio(){var a=document.getElementById('a');a.pause();a.removeAttribute('src');a.load();}" +
        "function seekTo(t){var a=document.getElementById('a');try{a.currentTime=t;}catch(e){}}" +
        "function getState(){var a=document.getElementById('a');return JSON.stringify({t:a.currentTime||0,d:a.duration||0,p:a.paused,e:a.error?a.error.code:0,ended:a.ended});}" +
        "</script></body></html>";

    private static readonly Regex StateRegex = new Regex(
        "\"t\":([0-9.]+).*?\"d\":([0-9.]+).*?\"p\":(true|false).*?\"e\":(\\d+).*?\"ended\":(true|false)",
        RegexOptions.Compiled);

    private Form _host;
    private WebView2 _web;
    private System.Windows.Forms.Timer _poll;
    private bool _initializing;
    private bool _ready;
    private bool _backupTried;
    private ResolvedSong _current;
    private readonly object _gate = new object();

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

    public async Task EnsureReadyAsync()
    {
        if (_ready) return;
        if (_initializing) return;
        _initializing = true;

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
            var environment = await CoreWebView2Environment.CreateAsync(null, dataDir);
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
            throw new InvalidOperationException(
                "需要 WebView2 运行时（Windows 11 自带；Windows 10 请安装 Edge WebView2 Runtime）。\n" + ex.Message, ex);
        }
        finally
        {
            _initializing = false;
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
                uri.IndexOf("hdslb", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                e.Request.Headers.SetHeader("Referer", "https://www.bilibili.com");
                e.Request.Headers.SetHeader("Origin", "https://www.bilibili.com");
            }
        }
        catch { }
    }

    public async Task PlayAsync(ResolvedSong song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.AudioUrl)) return;

        await EnsureReadyAsync();
        _current = song;
        _backupTried = false;

        await _web.CoreWebView2.ExecuteScriptAsync("playAudio(" + ToJsString(song.AudioUrl) + ")");
        _poll.Start();
    }

    public async Task StopAsync()
    {
        try
        {
            _poll.Stop();
            if (_ready) await _web.CoreWebView2.ExecuteScriptAsync("stopAudio()");
        }
        catch { }
        _current = null;
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
        if (!_ready || _web == null) return;
        try
        {
            var json = await _web.CoreWebView2.ExecuteScriptAsync("getState()");
            if (string.IsNullOrEmpty(json)) return;

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
                await _web.CoreWebView2.ExecuteScriptAsync("playAudio(" + ToJsString(_current.BackupUrl) + ")");
                return;
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
    }

    private static string ToJsString(string value)
    {
        return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    public void Dispose()
    {
        try { _poll?.Stop(); _poll?.Dispose(); } catch { }
        try { _web?.Dispose(); } catch { }
        try { _host?.Close(); _host?.Dispose(); } catch { }
    }
}
