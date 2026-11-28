using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicToppin;

/// <summary>Spotify 设置：填 Client ID、走浏览器 PKCE 授权、查看账号类型。</summary>
internal sealed class SpotifySetupForm : Form
{
    private readonly SpotifyClient _client;
    private readonly Action<string, string> _persist;
    private readonly TextBox _clientId;
    private readonly Label _status;

    public SpotifySetupForm(SpotifyClient client, string clientId, Action<string, string> persist)
    {
        _client = client;
        _persist = persist;

        Text = "Spotify 点歌设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 300);
        BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = "Spotify 点歌（需要 Premium 才能控制播放）",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 14)
        };
        Controls.Add(title);

        var help = new Label
        {
            Text = "1. 在 https://developer.spotify.com/dashboard 创建 App\n" +
                   "2. 把 Redirect URI 填成：" + SpotifyClient.RedirectUri + "\n" +
                   "3. 把该 App 的 Client ID 填到下面，点「保存并登录」",
            AutoSize = true,
            Location = new Point(18, 46),
            ForeColor = Color.FromArgb(70, 80, 95)
        };
        Controls.Add(help);

        var label = new Label { Text = "Client ID", AutoSize = true, Location = new Point(18, 128) };
        Controls.Add(label);

        _clientId = new TextBox { Location = new Point(90, 124), Width = 448, Text = clientId ?? "" };
        Controls.Add(_clientId);

        var login = new Button { Text = "保存并登录", Location = new Point(90, 160), Width = 120, Height = 32 };
        var check = new Button { Text = "检查状态", Location = new Point(220, 160), Width = 100, Height = 32 };
        var logout = new Button { Text = "退出登录", Location = new Point(330, 160), Width = 100, Height = 32 };
        var close = new Button { Text = "关闭", Location = new Point(440, 160), Width = 98, Height = 32, DialogResult = DialogResult.OK };
        Controls.Add(login);
        Controls.Add(check);
        Controls.Add(logout);
        Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        _status = new Label
        {
            Text = "未登录",
            Location = new Point(18, 206),
            Width = 520,
            Height = 80,
            ForeColor = Color.FromArgb(50, 60, 75)
        };
        Controls.Add(_status);

        login.Click += async (_, _) => await LoginAsync();
        check.Click += async (_, _) => await CheckAsync();
        logout.Click += (_, _) =>
        {
            _client.Logout();
            _persist(_clientId.Text.Trim(), "");
            _status.Text = "已退出登录。";
        };
    }

    private async Task LoginAsync()
    {
        _client.ClientId = _clientId.Text.Trim();
        _persist(_client.ClientId, _client.RefreshToken);
        if (_client.ClientId.Length == 0)
        {
            _status.Text = "请先填写 Client ID。";
            return;
        }

        _status.Text = "已打开浏览器，请在 Spotify 页面完成授权…";
        var error = await _client.LoginAsync(_client.ClientId);
        if (error.Length > 0)
        {
            _status.Text = error;
            return;
        }

        _persist(_client.ClientId, _client.RefreshToken);
        var status = await _client.GetStatusAsync();
        if (status.Error.Length > 0)
        {
            _status.Text = "授权成功，但读取账号状态失败：" + status.Error;
            return;
        }

        _status.Text = status.IsPremium
            ? "已登录：Spotify Premium，可以点歌 / 控制播放。"
            : "已登录：Spotify 非 Premium（" + status.Product + "）。点歌命令会自动降级为外部播放器歌词显示。";
    }

    private async Task CheckAsync()
    {
        _client.ClientId = _clientId.Text.Trim();
        if (!_client.HasLogin)
        {
            _status.Text = "还没有登录 Spotify。";
            return;
        }

        var status = await _client.GetStatusAsync();
        if (status.Error.Length > 0)
        {
            _status.Text = status.Error;
            return;
        }

        _status.Text = status.IsPremium
            ? "Spotify 状态：Premium，可以点歌。"
            : "Spotify 状态：" + status.Product + "（非 Premium：只能显示外部歌词，不能控制播放）。";
    }
}
