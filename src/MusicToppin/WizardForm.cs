namespace MusicToppin;

/// <summary>
/// 首次启动的上屏向导：不解释 OBS/源/滤镜这些概念，只按“你用的开播软件”给 3 步。
/// </summary>
internal sealed class WizardForm : Form
{
    private readonly RadioButton _obs;
    private readonly RadioButton _jiji;
    private readonly RadioButton _banlv;
    private readonly RadioButton _other;
    private readonly Label _steps;
    private readonly CheckBox _green;
    private readonly Action<bool> _applyGreen;

    private const string ObsSteps =
        "1. OBS 里点「来源」下方的 + → 选「窗口采集」→ 窗口选 Music Toppin\r\n\r\n" +
        "2. 采集方式选「Windows 10 (1903 and up)」，并勾上「允许透明度」\r\n\r\n" +
        "3. 拖动缩放到画面角落。OBS 用这条即可，不需要开绿幕。";

    private const string JijiSteps =
        "1. 先在托盘右键打开「绿幕模式」，浮窗会变成绿底卡片\r\n\r\n" +
        "2. 直播姬里「添加素材」→「窗口捕捉」→ 选 Music Toppin\r\n\r\n" +
        "3. 给这个素材加「抠像 / 色度键」，颜色选纯绿（#00FF00），卡片就浮在画面上了。";

    private const string BanlvSteps =
        "1. 先在托盘右键打开「绿幕模式」，浮窗会变成绿底卡片\r\n\r\n" +
        "2. 直播伴侣里「添加素材」→「窗口」→ 选 Music Toppin\r\n\r\n" +
        "3. 点这个素材的「高级设置」→ 打开「绿幕抠图」→ 颜色选纯绿（#00FF00）。";

    private const string OtherSteps =
        "1. 在开播软件里添加「窗口捕获 / 窗口采集」，窗口选 Music Toppin\r\n\r\n" +
        "2. 如果它支持透明采集：采集方式选 Windows 10 1903 以上，勾「允许透明度」\r\n\r\n" +
        "3. 如果不支持透明：先在托盘打开「绿幕模式」，再给该窗口源加色度键，颜色 #00FF00。";

    public WizardForm(AppSettings settings, Action<bool> applyGreen)
    {
        _applyGreen = applyGreen;

        Text = "上屏向导 · Music Toppin";
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 468);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = "把正在播放显示到直播画面里",
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(24, 20)
        };

        var subtitle = new Label
        {
            Text = "浮窗本身已经有了。要让观众看到，只需在你用的开播软件里「捕获这个窗口」，三步。",
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = false,
            Location = new Point(26, 56),
            Size = new Size(548, 22)
        };

        var ask = new Label
        {
            Text = "你用哪个软件开播？",
            ForeColor = Color.FromArgb(60, 66, 78),
            AutoSize = true,
            Location = new Point(26, 90)
        };

        _obs = MakeRadio("OBS Studio", 26, 114, true);
        _jiji = MakeRadio("哔哩哔哩直播姬", 26, 140, false);
        _banlv = MakeRadio("抖音直播伴侣", 220, 114, false);
        _other = MakeRadio("其他（通用步骤）", 220, 140, false);

        var box = new Panel
        {
            Location = new Point(26, 172),
            Size = new Size(548, 178),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        _steps = new Label
        {
            Location = new Point(14, 12),
            Size = new Size(516, 154),
            ForeColor = Color.FromArgb(40, 44, 54),
            AutoSize = false
        };
        box.Controls.Add(_steps);

        _green = new CheckBox
        {
            Text = "现在切换到绿幕模式（直播姬 / 直播伴侣 需要；OBS 不需要）",
            AutoSize = true,
            Location = new Point(26, 362),
            Checked = settings.Green
        };
        _green.CheckedChanged += (_, _) =>
        {
            if (_applyGreen != null) _applyGreen(_green.Checked);
        };

        var tip = new Label
        {
            Text = "配好后这个窗口可以关掉，浮窗会一直在。下次要再看：托盘右键 →「上屏向导」。",
            ForeColor = Color.FromArgb(140, 146, 158),
            AutoSize = false,
            Location = new Point(26, 392),
            Size = new Size(548, 20)
        };

        var done = new Button
        {
            Text = "完成",
            Location = new Point(446, 418),
            Size = new Size(128, 34),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        done.FlatAppearance.BorderSize = 0;
        done.Click += (_, _) => Close();

        _obs.CheckedChanged += (_, _) => RefreshSteps();
        _jiji.CheckedChanged += (_, _) => RefreshSteps();
        _banlv.CheckedChanged += (_, _) => RefreshSteps();
        _other.CheckedChanged += (_, _) => RefreshSteps();

        Controls.AddRange(new Control[]
        {
            title, subtitle, ask, _obs, _jiji, _banlv, _other, box, _green, tip, done
        });

        RefreshSteps();
    }

    private RadioButton MakeRadio(string text, int x, int y, bool isChecked)
    {
        return new RadioButton
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            Checked = isChecked
        };
    }

    private void RefreshSteps()
    {
        string text;
        if (_obs.Checked) text = ObsSteps;
        else if (_jiji.Checked) text = JijiSteps;
        else if (_banlv.Checked) text = BanlvSteps;
        else text = OtherSteps;

        _steps.Text = text;
    }
}
