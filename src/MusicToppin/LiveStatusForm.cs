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
}

/// <summary>
/// 直播间状态：房间号、开播状态、弹幕连接、收到多少条、最后一条什么时候、最近错误。
/// 用来回答「到底连上没有 / 为什么发点歌没反应」。
/// </summary>
internal sealed class LiveStatusForm : Form
{
    private readonly Func<LiveStatusSnapshot> _snapshot;
    private readonly Action _reconnect;

    private readonly Label _room;
    private readonly Label _live;
    private readonly Label _connection;
    private readonly Label _count;
    private readonly Label _last;
    private readonly Label _error;

    private readonly System.Windows.Forms.Timer _timer;

    public LiveStatusForm(Func<LiveStatusSnapshot> snapshot, Action reconnect)
    {
        _snapshot = snapshot;
        _reconnect = reconnect;

        Text = "直播间状态 · Music Toppin";
        ClientSize = new Size(520, 332);
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

        var hint = new Label
        {
            Text = "未开播时 B 站不推弹幕；开播后点「重新连接」。发点歌没反应时，先看这里有没有「收到弹幕」。",
            Location = new Point(20, 246),
            Size = new Size(480, 32),
            ForeColor = Color.FromArgb(120, 126, 138)
        };
        Controls.Add(hint);

        var reconnectButton = new Button
        {
            Text = "重新连接",
            Location = new Point(300, 288),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System
        };
        reconnectButton.Click += (_, _) => _reconnect();

        var close = new Button
        {
            Text = "关闭",
            Location = new Point(404, 288),
            Size = new Size(96, 30),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = reconnectButton;
        CancelButton = close;
        Controls.Add(reconnectButton);
        Controls.Add(close);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private Label AddRow(string caption, int index)
    {
        var y = 20 + index * 36;

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
            Size = new Size(384, 22),
            ForeColor = Color.FromArgb(30, 34, 42)
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
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosing(e);
    }
}
