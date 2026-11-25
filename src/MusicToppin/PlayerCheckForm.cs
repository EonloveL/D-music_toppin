namespace MusicToppin;

/// <summary>
/// 「播放器检查」对话框：列出网易云 / QQ 音乐 / 酷狗 的
/// 已安装 / 运行中 / 登录状态，可「再次检查」，也能直接启动没开的客户端。
/// </summary>
internal sealed class PlayerCheckForm : Form
{
    private const int Rows = 2;

    private readonly Label[] _names = new Label[Rows];
    private readonly Label[] _status = new Label[Rows];
    private readonly Button[] _start = new Button[Rows];
    private List<ClientStatus> _clients = new List<ClientStatus>();

    public PlayerCheckForm()
    {
        Text = "播放器检查 · Music Toppin";
        ClientSize = new Size(560, 248);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(248, 249, 251);

        var title = new Label
        {
            Text = "客户端状态（网易云 / QQ 音乐）",
            Location = new Point(20, 14),
            Size = new Size(300, 20),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 34, 42)
        };
        Controls.Add(title);

        for (var i = 0; i < Rows; i++)
        {
            var y = 46 + i * 72;
            var index = i;

            _names[i] = new Label
            {
                Location = new Point(20, y),
                Size = new Size(130, 20),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 34, 42)
            };

            _status[i] = new Label
            {
                Location = new Point(20, y + 24),
                Size = new Size(400, 40),
                ForeColor = Color.FromArgb(86, 92, 104)
            };

            _start[i] = new Button
            {
                Text = "启动",
                Location = new Point(452, y + 16),
                Size = new Size(84, 30),
                FlatStyle = FlatStyle.System,
                Enabled = false
            };
            _start[i].Click += (_, _) => Launch(index);

            Controls.Add(_names[i]);
            Controls.Add(_status[i]);
            Controls.Add(_start[i]);
        }

        var hint = new Label
        {
            Text = "登录请在各客户端里完成（扫码/账号登录）；状态每次打开或点「再次检查」都会重测。",
            Location = new Point(20, 188),
            Size = new Size(520, 18),
            ForeColor = Color.FromArgb(120, 126, 138)
        };
        Controls.Add(hint);

        var refresh = new Button
        {
            Text = "再次检查",
            Location = new Point(336, 208),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System
        };
        refresh.Click += (_, _) => RefreshStatus();

        var close = new Button
        {
            Text = "关闭",
            Location = new Point(440, 208),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = refresh;
        CancelButton = close;
        Controls.Add(refresh);
        Controls.Add(close);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        _clients = SourceCheck.CheckAll();

        for (var i = 0; i < Rows && i < _clients.Count; i++)
        {
            var client = _clients[i];
            _names[i].Text = client.Name;
            _status[i].Text = SourceCheck.StateText(client) + Environment.NewLine + "登录：" + client.LoginText;
            _start[i].Enabled = client.Installed && !client.Running && !string.IsNullOrEmpty(client.ExePath);
        }
    }

    private void Launch(int index)
    {
        if (index < 0 || index >= _clients.Count) return;

        var client = _clients[index];
        if (string.IsNullOrEmpty(client.ExePath)) return;

        try
        {
            Process.Start(client.ExePath);
            Logger.Write("playercheck: started " + client.ExePath);
        }
        catch (Exception ex)
        {
            Logger.Write("playercheck start: " + ex.Message);
            MessageBox.Show(this, "启动失败：" + ex.Message, "Music Toppin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 客户端启动要几秒，稍等再刷新一次状态
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            RefreshStatus();
        };
        timer.Start();
    }
}
