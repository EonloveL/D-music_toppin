namespace MusicToppin;

/// <summary>「直播间状态」窗口用的快照。</summary>
internal sealed class LiveStatusSnapshot
{
    public long RoomId;
    public int LiveStatus = -1;      // 1 直播中 / 0 未开播 / -1 未知
    public bool Connected;
    public bool Running;
    public int MessageCount;
    public DateTime LastMessageUtc;
    public string LastError = "";
    public string LastRequestResult = "";
    public DateTime LastPacketUtc;
    public int PacketCount;
    public List<string> Recent = new List<string>();
}

/// <summary>
/// 直播间状态：房间号、开播状态、弹幕连接、收到多少条、最后一条、最近错误、
/// 最近 12 条弹幕原文、最近一次点歌解析结果。
/// 「发点歌没反应」时先看这里：有没有收到弹幕、有没有解析成点歌。
/// </summary>
internal sealed class LiveStatusForm : Form
{
    private readonly Func<LiveStatusSnapshot> _snapshot;
    private readonly Action _reconnect;
    private readonly Action _testPlay;

    private readonly Label _room;
    private readonly Label _live;
    private readonly Label _connection;
    private readonly Label _count;
    private readonly Label _last;
    private readonly Label _error;
    private readonly Label _request;
    private readonly Label _packet;
    private readonly ListBox _recent;

    private int _lastRecentCount = -1;
    private readonly System.Windows.Forms.Timer _timer;

    public LiveStatusForm(Func<LiveStatusSnapshot> snapshot, Action reconnect, Action testPlay)
    {
        _snapshot = snapshot;
        _reconnect = reconnect;
        _testPlay = testPlay;

        Text = "直播间状态 · Music Toppin";
        ClientSize = new Size(560, 512);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(248, 249, 251);

        _room = AddRow("直播间", 0);
        _live = AddRow("开播状态", 1);
        _connection = AddRow("弹幕连接", 2);
        _count = AddRow("收到弹幕", 3);
        _last = AddRow("最后一条", 4);
        _error = AddRow("最近错误", 5);
        _error.ForeColor = Color.FromArgb(176, 88, 60);
        _request = AddRow("最近点歌", 6);
        _packet = AddRow("最后收包", 7);

        var recentLabel = new Label
        {
            Text = "最近弹幕（最新 12 条，含没解析成点歌的）",
            Location = new Point(20, 272),
            Size = new Size(480, 20),
            ForeColor = Color.FromArgb(120, 126, 138)
        };
        Controls.Add(recentLabel);

        _recent = new ListBox
        {
            Location = new Point(20, 294),
            Size = new Size(520, 130),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false
        };
        Controls.Add(_recent);

        var hint = new Label
        {
            Text = "没收到弹幕 → 看「开播状态」和「弹幕连接」；收到了但没入队 → 看「最近点歌」的解析结果。\n" +
                   "「测试点歌」会在本地模拟发一条「点歌 起风了」，用来单独验证播放链路。",
            Location = new Point(20, 432),
            Size = new Size(520, 34),
            ForeColor = Color.FromArgb(120, 126, 138)
        };
        Controls.Add(hint);

        var test = new Button
        {
            Text = "测试点歌",
            Location = new Point(196, 470),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System
        };
        test.Click += (_, _) => _testPlay();

        var reconnectButton = new Button
        {
            Text = "重新连接",
            Location = new Point(300, 470),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System
        };
        reconnectButton.Click += (_, _) => _reconnect();

        var close = new Button
        {
            Text = "关闭",
            Location = new Point(404, 470),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = test;
        CancelButton = close;
        Controls.Add(test);
        Controls.Add(reconnectButton);
        Controls.Add(close);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private Label AddRow(string caption, int index)
    {
        var y = 20 + index * 32;

        var label = new Label
        {
            Text = caption,
            Location = new Point(20, y),
            Size = new Size(90, 22),
            ForeColor = Color.FromArgb(120, 126, 138)
        };

        var value = new Label
        {
            Text = "—",
            Location = new Point(116, y),
            Size = new Size(424, 22),
            ForeColor = Color.FromArgb(30, 34, 42),
            AutoEllipsis = true
        };

        Controls.Add(label);
        Controls.Add(value);
        return value;
    }

    private void RefreshStatus()
    {
        var s = _snapshot();

        _room.Text = s.RoomId > 0 ? s.RoomId.ToString() : "未连接";
        _live.Text = s.LiveStatus == 1 ? "直播中" : s.LiveStatus == 0 ? "未开播（收不到弹幕）" : "未知";
        _connection.Text = s.Connected ? "已连接" : (s.Running ? "连接中 / 重连中" : "未连接");
        _count.Text = s.MessageCount + " 条";

        if (s.LastMessageUtc == DateTime.MinValue)
        {
            _last.Text = "还没收到过";
        }
        else
        {
            var seconds = Math.Max(0, (int)(DateTime.UtcNow - s.LastMessageUtc).TotalSeconds);
            _last.Text = s.LastMessageUtc.ToLocalTime().ToString("HH:mm:ss") + "（" + seconds + " 秒前）";
        }

        _error.Text = string.IsNullOrWhiteSpace(s.LastError) ? "无" : s.LastError;
        _request.Text = string.IsNullOrWhiteSpace(s.LastRequestResult) ? "还没收到点歌" : s.LastRequestResult;

        if (s.LastPacketUtc == DateTime.MinValue)
        {
            _packet.Text = "还没收到过";
        }
        else
        {
            var age = Math.Max(0, (int)(DateTime.UtcNow - s.LastPacketUtc).TotalSeconds);
            _packet.Text = s.LastPacketUtc.ToLocalTime().ToString("HH:mm:ss") + "（" + age + " 秒前）· 共 " + s.PacketCount + " 个包";
        }

        // 最近弹幕：只在变化时重建，避免每秒闪
        if (s.Recent.Count != _lastRecentCount)
        {
            _lastRecentCount = s.Recent.Count;
            _recent.BeginUpdate();
            _recent.Items.Clear();
            foreach (var line in s.Recent) _recent.Items.Add(line);
            if (_recent.Items.Count > 0) _recent.TopIndex = _recent.Items.Count - 1;
            _recent.EndUpdate();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosing(e);
    }
}
