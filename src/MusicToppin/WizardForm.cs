namespace MusicToppin;

/// <summary>上屏向导和主程序之间的动作集合，避免向导直接依赖 TrayApp 的内部字段。</summary>
internal sealed class WizardContext
{
    public AppSettings Settings;

    public Func<bool> OverlayRunning;
    public Action<bool> SetOverlay;
    public Func<bool> HudVisible;
    public Action<bool> SetHudVisible;
    public Action<bool> SetGreen;
    public Func<bool> Offscreen;
    public Action<bool> SetOffscreen;
    public Func<bool> Occluded;
    public Action<bool> SetOccluded;

    /// <summary>当前是否为守望先锋模式（该样式只在浏览器源渲染，桌面浮窗隐藏）。</summary>
    public Func<bool> OverwatchMode;

    public Action ConnectLive;
    public Action SyncNetease;
    public Action LoginNetease;
    public Action LoginQQ;
    public Action ShowPlayerCheck;
    public Action OpenLogFolder;

    public Func<string> LiveStatusText;
    public Func<string> SourceStatusText;
    public Action<int> SetRequestPolicy;
    public Action<int> SetSkipPolicy;
}

/// <summary>
/// 上屏向导：按「你用什么软件开播」给出推荐上屏方式，一键完成叠加层 / 绿幕 / 离屏设置，
/// 同时把弹幕点歌、权限、曲源登录和完整功能说明放在同一个窗口里，第一次用也能自己配好。
/// </summary>
internal sealed class WizardForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 26, 32);
    private static readonly Color BodyInk = Color.FromArgb(58, 64, 76);
    private static readonly Color SubInk = Color.FromArgb(112, 120, 134);
    private static readonly Color Accent = Color.FromArgb(255, 59, 48);
    private static readonly Color CardBg = Color.White;
    private static readonly Color PageBg = Color.FromArgb(247, 248, 251);

    private readonly WizardContext _context;
    private readonly TabControl _tabs;
    private Label _screenStatus;
    private Label _liveStatus;
    private Label _sourceStatus;
    private Label _requestHint;
    private Label _skipHint;
    private Label _detected;
    private RichTextBox _steps;
    private RadioButton _obs;
    private RadioButton _jiji;
    private RadioButton _banlv;
    private RadioButton _other;
    private RadioButton _methodOverlay;
    private RadioButton _methodWindow;
    private RadioButton _methodGreen;
    private RadioButton _methodOffscreen;
    private CheckBox _hideHud;
    private Button _apply;
    private ComboBox _requestPolicy;
    private ComboBox _skipPolicy;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly Font _h1;
    private readonly Font _h2;
    private readonly Font _body;
    private readonly Font _mono;
    private readonly string _detectedSoftware;
    private bool _syncing;

    public WizardForm(WizardContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Text = "上屏向导 · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(920, 680);
        BackColor = PageBg;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        TopMost = true;

        _h1 = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        _h2 = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _body = new Font("Microsoft YaHei UI", 9.5f);
        _mono = new Font("Consolas", 9.5f);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 10f),
            Padding = new Point(18, 6)
        };

        var screenPage = BuildScreenPage();
        screenPage.Text = "① 快速上屏";
        var livePage = BuildLivePage();
        livePage.Text = "② 弹幕点歌";
        var featurePage = BuildFeaturePage();
        featurePage.Text = "③ 全部功能";
        var faqPage = BuildFaqPage();
        faqPage.Text = "④ 常见问题";

        _tabs.TabPages.Add(screenPage);
        _tabs.TabPages.Add(livePage);
        _tabs.TabPages.Add(featurePage);
        _tabs.TabPages.Add(faqPage);
        _tabs.SelectedIndex = 0;
        _tabs.SelectedIndexChanged += (_, _) => UpdateAllStatus();

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8) };
        content.Controls.Add(_tabs);

        var header = BuildHeader();
        var bottom = BuildBottomBar();

        Controls.Add(content);
        Controls.Add(bottom);
        Controls.Add(header);

        _detectedSoftware = DetectSoftware();
        InitScreenDefaults();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _statusTimer.Tick += (_, _) => UpdateAllStatus();
    }

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(22, 24, 30) };

        var title = new Label
        {
            Text = "3 分钟完成上屏",
            Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(30, 18)
        };

        var subtitle = new Label
        {
            Text = "先选开播软件，再点一下推荐配置；弹幕点歌、权限和完整功能说明在后面的标签里。",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(190, 198, 212),
            AutoSize = true,
            Location = new Point(32, 58)
        };

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        return header;
    }

    private Panel BuildBottomBar()
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Color.FromArgb(238, 241, 246) };

        var tip = new Label
        {
            Text = "所有设置都会自动保存；下次要再打开：托盘右键 →「上屏向导…」。",
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(22, 20),
            Size = new Size(520, 22)
        };

        var log = new Button
        {
            Text = "打开日志目录",
            Size = new Size(116, 30),
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 2, 10, 0)
        };
        log.Click += (_, _) => _context.OpenLogFolder?.Invoke();

        var done = new Button
        {
            Text = "完成",
            Size = new Size(118, 34),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 0, 0)
        };
        done.FlatAppearance.BorderSize = 0;
        done.Click += (_, _) => Close();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 300,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 11, 20, 0),
            WrapContents = false
        };
        actions.Controls.Add(done);
        actions.Controls.Add(log);

        bottom.Controls.Add(tip);
        bottom.Controls.Add(actions);
        return bottom;
    }

    // ---------------------------------------------------------------- 上屏设置页

    private TabPage BuildScreenPage()
    {
        var page = NewPage();

        var left = new Panel { Location = new Point(0, 0), Size = new Size(432, 468), BackColor = PageBg };
        var right = new Panel { Location = new Point(448, 0), Size = new Size(430, 468), BackColor = PageBg };

        var step1 = MakeGroup("第 1 步 · 你用什么软件开播？", 0, 0, 432, 154);
        _obs = MakeRadio("OBS Studio（推荐）", 16, 28, 390);
        _jiji = MakeRadio("哔哩哔哩直播姬", 16, 54, 390);
        _banlv = MakeRadio("抖音直播伴侣", 16, 80, 390);
        _other = MakeRadio("其他软件 / 不确定", 16, 106, 390);
        _detected = new Label
        {
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(16, 130),
            Size = new Size(400, 20)
        };
        step1.Controls.AddRange(new Control[] { _obs, _jiji, _banlv, _other, _detected });

        var step2 = MakeGroup("第 2 步 · 选择上屏方式（已按软件推荐）", 0, 166, 432, 212);
        _methodOverlay = MakeRadio("浏览器源叠加层（OBS 推荐，桌面无浮窗）", 16, 28, 400);
        _methodWindow = MakeRadio("窗口采集 + 允许透明度（OBS 备选）", 16, 56, 400);
        _methodGreen = MakeRadio("绿幕 + 窗口采集（直播姬 / 直播伴侣）", 16, 84, 400);
        _methodOffscreen = MakeRadio("离屏采集（窗口采集抓不到屏幕外窗口时）", 16, 112, 400);
        _hideHud = new CheckBox
        {
            Text = "同时隐藏桌面浮窗（观众仍能在直播里看到）",
            AutoSize = true,
            Location = new Point(18, 144),
            ForeColor = BodyInk
        };
        var methodHint = new Label
        {
            Text = "不确定就用第一项：桌面干净，OBS 直接加浏览器源。",
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(18, 170),
            Size = new Size(400, 24)
        };
        step2.Controls.AddRange(new Control[] { _methodOverlay, _methodWindow, _methodGreen, _methodOffscreen, _hideHud, methodHint });

        _apply = new Button
        {
            Text = "一键应用推荐配置",
            Location = new Point(0, 388),
            Size = new Size(432, 42),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _apply.FlatAppearance.BorderSize = 0;
        _apply.Click += (_, _) => ApplyRecommended();

        _screenStatus = new Label
        {
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(2, 434),
            Size = new Size(430, 34)
        };

        left.Controls.Add(step1);
        left.Controls.Add(step2);
        left.Controls.Add(_apply);
        left.Controls.Add(_screenStatus);

        var step3 = MakeGroup("第 3 步 · 在开播软件里这样添加", 0, 0, 430, 468);
        _steps = MakeRichBox(new Point(12, 24), new Size(406, 430));

        step3.Controls.Add(_steps);
        right.Controls.Add(step3);

        page.Controls.Add(left);
        page.Controls.Add(right);

        _obs.CheckedChanged += (_, _) => OnSoftwareChanged();
        _jiji.CheckedChanged += (_, _) => OnSoftwareChanged();
        _banlv.CheckedChanged += (_, _) => OnSoftwareChanged();
        _other.CheckedChanged += (_, _) => OnSoftwareChanged();
        _methodOverlay.CheckedChanged += (_, _) => OnMethodChanged();
        _methodWindow.CheckedChanged += (_, _) => OnMethodChanged();
        _methodGreen.CheckedChanged += (_, _) => OnMethodChanged();
        _methodOffscreen.CheckedChanged += (_, _) => OnMethodChanged();

        return page;
    }

    private void InitScreenDefaults()
    {
        _syncing = true;
        try
        {
            var settings = _context.Settings;
            var overlay = settings != null && settings.Overlay;
            var green = settings != null && settings.Green;
            var offscreen = settings != null && settings.CaptureOffscreen;
            var occluded = settings != null && settings.CaptureOccluded;

            // 先按当前实际模式选软件，避免打开向导后「一键应用」把正在用的绿幕/离屏悄悄换掉
            if (overlay)
            {
                _obs.Checked = true;
            }
            else if (green || offscreen || occluded)
            {
                if (_detectedSoftware == "jiji") _jiji.Checked = true;
                else if (_detectedSoftware == "banlv") _banlv.Checked = true;
                else if (_detectedSoftware == "obs") _obs.Checked = true;
                else if (_detectedSoftware.Length > 0) _other.Checked = true;
                else _jiji.Checked = true;
            }
            else if (_detectedSoftware == "jiji") _jiji.Checked = true;
            else if (_detectedSoftware == "banlv") _banlv.Checked = true;
            else if (_detectedSoftware == "obs") _obs.Checked = true;
            else if (_detectedSoftware.Length > 0) _other.Checked = true;
            else _obs.Checked = true;

            if (offscreen) _methodOffscreen.Checked = true;
            else if (green) _methodGreen.Checked = true;
            else if (overlay) _methodOverlay.Checked = true;
            else if (occluded) _methodWindow.Checked = true;
            else SelectRecommendedMethod();

            _hideHud.Checked = overlay && settings != null && !settings.Visible;
            _hideHud.Enabled = _methodOverlay.Checked;
            _detected.Text = DescribeDetected();
        }
        finally
        {
            _syncing = false;
        }

        UpdateSteps();
        UpdateScreenStatus();
    }

    private string DescribeDetected()
    {
        switch (_detectedSoftware)
        {
            case "obs": return "已检测到 OBS Studio。";
            case "jiji": return "已检测到哔哩哔哩直播姬。";
            case "banlv": return "已检测到抖音直播伴侣。";
            default: return "没检测到开播软件，先按 OBS 的步骤显示；不对就点上面的选项。";
        }
    }

    private void OnSoftwareChanged()
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            SelectRecommendedMethod();
            _hideHud.Enabled = _methodOverlay.Checked;
        }
        finally
        {
            _syncing = false;
        }

        UpdateSteps();
    }

    private void SelectRecommendedMethod()
    {
        if (_jiji.Checked || _banlv.Checked)
        {
            _methodGreen.Checked = true;
            _hideHud.Checked = false;
        }
        else if (_other.Checked)
        {
            _methodWindow.Checked = true;
            _hideHud.Checked = false;
        }
        else
        {
            _methodOverlay.Checked = true;
            _hideHud.Checked = true;
        }
    }

    private void OnMethodChanged()
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (_methodGreen.Checked || _methodWindow.Checked || _methodOffscreen.Checked)
            {
                _hideHud.Checked = false;
            }
            else
            {
                _hideHud.Checked = true;
            }

            _hideHud.Enabled = _methodOverlay.Checked;
        }
        finally
        {
            _syncing = false;
        }

        UpdateSteps();
    }

    private void UpdateSteps()
    {
        if (_steps == null) return;

        _steps.Clear();

        if (_methodOverlay.Checked)
        {
            AddRich(_steps, "浏览器源叠加层（OBS 推荐）", _h2, Ink);
            AddRich(_steps, "1. 点下面的「一键应用推荐配置」：自动开启叠加层，并把地址复制到剪贴板。", _body, BodyInk);
            AddRich(_steps, "2. OBS → 来源 → 点「+」→ 选「浏览器」→ 把地址粘贴进去。", _body, BodyInk);
            var ow = _context.OverwatchMode != null && _context.OverwatchMode();
            AddRich(_steps, ow ? "3. 宽 1920、高 1080（16:9 全画布，别用 400×320）。圆圈按游戏默认 HUD 缩放 100% 对位，调过 HUD 缩放会偏移。" : "3. 宽 400、高 320；勾选「关闭源时关闭页面」。", _body, BodyInk);
            AddRich(_steps, "4. 需要视频点播再加一个浏览器源：地址后面加 /video，建议 480×270。", _body, BodyInk);
            AddRich(_steps, ow ? "5. 守望先锋模式的主题在托盘「样式 → 主题」切换；桌面浮窗在该样式下自动隐藏。" : "5. 桌面浮窗可以关掉，观众端照常显示；颜色、透明度、进度都和桌面一致。", _body, BodyInk);
            AddRich(_steps, "", _body, BodyInk);
            AddRich(_steps, "适合：OBS Studio、支持「浏览器源」的直播软件。", _mono, SubInk);
        }
        else if (_methodGreen.Checked)
        {
            AddRich(_steps, "绿幕 + 窗口采集（直播姬 / 直播伴侣）", _h2, Ink);
            AddRich(_steps, "1. 点下面的「一键应用推荐配置」：浮窗变成绿底卡片。", _body, BodyInk);
            AddRich(_steps, "2. 直播姬：「添加素材」→「窗口捕捉」→ 选窗口 Music Toppin。", _body, BodyInk);
            AddRich(_steps, "3. 直播伴侣：「添加素材」→「窗口」→ 选 Music Toppin。", _body, BodyInk);
            AddRich(_steps, "4. 给这个素材加「绿幕抠图 / 色度键」，颜色选纯绿 #00FF00。", _body, BodyInk);
            AddRich(_steps, "5. OBS 参考参数：相似度 400、平滑 80、键色溢出 100。", _body, BodyInk);
            AddRich(_steps, "", _body, BodyInk);
            AddRich(_steps, "绿幕模式下卡片会保持不透明，避免透明卡片把绿底透出来。", _mono, SubInk);
        }
        else if (_methodOffscreen.Checked)
        {
            AddRich(_steps, "离屏采集（窗口采集专用）", _h2, Ink);
            AddRich(_steps, "1. 点下面的「一键应用推荐配置」：浮窗会被移到所有显示器之外。", _body, BodyInk);
            AddRich(_steps, "2. 直播软件里添加「窗口采集」，窗口选 Music Toppin。", _body, BodyInk);
            AddRich(_steps, "3. 主播屏幕上看不到任何东西，但采集软件仍能抓到窗口。", _body, BodyInk);
            AddRich(_steps, "4. 想恢复：再点一次托盘里的「离屏采集」，或使用上屏向导重新配置。", _body, BodyInk);
            AddRich(_steps, "", _body, BodyInk);
            AddRich(_steps, "注意：采集端必须支持抓取屏幕外窗口；不支持时改用绿幕。", _mono, SubInk);
        }
        else
        {
            AddRich(_steps, "窗口采集 + 允许透明度（OBS）", _h2, Ink);
            AddRich(_steps, "1. 点下面的「一键应用推荐配置」：关闭绿幕，桌面浮窗保持显示。", _body, BodyInk);
            AddRich(_steps, "2. OBS → 来源 → 点「+」→ 选「窗口采集」。", _body, BodyInk);
            AddRich(_steps, "3. 窗口选 Music Toppin；采集方式选「Windows 10 (1903 and up)」。", _body, BodyInk);
            AddRich(_steps, "4. 勾选「允许透明度」，卡片就会透明地叠在画面上。", _body, BodyInk);
            AddRich(_steps, "5. 拖动缩放到画面角落即可。", _body, BodyInk);
            AddRich(_steps, "", _body, BodyInk);
            AddRich(_steps, "适合：OBS，以及支持透明窗口采集的软件。", _mono, SubInk);
        }

        ResetScroll(_steps);
    }

    private void ApplyRecommended()
    {
        try
        {
            // 向导不提供遮挡模式；如果之前开了，先退出，避免配好之后浮窗被压在窗口后面
            if (_context.Occluded != null && _context.Occluded()) _context.SetOccluded?.Invoke(false);

            // 守望先锋模式只有浏览器源一种用法：忽略其它选项，避免关掉叠加层后彻底没有画面
            if (_context.OverwatchMode != null && _context.OverwatchMode())
            {
                _context.SetOverlay?.Invoke(true);
                _context.SetGreen?.Invoke(false);
                if (_context.Offscreen != null && _context.Offscreen()) _context.SetOffscreen?.Invoke(false);
                _context.SetHudVisible?.Invoke(false);
                MessageBox.Show(this,
                    "当前样式是「守望先锋模式（显示歌词）」，只能使用浏览器源：\n\n" +
                    "OBS → 来源 → 浏览器，宽 1920 高 1080。\n" +
                    "桌面浮窗在该样式下自动隐藏。想用窗口采集/绿幕，请先切回其它样式。",
                    "Music Toppin", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateScreenStatus();
                return;
            }

            if (_methodOverlay.Checked)
            {
                _context.SetOverlay?.Invoke(true);
                _context.SetGreen?.Invoke(false);
                if (_context.Offscreen != null && _context.Offscreen()) _context.SetOffscreen?.Invoke(false);
                _context.SetHudVisible?.Invoke(!_hideHud.Checked);
            }
            else if (_methodGreen.Checked)
            {
                _context.SetOverlay?.Invoke(false);
                if (_context.Offscreen != null && _context.Offscreen()) _context.SetOffscreen?.Invoke(false);
                _context.SetHudVisible?.Invoke(true);
                _context.SetGreen?.Invoke(true);
            }
            else if (_methodOffscreen.Checked)
            {
                _context.SetOverlay?.Invoke(false);
                _context.SetGreen?.Invoke(false);
                _context.SetHudVisible?.Invoke(true);
                _context.SetOffscreen?.Invoke(true);
            }
            else
            {
                _context.SetOverlay?.Invoke(false);
                _context.SetGreen?.Invoke(false);
                if (_context.Offscreen != null && _context.Offscreen()) _context.SetOffscreen?.Invoke(false);
                _context.SetHudVisible?.Invoke(true);
            }

            UpdateScreenStatus();
        }
        catch (Exception ex)
        {
            Logger.Write("wizard apply: " + ex.Message);
            MessageBox.Show(this, "应用配置失败：" + ex.Message, "Music Toppin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateScreenStatus()
    {
        if (_screenStatus == null || _context == null) return;

        var settings = _context.Settings;
        var overlay = _context.OverlayRunning != null && _context.OverlayRunning();
        var hud = _context.HudVisible != null && _context.HudVisible();
        var wantVisible = settings == null || settings.Visible;
        var green = settings != null && settings.Green;
        var offscreen = settings != null && settings.CaptureOffscreen;
        var occluded = settings != null && settings.CaptureOccluded;

        var hudText = wantVisible
            ? (hud ? "显示中" : "显示中（当前无播放，已自动收起）")
            : "已隐藏";

        var text = "叠加层：" + (overlay ? "已开启（地址已复制）" : "未开启")
                   + "　·　桌面浮窗：" + hudText
                   + "　·　绿幕：" + (green ? "开" : "关");
        if (offscreen) text += "　·　离屏采集：开";
        if (occluded) text += "　·　遮挡模式：开";

        _screenStatus.Text = text;
    }

    // ---------------------------------------------------------------- 弹幕点歌页

    private TabPage BuildLivePage()
    {
        var page = NewPage();

        var left = new Panel { Location = new Point(0, 0), Size = new Size(432, 468), BackColor = PageBg };
        var right = new Panel { Location = new Point(448, 0), Size = new Size(430, 468), BackColor = PageBg };

        var connectGroup = MakeGroup("连接直播间", 0, 0, 432, 142);
        _liveStatus = new Label
        {
            Text = "—",
            ForeColor = BodyInk,
            AutoSize = false,
            Location = new Point(14, 24),
            Size = new Size(404, 40)
        };
        var connect = MakeSecondaryButton("连接 / 更换直播间…", 14, 68, 190, 32);
        connect.Click += (_, _) =>
        {
            _context.ConnectLive?.Invoke();
            UpdateLiveStatus();
        };
        var check = MakeSecondaryButton("播放器检查…", 214, 68, 120, 32);
        check.Click += (_, _) =>
        {
            _context.ShowPlayerCheck?.Invoke();
            UpdateSourceStatus();
        };
        var connectHint = new Label
        {
            Text = "输入房间号或直播间链接；未开播时 B 站不会推送弹幕。",
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(14, 108),
            Size = new Size(404, 22)
        };
        connectGroup.Controls.AddRange(new Control[] { _liveStatus, connect, check, connectHint });

        var sourceGroup = MakeGroup("曲源登录", 0, 152, 432, 140);
        _sourceStatus = new Label
        {
            Text = "—",
            ForeColor = BodyInk,
            AutoSize = false,
            Location = new Point(14, 24),
            Size = new Size(404, 40)
        };
        var syncNetease = MakeSecondaryButton("同步网易云客户端", 14, 66, 140, 30);
        syncNetease.Click += (_, _) =>
        {
            _context.SyncNetease?.Invoke();
            UpdateSourceStatus();
        };
        var loginNetease = MakeSecondaryButton("网易云扫码", 160, 66, 110, 30);
        loginNetease.Click += (_, _) =>
        {
            _context.LoginNetease?.Invoke();
            UpdateSourceStatus();
        };
        var loginQQ = MakeSecondaryButton("QQ 音乐扫码", 276, 66, 140, 30);
        loginQQ.Click += (_, _) =>
        {
            _context.LoginQQ?.Invoke();
            UpdateSourceStatus();
        };
        var sourceHint = new Label
        {
            Text = "网易云客户端登录过会自动同步；QQ 音乐不登录时多数歌放不了。",
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(14, 104),
            Size = new Size(404, 32)
        };
        sourceGroup.Controls.AddRange(new Control[] { _sourceStatus, syncNetease, loginNetease, loginQQ, sourceHint });

        var policyGroup = MakeGroup("权限设置（弹幕指令）", 0, 300, 432, 168);
        var requestLabel = new Label { Text = "点歌权限", ForeColor = BodyInk, AutoSize = true, Location = new Point(14, 26) };
        _requestPolicy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(100, 22),
            Width = 316
        };
        _requestPolicy.Items.AddRange(new object[]
        {
            "所有人可点歌",
            "粉丝团灯牌 或 舰长",
            "仅舰长 / 大航海",
            "关闭点歌"
        });
        _requestPolicy.SelectedIndex = ClampIndex(_context.Settings != null ? _context.Settings.RequestPolicy : 1, 4);
        _requestPolicy.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            _context.SetRequestPolicy?.Invoke(_requestPolicy.SelectedIndex);
            UpdatePolicyHints();
        };

        _requestHint = new Label
        {
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(100, 48),
            Size = new Size(316, 34)
        };

        var skipLabel = new Label { Text = "切歌权限", ForeColor = BodyInk, AutoSize = true, Location = new Point(14, 88) };
        _skipPolicy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(100, 84),
            Width = 316
        };
        _skipPolicy.Items.AddRange(new object[]
        {
            "所有人可切",
            "仅点歌人（房管可切全部）",
            "仅房管",
            "关闭切歌"
        });
        _skipPolicy.SelectedIndex = ClampIndex(_context.Settings != null ? _context.Settings.SkipPolicy : 1, 4);
        _skipPolicy.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            _context.SetSkipPolicy?.Invoke(_skipPolicy.SelectedIndex);
            UpdatePolicyHints();
        };

        _skipHint = new Label
        {
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(100, 110),
            Size = new Size(316, 34)
        };

        var policyNote = new Label
        {
            Text = "权限只影响弹幕指令；托盘菜单和「点歌测试」不受限制。",
            ForeColor = SubInk,
            AutoSize = false,
            Location = new Point(14, 146),
            Size = new Size(404, 18)
        };

        policyGroup.Controls.AddRange(new Control[]
        {
            requestLabel, _requestPolicy, _requestHint,
            skipLabel, _skipPolicy, _skipHint, policyNote
        });

        left.Controls.Add(connectGroup);
        left.Controls.Add(sourceGroup);
        left.Controls.Add(policyGroup);

        var commandGroup = MakeGroup("观众指令速查", 0, 0, 430, 468);
        var commandHelp = MakeRichBox(new Point(12, 24), new Size(406, 430));
        WriteCommandHelp(commandHelp);
        commandGroup.Controls.Add(commandHelp);
        right.Controls.Add(commandGroup);

        page.Controls.Add(left);
        page.Controls.Add(right);

        UpdatePolicyHints();
        return page;
    }

    private void UpdatePolicyHints()
    {
        if (_requestHint == null || _skipHint == null) return;

        switch (_requestPolicy.SelectedIndex)
        {
            case 1: _requestHint.Text = "需要挂本直播间的粉丝团灯牌，或开通舰长 / 提督 / 总督。"; break;
            case 2: _requestHint.Text = "只有大航海（舰长 / 提督 / 总督）可以点歌。"; break;
            case 3: _requestHint.Text = "弹幕点歌已关闭；托盘里的「点歌测试」仍可使用。"; break;
            default: _requestHint.Text = "普通观众也能点歌，适合开放的直播间。"; break;
        }

        switch (_skipPolicy.SelectedIndex)
        {
            case 1: _skipHint.Text = "普通观众只能切自己点的歌；房管可以切全部歌曲。"; break;
            case 2: _skipHint.Text = "只有房管可以切歌，普通观众发「切歌」无效。"; break;
            case 3: _skipHint.Text = "弹幕切歌完全关闭，只能从托盘菜单手动切。"; break;
            default: _skipHint.Text = "普通观众也能切任意歌；房管当然也可以。"; break;
        }
    }

    private void WriteCommandHelp(RichTextBox box)
    {
        AddRich(box, "点歌（只认「点歌」开头）", _h2, Ink);
        AddRich(box, "点歌 起风了", _mono, BodyInk);
        AddRich(box, "点歌 wy 起风了", _mono, BodyInk);
        AddRich(box, "点歌 qq 夜に駆ける", _mono, BodyInk);
        AddRich(box, "点歌 id:1330348068", _mono, BodyInk);
        AddRich(box, "点歌 BV1xx411c7mD", _mono, BodyInk);
        AddRich(box, "点歌 b23.tv/xxxx", _mono, BodyInk);
        AddRich(box, "点歌 am Lemon（会入队，但暂时放不了）", _mono, SubInk);
        AddRich(box, "也支持「qq 歌名」「wy 歌名」直接开头，不用写「点歌」。", _body, SubInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "切歌", _h2, Ink);
        AddRich(box, "切歌 / 换歌 / 下一首 / 跳过", _mono, BodyInk);
        AddRich(box, "权限为「仅点歌人」时：普通观众只能切自己点的歌；房管可以切全部。", _body, BodyInk);
        AddRich(box, "权限为「仅房管」时：只有带房管标识的观众可以切。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "撤回还没轮到的歌", _h2, Ink);
        AddRich(box, "撤回", _mono, BodyInk);
        AddRich(box, "撤回 2（队列第 2 首）", _mono, BodyInk);
        AddRich(box, "撤回 起风了（按歌名唯一匹配）", _mono, BodyInk);
        AddRich(box, "正在播的那首不能撤回，请用「切歌」。", _body, SubInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "点歌权限说明", _h2, Ink);
        AddRich(box, "粉丝团灯牌必须是本直播间的；舰长 / 提督 / 总督都按「舰长」处理。", _body, BodyInk);
        AddRich(box, "点歌、切歌、撤回的权限都在托盘「弹幕点歌」菜单和本页随时可改。", _body, BodyInk);
        ResetScroll(box);
    }

    // ---------------------------------------------------------------- 功能说明页

    private TabPage BuildFeaturePage()
    {
        var page = NewPage();
        var box = MakeRichBox(new Point(0, 0), new Size(876, 468));
        box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        WriteFeatureHelp(box);
        page.Controls.Add(box);
        return page;
    }

    private void WriteFeatureHelp(RichTextBox box)
    {
        AddRich(box, "一、这个工具能做什么", _h1, Ink);
        AddRich(box, "把「系统正在播放的音乐」用击杀播报卡片显示出来，给 OBS / 直播姬 / 直播伴侣采集上屏。纯显示模式不需要登录，也不改变你的播放器；点歌功能才使用应用内播放器。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "二、两条读取通道", _h1, Ink);
        AddRich(box, "① SMTC（Windows「正在播放」）：Edge / Chrome 网页音乐、B 站视频、Spotify、Apple Music、Windows 媒体播放器。能拿到歌名、歌手、进度和播放状态。", _body, BodyInk);
        AddRich(box, "② 窗口标题 + 按进程音频会话：网易云、QQ 音乐、酷狗、酷我、咪咕、PotPlayer、foobar2000、AIMP、MusicBee 等。能拿到歌名/歌手，没有进度；用该进程是否在出声判断播放/暂停。", _body, BodyInk);
        AddRich(box, "选源带滞回：当前源还在就保持，换源要连续两拍确认，避免暂停的浏览器和正在放歌的网易云来回跳。", _body, SubInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "三、卡片显示", _h1, Ink);
        AddRich(box, "· 主卡片：完整歌名、完整歌手（自动换行）、上一首、播放进度。", _body, BodyInk);
        AddRich(box, "· 下方灰卡：「播放完毕」和「更早播放」各一张，宽度递减、透明度递减，切歌时做 200ms 堆叠动画；贴左侧时灰卡向左展开，贴右侧保持纵向堆叠。", _body, BodyInk);
        AddRich(box, "· 暂停时红点转灰并显示「已暂停」；没有会话时自动隐藏。", _body, BodyInk);
        AddRich(box, "· 点歌播放时多一行「下一首 ↓」和队列长度，队列空了自动收起。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "四、样式、位置与鼠标", _h1, Ink);
        AddRich(box, "· 样式：击杀播报 / 极简 / 环状毛玻璃 / 守望先锋模式（显示歌词）。环状样式可放在屏幕上/下/左/右，圆心在屏幕外，上一首/下一首分列两侧，切歌时沿圆周滚动；本应用内回退到上一首时反向滚动。守望先锋模式在主叠层显示底部圆圈 + 左右卡片 + 歌词，主题在「样式 → 主题」切换：无装饰 / 暹罗猫 / 腊肠狗 / 德文猫 / 春节 / 圣诞 / 万圣 / 中秋 / 情人。歌词只对本应用播放（点歌 / 默认歌单）生效，外部播放器不显示歌词。", _body, BodyInk);
        AddRich(box, "· 托盘「播放控制 → 上一首」回退本应用本次运行内播放过的歌（最多 12 首，重启清空）；「重播当前」只重头播当前；外部播放器走系统切歌。", _body, BodyInk);
        AddRich(box, "· 5 套配色预设 + 主题色/歌名/歌手自定义；背景透明度 100%~10% 可调，只影响背景不影响文字。环状样式下当前卡文字不发光，上一首/下一首带很轻的同色光晕。", _body, BodyInk);
        AddRich(box, "· 位置：左上 / 右上 / 左下 / 右下 / 左侧 / 右侧 / 居中，也可以解锁后自由拖动；记住锚点，分辨率或 DPI 变化后自动贴边，支持负坐标副屏。", _body, BodyInk);
        AddRich(box, "· 默认鼠标穿透，打游戏不会吞点击；按锁定快捷键解锁编辑，12 秒无操作自动回锁。", _body, BodyInk);
        AddRich(box, "· 反作弊保守模式：禁用媒体键切歌，不再向系统注入上一首/下一首按键；不影响点歌和默认歌单。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "五、上屏方式", _h1, Ink);
        AddRich(box, "· OBS 窗口采集：采集方式选 Windows 10 (1903 and up) 并勾「允许透明度」，画质好、实现简单。", _body, BodyInk);
        AddRich(box, "· 浏览器源叠加层：本地 HTTP 页面，桌面浮窗可以隐藏，观众仍能看到；端口 4681 起自动找空闲，设置会记住。", _body, BodyInk);
        AddRich(box, "· 绿幕：浮窗变纯绿底 + 卡片，四周留 12px 绿边，适合直播姬 / 直播伴侣抠像；绿幕下卡片保持不透明。", _body, BodyInk);
        AddRich(box, "· 离屏采集 / 遮挡模式：给只能抓窗口的采集软件用；三种采集模式互斥。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "六、弹幕点歌", _h1, Ink);
        AddRich(box, "· 托盘「弹幕点歌 → 连接直播间」，输入 B 站房间号或直播间链接；断线自动重连，直播间号会记住。", _body, BodyInk);
        AddRich(box, "· 曲源：网易云（关键词 / id:）、QQ 音乐（qq 前缀）、B 站视频（BV / b23 短链）；Apple Music 目前只入队不播放。", _body, BodyInk);
        AddRich(box, "· 默认歌单：托盘「默认歌单」→ 设置网易云或 QQ 音乐歌单链接/ID，二选一；默认歌单持续播放，点歌排在当前曲目之后，队列空了自动继续默认歌单。", _body, BodyInk);
        AddRich(box, "· 队列规则：同一首歌 5 分钟内不重复、同一用户 30 秒冷却、最多 20 首、先点先放；同一时间只放一首。", _body, BodyInk);
        AddRich(box, "· 第一首自动开播，放完自动下一首；放不了会提示并跳过，不会卡住。点歌音量默认 60%。", _body, BodyInk);
        AddRich(box, "· B 站视频点播会在叠加层 /video 小窗播放，只出现在直播画面里；声音默认走应用播放器，保证 OBS 有声。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "七、权限", _h1, Ink);
        AddRich(box, "· 点歌：所有人 / 粉丝团灯牌或舰长 / 仅舰长（大航海）/ 关闭。粉丝团灯牌要求是本直播间的牌子。", _body, BodyInk);
        AddRich(box, "· 切歌：所有人 / 仅点歌人（房管可切全部）/ 仅房管 / 关闭。房管在「仅点歌人」和「仅房管」模式下都可以切全部。", _body, BodyInk);
        AddRich(box, "· 撤回：所有人 / 仅本人 / 关闭；正在播的歌只能切歌，不能撤回。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "八、曲源登录与低占用", _h1, Ink);
        AddRich(box, "· 网易云优先「从官方客户端同步登录」：本机读取客户端会话，不上传；也可以应用内扫码。", _body, BodyInk);
        AddRich(box, "· QQ 音乐应用内扫码登录后取流更完整；「播放器检查」能看到已安装 / 运行中 / 登录状态。", _body, BodyInk);
        AddRich(box, "· 纯显示模式不加载 WebView2，内存约 25–33 MB；点歌播放时才加载，约 +160 MB。", _body, BodyInk);
        AddRich(box, "· 更新：启动后自动检查，也可以托盘「检查更新…」；下载后校验 SHA256，校验不过会丢弃。", _body, BodyInk);
        ResetScroll(box);
    }

    // ---------------------------------------------------------------- 常见问题页

    private TabPage BuildFaqPage()
    {
        var page = NewPage();
        var box = MakeRichBox(new Point(0, 0), new Size(876, 468));
        box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        WriteFaq(box);
        page.Controls.Add(box);
        return page;
    }

    private void WriteFaq(RichTextBox box)
    {
        AddRich(box, "观众看不到卡片？", _h2, Ink);
        AddRich(box, "先在桌面确认浮窗显示正常，再检查采集：OBS 窗口采集要勾「允许透明度」；直播姬 / 直播伴侣要用绿幕模式并加色度键；用浏览器源时确认地址端口和宽高。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "卡片挡住游戏 / 鼠标被吞？", _h2, Ink);
        AddRich(box, "浮窗默认是鼠标穿透。要是点到了卡片，按托盘第一行显示的锁定快捷键解锁/回锁；也可以在托盘菜单里手动锁定。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "为什么没有进度条？", _h2, Ink);
        AddRich(box, "窗口标题通道拿不到时长；Apple Music 的 SMTC 也会把时长报成 0。卡片会自动隐藏进度条，这是系统限制，不是故障。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "歌停了卡片还不消失？", _h2, Ink);
        AddRich(box, "窗口通道依赖该进程的音频会话。输出到独占模式 / WASAPI exclusive 时可能查不到状态，会退回端点峰值；如果桌面浮窗还在，按「显示浮窗」隐藏即可。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "观众点歌没反应？", _h2, Ink);
        AddRich(box, "按顺序查：① 直播间是否开播（未开播 B 站不推弹幕）；② 托盘「直播间状态…」里有没有收到弹幕；③ 点歌权限是否把普通观众挡住了；④ 曲源是否登录、有没有版权；⑤ 日志里的 song request / resolved / queue play。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "点歌入队了但放不了？", _h2, Ink);
        AddRich(box, "常见原因是无版权、需要登录、QQ 音乐未登录或直链过期。队列会自动跳过并提示；网易云可以在「曲源（登录）」里同步客户端或扫码，QQ 音乐需要应用内扫码。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "视频小窗黑屏？", _h2, Ink);
        AddRich(box, "部分 UP 主关闭了站外播放，视频小窗会报错，但音频仍会正常播放。也可以把「播放控制 → 视频声音」切回「走应用播放器」。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "怎么确认权限生效？", _h2, Ink);
        AddRich(box, "在「弹幕点歌」页把点歌权限改成「粉丝团灯牌 或 舰长」，再用普通账号发「点歌 起风了」；开启浏览器源叠加层时底部会提示需要灯牌，日志里会出现「song request denied」，也可以在「直播间状态」看最近一次点歌结果。切歌权限选「仅点歌人」后，非点歌人发「切歌」会被拒绝，房管仍然可以切。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "升级后设置还在吗？", _h2, Ink);
        AddRich(box, "设置存在 HKCU\\Software\\MusicToppin，正常覆盖安装会保留。旧版的「切歌关闭」会映射成新版的「关闭」；旧版默认的「所有人」会升级为推荐值「仅点歌人（房管可切全部）」，随时可以在托盘切回所有人。", _body, BodyInk);
        AddRich(box, "", _body, BodyInk);

        AddRich(box, "日志在哪？", _h2, Ink);
        AddRich(box, "%TEMP%\\musictoppin.log（UTF-8）。左下角「打开日志目录」可以直接打开。排错关键词：sources、danmaku、song request、resolved、queue play、queue skip、player failed。", _mono, BodyInk);
        ResetScroll(box);
    }

    // ---------------------------------------------------------------- 状态刷新

    private void UpdateAllStatus()
    {
        UpdateScreenStatus();
        UpdateLiveStatus();
        UpdateSourceStatus();
        SyncPolicyCombos();
    }

    private void SyncPolicyCombos()
    {
        if (_requestPolicy == null || _skipPolicy == null || _context.Settings == null) return;

        var request = ClampIndex(_context.Settings.RequestPolicy, 4);
        var skip = ClampIndex(_context.Settings.SkipPolicy, 4);

        if (_requestPolicy.SelectedIndex == request && _skipPolicy.SelectedIndex == skip) return;

        _syncing = true;
        try
        {
            _requestPolicy.SelectedIndex = request;
            _skipPolicy.SelectedIndex = skip;
        }
        finally
        {
            _syncing = false;
        }

        UpdatePolicyHints();
    }

    private void UpdateLiveStatus()
    {
        if (_liveStatus == null || _context == null) return;

        try
        {
            var text = _context.LiveStatusText != null ? _context.LiveStatusText() : "";
            _liveStatus.Text = string.IsNullOrWhiteSpace(text) ? "未连接直播间" : text;
        }
        catch (Exception ex)
        {
            Logger.Write("wizard live status: " + ex.Message);
        }
    }

    private void UpdateSourceStatus()
    {
        if (_sourceStatus == null || _context == null) return;

        try
        {
            var text = _context.SourceStatusText != null ? _context.SourceStatusText() : "";
            _sourceStatus.Text = string.IsNullOrWhiteSpace(text) ? "未登录曲源 · 游客模式" : text;
        }
        catch (Exception ex)
        {
            Logger.Write("wizard source status: " + ex.Message);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ResetAllRichTextBoxes(this);
        UpdateAllStatus();
        _statusTimer.Start();
    }

    private static void ResetAllRichTextBoxes(Control parent)
    {
        if (parent == null) return;

        foreach (Control child in parent.Controls)
        {
            var box = child as RichTextBox;
            if (box != null) ResetScroll(box);
            if (child.HasChildren) ResetAllRichTextBoxes(child);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer?.Stop();
            _statusTimer?.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _h1?.Dispose();
            _h2?.Dispose();
            _body?.Dispose();
            _mono?.Dispose();
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static TabPage NewPage()
    {
        return new TabPage
        {
            BackColor = PageBg,
            Padding = new Padding(0)
        };
    }

    private static GroupBox MakeGroup(string title, int x, int y, int width, int height)
    {
        return new GroupBox
        {
            Text = title,
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = CardBg,
            ForeColor = BodyInk,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
    }

    private static RadioButton MakeRadio(string text, int x, int y, int width)
    {
        return new RadioButton
        {
            Text = text,
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(width, 24),
            ForeColor = BodyInk
        };
    }

    private static Button MakeSecondaryButton(string text, int x, int y, int width, int height)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            FlatStyle = FlatStyle.System
        };
    }

    private static RichTextBox MakeRichBox(Point location, Size size)
    {
        return new RichTextBox
        {
            Location = location,
            Size = size,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = CardBg,
            ForeColor = BodyInk,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
    }

    private static void AddRich(RichTextBox box, string text, Font font, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.AppendText((text ?? "") + Environment.NewLine);
    }

    private static void ResetScroll(RichTextBox box)
    {
        box.SelectionStart = 0;
        box.SelectionLength = 0;
        try
        {
            if (box.IsHandleCreated) box.ScrollToCaret();
        }
        catch { }
    }

    private static int ClampIndex(int value, int count)
    {
        if (value < 0) return 0;
        if (value >= count) return count - 1;
        return value;
    }

    private static string DetectSoftware()
    {
        // 正在运行的软件优先，避免「装了 OBS 但实际用直播姬」被预选成 OBS
        if (IsProcessRunning("obs64") || IsProcessRunning("obs32")) return "obs";
        if (IsProcessRunning("livehime")) return "jiji";
        if (IsProcessRunning("直播伴侣") || IsProcessRunning("DouyinLive") || IsProcessRunning("LiveCompanion")) return "banlv";

        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (File.Exists(Path.Combine(programFiles, @"obs-studio\bin\64bit\obs64.exe")) ||
                File.Exists(Path.Combine(programFilesX86, @"obs-studio\bin\32bit\obs32.exe"))) return "obs";
        }
        catch { }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (File.Exists(Path.Combine(localAppData, @"BilibiliLive\LiveHime\livehime.exe")) ||
                File.Exists(Path.Combine(programFilesX86, @"BilibiliLive\LiveHime\livehime.exe"))) return "jiji";
        }
        catch { }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Directory.Exists(Path.Combine(localAppData, "DouyinLive")) ||
                Directory.Exists(Path.Combine(localAppData, "直播伴侣"))) return "banlv";
        }
        catch { }

        return "";
    }

    private static bool IsProcessRunning(string name)
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(name);
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (processes != null)
            {
                foreach (var process in processes)
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
    }
}
