namespace MusicToppin;

/// <summary>点歌音量调节：内置播放器的音量，滑块实时预览，确定后落盘。</summary>
internal sealed class VolumeDialog : Form
{
    private readonly TrackBar _slider;
    private readonly Label _value;
    private readonly Action<int> _preview;
    private readonly Action<int> _commit;

    public VolumeDialog(int current, Action<int> preview, Action<int> commit)
    {
        _preview = preview;
        _commit = commit;

        Text = "点歌音量 · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 156);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        TopMost = true;

        var title = new Label
        {
            Text = "点歌音量",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        _slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            Value = Math.Max(0, Math.Min(100, current)),
            Location = new Point(18, 50),
            Width = 300
        };

        _value = new Label
        {
            Text = _slider.Value + "%",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(60, 66, 78),
            Location = new Point(322, 60),
            Size = new Size(60, 24)
        };

        var hint = new Label
        {
            Text = "内置播放器的音量；游戏直播建议 40%–70%，0 为静音",
            ForeColor = Color.FromArgb(140, 146, 158),
            AutoSize = true,
            Location = new Point(22, 104)
        };

        var ok = new Button
        {
            Text = "确定",
            Location = new Point(292, 110),
            Size = new Size(90, 32),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => Close();

        _slider.ValueChanged += (_, _) =>
        {
            _value.Text = _slider.Value + "%";
            if (_preview != null) _preview(_slider.Value);
        };

        Controls.AddRange(new Control[] { title, _slider, _value, hint, ok });

        // 拖动只改内存（实时听效果），关闭时才写注册表
        FormClosed += (_, _) =>
        {
            if (_commit != null) _commit(_slider.Value);
        };
    }
}
