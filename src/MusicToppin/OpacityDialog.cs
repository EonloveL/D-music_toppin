namespace MusicToppin;

/// <summary>背景透明度调节：滑块 + 实时预览（主窗已经开着，改完立刻能看到）。</summary>
internal sealed class OpacityDialog : Form
{
    private readonly TrackBar _slider;
    private readonly Label _value;
    private readonly Action<int> _preview;
    private readonly Action<int> _commit;

    public OpacityDialog(int current, Action<int> preview, Action<int> commit)
    {
        _preview = preview;
        _commit = commit;

        Text = "背景透明度 · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 148);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        TopMost = true;

        var title = new Label
        {
            Text = "背景透明度",
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        _slider = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Max(10, Math.Min(100, current)),
            Location = new Point(18, 48),
            Width = 250
        };

        _value = new Label
        {
            Text = _slider.Value + "%",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(60, 66, 78),
            Location = new Point(272, 58),
            Size = new Size(70, 24)
        };

        var hint = new Label
        {
            Text = "数值越小，游戏画面透出越多",
            ForeColor = Color.FromArgb(140, 146, 158),
            AutoSize = true,
            Location = new Point(22, 96)
        };

        var ok = new Button
        {
            Text = "确定",
            Location = new Point(252, 102),
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

        // 拖动过程中只改内存里的值（实时预览），关闭时才写注册表，避免每动一格写一次盘
        FormClosed += (_, _) =>
        {
            if (_commit != null) _commit(_slider.Value);
        };
    }
}
