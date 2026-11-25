using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MusicToppin;

/// <summary>
/// 应用内登录窗口：直接加载官方登录页，用户扫码/输密码后从 WebView2 里取 cookie。
/// 比「手动复制 cookie」靠谱，也不怕过期——重新登录点一下即可。
/// 用独立的 user data 目录，避免和播放器的 WebView2 抢同一个缓存目录。
/// </summary>
internal sealed class WebLoginForm : Form
{
    private readonly WebView2 _web;
    private readonly string _startUrl;
    private readonly string _cookieUrl;
    private readonly Func<Dictionary<string, string>, bool> _isLoggedIn;
    private readonly System.Windows.Forms.Timer _poll;
    private bool _checking;
    private bool _ready;

    /// <summary>登录成功后的 cookie 串（"a=b; c=d"）。</summary>
    public string CookieHeader { get; private set; } = "";

    /// <summary>登录成功后的 cookie 字典。</summary>
    public Dictionary<string, string> CookieValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <param name="startUrl">打开的登录页地址。</param>
    /// <param name="cookieUrl">按哪个站点取 cookie（如 https://music.163.com）。</param>
    /// <param name="isLoggedIn">判断 cookie 是否已经能代表「登录成功」。</param>
    /// <param name="hint">底部提示文案。</param>
    public WebLoginForm(string title, string startUrl, string cookieUrl,
        Func<Dictionary<string, string>, bool> isLoggedIn, string hint)
    {
        Text = title + " · Music Toppin";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 700);
        MinimumSize = new Size(420, 560);
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        _startUrl = startUrl;
        _cookieUrl = cookieUrl;
        _isLoggedIn = isLoggedIn;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(245, 246, 250) };

        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 8, 0),
            ForeColor = Color.FromArgb(80, 86, 96)
        };

        var cancel = new Button
        {
            Text = "取消",
            Dock = DockStyle.Right,
            Width = 84,
            FlatStyle = FlatStyle.System
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        footer.Controls.Add(hintLabel);
        footer.Controls.Add(cancel);

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        Controls.Add(footer);

        _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        _poll.Tick += (_, _) => _ = CheckCookiesAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicToppin", "WebView2-Login");
            Directory.CreateDirectory(dataDir);

            var environment = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            if (core == null) throw new InvalidOperationException("WebView2 初始化失败");

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Navigate(_startUrl);
            Logger.Write("login form opened: " + _startUrl);

            _ready = true;
            _poll.Start();
        }
        catch (Exception ex)
        {
            Logger.Write("login form init: " + ex.Message);
            MessageBox.Show(this,
                "打不开登录页：需要 WebView2 运行时（Windows 11 自带；Windows 10 请装 Edge WebView2 Runtime）。\n" + ex.Message,
                "Music Toppin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private async Task CheckCookiesAsync()
    {
        if (!_ready || _checking || IsDisposed) return;

        var core = _web.CoreWebView2;
        if (core == null) return;

        _checking = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(_cookieUrl);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cookie in cookies)
            {
                if (string.IsNullOrEmpty(cookie.Name)) continue;
                if (map.ContainsKey(cookie.Name)) continue;
                map[cookie.Name] = cookie.Value ?? "";
            }

            if (!_isLoggedIn(map)) return;

            CookieValues.Clear();
            foreach (var pair in map) CookieValues[pair.Key] = pair.Value;
            CookieHeader = string.Join("; ", map.Select(pair => pair.Key + "=" + pair.Value));

            _poll.Stop();
            Logger.Write("login ok: " + string.Join(",", CookieValues.Keys));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Write("login cookie: " + ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        try { _web.Dispose(); } catch { }
        base.OnFormClosing(e);
    }
}
