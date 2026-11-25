using System.Runtime.InteropServices;

namespace MusicToppin;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly HudForm _hud;
    private readonly SynchronizationContext _ui;
    private readonly NowPlayingService? _service;
    private readonly System.Windows.Forms.Timer _idleTimer;
    private readonly System.Windows.Forms.Timer _relockTimer;

    private readonly System.Windows.Forms.Timer? _demoTimer;
    private readonly bool _demo;
    private int _demoIndex;
    private DateTime _demoStart = DateTime.UtcNow;
    private string _demoPrevTitle = "";
    private string _demoPrevArtist = "";
    private int _demoPlayed;

    private NowPlaying _latest = NowPlaying.Empty;
    private DateTime _lastSessionUtc = DateTime.UtcNow;

    private readonly ToolStripMenuItem _itemShow;
    private readonly ToolStripMenuItem _itemLock;
    private readonly ToolStripMenuItem _itemGreen;
    private readonly ToolStripMenuItem _itemCompact;
    private readonly ToolStripMenuItem _itemKillfeed;
    private readonly ToolStripMenuItem _itemAutoStart;

    private static readonly (string Title, string Artist, int Seconds)[] DemoTracks =
    {
        ("夜に駆ける（向夜晚奔去／Yoru ni Kakeru）", "YOASOBI · Ayase / 幾田りら", 198),
        ("起风了", "买辣椒也用券", 325),
        ("Lemon", "米津玄師", 255),
        ("春不晚（完整版）", "张叶蕾", 214),
        ("群青", "YOASOBI", 264)
    };

    private readonly bool _demoFast;

    public TrayApp(bool demo, bool demoFast)
    {
        _demo = demo;
        _demoFast = demoFast;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load();

        Logger.Write("args: " + string.Join(" | ", Environment.GetCommandLineArgs()));

        // 曲源登录态：cookie 存注册表，启动时装回静态字段
        Netease.Cookie = _settings.NeteaseCookie ?? "";
        QQMusic.Cookie = _settings.QQMusicCookie ?? "";
        QQMusic.Uin = _settings.QQMusicUin ?? "";
        QQMusic.ClientLoggedIn = SourceCheck.IsQQMusicClientLoggedIn();
        Logger.Write("sources: netease=" + (Netease.IsLoggedIn ? "logged in" : "guest") +
                     ", qq=" + (QQMusic.IsLoggedIn ? "logged in" : "guest"));

        _hud = new HudForm();
        _hud.SetCompact(_settings.Compact);
        _hud.SetGreen(_settings.Green);
        _hud.SetLocked(_settings.Locked);
        _hud.SetEditing(!_settings.Locked);
        _hud.PrevRequested += () => _ = SkipAsync(previous: true);
        _hud.ToggleLockRequested += ToggleLock;
        _hud.MenuRequested += ShowMenuAtCursor;
        _hud.HotkeyPressed += OnHotkey;
        _hud.DragFinished += () =>
        {
            _anchor = "";
            _settings.Anchor = "";
            _settings.X = _hud.Left;
            _settings.Y = _hud.Top;
            _settings.Save();
        };
        _hud.SizeChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_anchor)) MoveHudTo(_anchor);
        };

        PlaceHud();

        _itemShow = new ToolStripMenuItem("显示浮窗", null, (_, _) => ToggleVisible()) { Checked = _settings.Visible };
        _itemLock = new ToolStripMenuItem("锁定浮窗（鼠标穿透）", null, (_, _) => ToggleLock()) { Checked = _settings.Locked };
        _itemGreen = new ToolStripMenuItem("绿幕模式（直播采集）", null, (_, _) => ToggleGreen()) { Checked = _settings.Green };
        _itemKillfeed = new ToolStripMenuItem("击杀播报", null, (_, _) => SetStyle(compact: false)) { Checked = !_settings.Compact };
        _itemCompact = new ToolStripMenuItem("极简", null, (_, _) => SetStyle(compact: true)) { Checked = _settings.Compact };
        _itemAutoStart = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart()) { Checked = _settings.AutoStart };

        _itemWizard = new ToolStripMenuItem("上屏向导…", null, (_, _) => ShowWizard());
        _itemCheckUpdate = new ToolStripMenuItem("检查更新…", null, (_, _) => CheckForUpdates(manual: true));
        _itemPlayBv = new ToolStripMenuItem("点歌测试（播放 BV）…", null, (_, _) => PromptAndPlayBv());
        _itemStopPlay = new ToolStripMenuItem("停止播放", null, (_, _) => StopOwnPlayback());

        _liveStatus = new ToolStripMenuItem("未连接直播间") { Enabled = false };
        _itemLiveDiag = new ToolStripMenuItem("直播间状态…", null, (_, _) => ShowLiveStatus());
        _itemLiveConnect = new ToolStripMenuItem("连接直播间…", null, (_, _) => PromptLiveRoom());
        _itemLiveDisconnect = new ToolStripMenuItem("断开直播间", null, (_, _) => DisconnectLive()) { Enabled = false };
        _itemQueueNext = new ToolStripMenuItem("队列：空", null, (_, _) => StartNextQueuedSong()) { Enabled = false };
        _itemQueueClear = new ToolStripMenuItem("清空点歌队列", null, (_, _) => ClearQueue()) { Enabled = false };
        _itemQueueList = new ToolStripMenuItem("点歌队列：空");
        _itemQueueList.DropDownOpening += (_, _) => RebuildQueueList();
        _itemLiveToast = new ToolStripMenuItem("点歌/播放时弹气泡提示（默认关闭）", null, (_, _) => ToggleLiveToast()) { Checked = _settings.LiveToast };

        var liveMenu = new ToolStripMenuItem("弹幕点歌");
        liveMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemLiveConnect,
            _itemLiveDisconnect,
            new ToolStripSeparator(),
            _liveStatus,
            _itemLiveDiag,
            _itemQueueList,
            new ToolStripSeparator(),
            _itemLiveToast
        });

        _itemNeteaseLogin = new ToolStripMenuItem("网易云：登录（扫码 / 手机号）…", null, (_, _) => LoginNetease());
        _itemNeteaseLogout = new ToolStripMenuItem("网易云：退出登录", null, (_, _) => LogoutNetease());
        _itemQqLogin = new ToolStripMenuItem("QQ 音乐：登录（QQ / 微信扫码）…", null, (_, _) => LoginQQMusic());
        _itemQqLogout = new ToolStripMenuItem("QQ 音乐：退出登录", null, (_, _) => LogoutQQMusic());
        _neteaseStatus = new ToolStripMenuItem("网易云：未登录（游客曲库）") { Enabled = false };
        _qqStatus = new ToolStripMenuItem("QQ 音乐：未登录（只能搜索）") { Enabled = false };

        _itemNeteaseSync = new ToolStripMenuItem("网易云：从官方客户端同步登录（推荐）", null, (_, _) => SyncNeteaseFromClient());
        _itemPlayerCheck = new ToolStripMenuItem("播放器检查…", null, (_, _) => ShowPlayerCheck());

        var sourceMenu = new ToolStripMenuItem("曲源（登录）");
        sourceMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemNeteaseSync,
            _itemNeteaseLogin,
            _neteaseStatus,
            _itemNeteaseLogout,
            new ToolStripSeparator(),
            _itemQqLogin,
            _qqStatus,
            _itemQqLogout,
            new ToolStripSeparator(),
            _itemPlayerCheck
        });
        _itemOverlay = new ToolStripMenuItem("直播叠加层（OBS 浏览器源）", null, (_, _) => ToggleOverlay()) { Checked = _settings.Overlay };
        _itemOffscreen = new ToolStripMenuItem("离屏采集（窗口采集用）", null, (_, _) => ToggleOffscreen()) { Checked = _settings.CaptureOffscreen };
        _itemOccluded = new ToolStripMenuItem("遮挡模式（压在其他窗口后面）", null, (_, _) => ToggleOccluded()) { Checked = _settings.CaptureOccluded };
        _itemCopyOverlay = new ToolStripMenuItem("复制叠加层地址", null, (_, _) => CopyOverlayUrl("")) { Enabled = false };
        _itemCopyVideo = new ToolStripMenuItem("复制视频小窗地址", null, (_, _) => CopyOverlayUrl("video")) { Enabled = false };

        var styleMenu = new ToolStripMenuItem("样式");
        styleMenu.DropDownItems.Add(_itemKillfeed);
        styleMenu.DropDownItems.Add(_itemCompact);

        _colorMenu = BuildColorMenu();

        var positionMenu = new ToolStripMenuItem("位置");
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("左上", null, (_, _) => MoveHudTo("tl")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("右上", null, (_, _) => MoveHudTo("tr")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("左下", null, (_, _) => MoveHudTo("bl")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("右下", null, (_, _) => MoveHudTo("br")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("屏幕居中", null, (_, _) => MoveHudTo("center")));
        positionMenu.DropDownItems.Add(new ToolStripSeparator());
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("自由拖动：先按快捷键解锁") { Enabled = false });

        var controlsMenu = new ToolStripMenuItem("播放控制");
        controlsMenu.DropDownItems.Add(new ToolStripMenuItem("上一首", null, (_, _) => _ = SkipAsync(previous: true)));
        controlsMenu.DropDownItems.Add(new ToolStripMenuItem("下一首", null, (_, _) => _ = SkipAsync(previous: false)));
        controlsMenu.DropDownItems.Add(new ToolStripSeparator());
        _volumeMenu = BuildVolumeMenu();
        controlsMenu.DropDownItems.Add(_volumeMenu);
        controlsMenu.DropDownItems.Add(BuildVideoAudioMenu());

        _itemHotkey = new ToolStripMenuItem("锁定快捷键：…", null, (_, _) => ShowHotkeyDialog());

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_itemHotkey);
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _itemShow,
            _itemLock,
            _itemGreen,
            new ToolStripSeparator(),
            controlsMenu,
            liveMenu,
            sourceMenu,
            styleMenu,
            _colorMenu,
            positionMenu,
            _itemWizard,
            _itemCheckUpdate,
            _itemPlayBv,
            _itemStopPlay,
            _itemOverlay,
            _itemCopyOverlay,
            _itemCopyVideo,
            _itemOffscreen,
            _itemOccluded,
            _itemAutoStart,
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => ExitApp())
        });

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Music Toppin · 正在播放",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleVisible();
        };

        _idleTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        // 编辑模式闲置 12 秒自动回锁，避免忘记锁回去后打游戏被吞点击
        _relockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _relockTimer.Tick += (_, _) => CheckRelock();
        _relockTimer.Start();

        if (_demo)
        {
            _demoTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _demoTimer.Tick += (_, _) => DemoTick();
            _demoTimer.Start();
        }
        else
        {
            _anchor = _settings.Anchor ?? "";
            _service = new NowPlayingService();
            _service.Updated += np => _ui.Post(_ => Apply(np), null);
        }

        ApplyTheme();
        RefreshSourceStatus();

        // 装了官方客户端就每次启动都同步一次（客户端换账号也能跟上）；没装才用已保存的 cookie
        if (NeteaseClient.IsInstalled)
        {
            var importTimer = new System.Windows.Forms.Timer { Interval = 2200 };
            importTimer.Tick += (_, _) =>
            {
                importTimer.Stop();
                importTimer.Dispose();
                _ = ImportNeteaseFromClientAsync(report: false);
            };
            importTimer.Start();
        }
        else if (!string.IsNullOrWhiteSpace(Netease.Cookie))
        {
            var sourceTimer = new System.Windows.Forms.Timer { Interval = 2800 };
            sourceTimer.Tick += (_, _) =>
            {
                sourceTimer.Stop();
                sourceTimer.Dispose();
                _ = ValidateNeteaseAsync();
            };
            sourceTimer.Start();
        }

        var playBv = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--play-bv=", StringComparison.OrdinalIgnoreCase));
        if (playBv != null)
        {
            var bvid = playBv.Substring("--play-bv=".Length).Trim();
            if (bvid.Length > 0)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 1200 };
                timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); _ = PlayBvidAsync(bvid); };
                timer.Start();
            }
        }
        // 调试/自动化用：--room=5050 启动即连直播间；--fake-danmaku="点歌 BV…" 注入一条假弹幕
        var roomArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--room=", StringComparison.OrdinalIgnoreCase));
        if (roomArg != null)
        {
            var roomId = BilibiliLive.ParseRoomId(roomArg.Substring("--room=".Length));
            if (roomId > 0)
            {
                var roomTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                roomTimer.Tick += (_, _) =>
                {
                    roomTimer.Stop();
                    roomTimer.Dispose();
                    ConnectLive(roomId, remember: false);
                };
                roomTimer.Start();
            }
        }
        else if (!_demo && _settings.LiveRoomId > 0 && _settings.LiveAutoConnect)
        {
            var liveTimer = new System.Windows.Forms.Timer { Interval = 2200 };
            liveTimer.Tick += (_, _) =>
            {
                liveTimer.Stop();
                liveTimer.Dispose();
                ConnectLive(_settings.LiveRoomId, remember: false);
            };
            liveTimer.Start();
        }

        // 调试用：--login-test=netease|qq 直接打开登录窗口
        var loginTest = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--login-test=", StringComparison.OrdinalIgnoreCase));
        if (loginTest != null)
        {
            var target = loginTest.Substring("--login-test=".Length).Trim().ToLowerInvariant();
            var loginTimer = new System.Windows.Forms.Timer { Interval = 1600 };
            loginTimer.Tick += (_, _) =>
            {
                loginTimer.Stop();
                loginTimer.Dispose();
                if (target == "qq") LoginQQMusic();
                else LoginNetease();
            };
            loginTimer.Start();
        }

        // 调试用：--live-status 打开「直播间状态」窗口
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--live-status", StringComparison.OrdinalIgnoreCase)))
        {
            var statusTimer = new System.Windows.Forms.Timer { Interval = 4500 };
            statusTimer.Tick += (_, _) =>
            {
                statusTimer.Stop();
                statusTimer.Dispose();
                ShowLiveStatus();
            };
            statusTimer.Start();
        }

        // 调试用：--player-check 打开播放器检查对话框
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--player-check", StringComparison.OrdinalIgnoreCase)))
        {
            var checkTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            checkTimer.Tick += (_, _) =>
            {
                checkTimer.Stop();
                checkTimer.Dispose();
                ShowPlayerCheck();
            };
            checkTimer.Start();
        }

        var fakeArguments = Environment.GetCommandLineArgs()
            .Where(a => a.StartsWith("--fake-danmaku=", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Substring("--fake-danmaku=".Length).Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (fakeArguments.Count > 0)
        {
            var fakeIndex = 0;
            var fakeTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            fakeTimer.Tick += (_, _) =>
            {
                if (fakeIndex >= fakeArguments.Count)
                {
                    fakeTimer.Stop();
                    fakeTimer.Dispose();
                    return;
                }

                fakeIndex++;
                HandleDanmaku(new DanmakuMessage
                {
                    Text = fakeArguments[fakeIndex - 1],
                    UserName = "测试观众" + fakeIndex,
                    Uid = 10000 + fakeIndex
                });
            };
            fakeTimer.Start();
        }

        if (_settings.Visible) ShowHud();
        EnsureHotkeys();
        if (!_settings.WizardShown)
        {
            var wizardTimer = new System.Windows.Forms.Timer { Interval = 1400 };
            wizardTimer.Tick += (_, _) =>
            {
                wizardTimer.Stop();
                wizardTimer.Dispose();
                ShowWizard();
            };
            wizardTimer.Start();
        }

        // 启动时按保存的锚点重新贴边（屏幕分辨率/缩放变化后也能回到正确位置）
        if (!string.IsNullOrEmpty(_settings.Anchor) && !_settings.CaptureOffscreen) MoveHudTo(_settings.Anchor);
        if (_settings.Overlay)
        {
            StartOverlay(notify: false);
        }
        if (_settings.UpdateCheckOnStart)
        {
            var updateTimer = new System.Windows.Forms.Timer { Interval = 3200 };
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Stop();
                updateTimer.Dispose();
                CheckForUpdates(manual: false);
            };
            updateTimer.Start();
        }

        // 调试/自动化用：MUSICTOPPIN_ANCHOR=tl|tr|bl|br|center
        var anchor = Environment.GetEnvironmentVariable("MUSICTOPPIN_ANCHOR");
        if (!string.IsNullOrEmpty(anchor) && !_settings.CaptureOffscreen) MoveHudTo(anchor);

        if (_settings.CaptureOffscreen && _hud.Visible)
        {
            _hud.SetCaptureOffscreen(true);
        }
        else if (_settings.CaptureOccluded && _hud.Visible)
        {
            _hud.SetCaptureOccluded(true);
        }

        if (_settings.Migrated)
        {
            _tray.ShowBalloonTip(4000, "Music Toppin",
                "浮窗已锁定（鼠标穿透），打游戏不会误点。" + (_hotkeyLabel.Length > 0 ? _hotkeyLabel + " 解锁后可拖动和点按钮。" : "可从托盘菜单解锁。"),
                ToolTipIcon.Info);
        }
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HK_PREV = 1;
    private const int HK_NEXT = 2;
    private const int HK_LOCK = 3;
    private const uint VK_LEFT = 0x25;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_M = 0x4D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _hotkeysRegistered;
    private string _hotkeyLabel = "";
    private ToolStripMenuItem _itemHotkey;
    private ToolStripMenuItem _itemWizard;
    private ToolStripMenuItem _itemCheckUpdate;
    private ToolStripMenuItem _itemPlayBv;
    private ToolStripMenuItem _itemStopPlay;
    private ToolStripMenuItem _liveStatus;
    private ToolStripMenuItem _itemLiveConnect;
    private ToolStripMenuItem _itemLiveDisconnect;
    private ToolStripMenuItem _itemQueueNext;
    private ToolStripMenuItem _itemQueueClear;
    private ToolStripMenuItem _itemQueueList;
    private ToolStripMenuItem _itemLiveDiag;
    private ToolStripMenuItem _volumeMenu;
    private ToolStripMenuItem _itemCopyOverlay;
    private ToolStripMenuItem _itemCopyVideo;
    private ToolStripMenuItem _itemLiveToast;
    private ToolStripMenuItem _itemNeteaseLogin;
    private ToolStripMenuItem _itemNeteaseLogout;
    private ToolStripMenuItem _itemNeteaseSync;
    private ToolStripMenuItem _itemPlayerCheck;
    private ToolStripMenuItem _itemQqLogin;
    private ToolStripMenuItem _itemQqLogout;
    private ToolStripMenuItem _neteaseStatus;
    private ToolStripMenuItem _qqStatus;

    private readonly RequestQueue _queue = new RequestQueue();
    private DanmakuClient _danmaku;
    private bool _playingFromQueue;
    private int _idleTicks;
    private int _danmakuReceived;
    private readonly List<string> _recentDanmaku = new List<string>();
    private string _lastRequestResult = "";
    private string _toastText = "";
    private int _toastId;
    private DateTime _toastUntilUtc = DateTime.MinValue;
    private string _videoBvid = "";
    private System.Windows.Forms.Timer _videoTimer;
    private DateTime _videoStartedUtc;
    private TimeSpan _videoDuration;
    private ResolvedSong _videoSong;
    private ToolStripMenuItem _itemOverlay;
    private ToolStripMenuItem _itemOffscreen;
    private ToolStripMenuItem _itemOccluded;
    private ToolStripMenuItem _colorMenu;
    private ToolStripMenuItem _itemAccentColor;
    private ToolStripMenuItem _itemTitleColor;
    private ToolStripMenuItem _itemArtistColor;
    private bool _wizardOpen;
    private bool _updateDialogOpen;
    private bool _checkingUpdate;
    private WebView2Player _player;
    private OverlayServer _overlay;
    private DateTime _lastLockToggleUtc = DateTime.MinValue;
    private string _anchor = "";
    private bool _reanchoring;

    private void ApplyTheme()
    {
        var theme = new HudTheme
        {
            Accent = Color.FromArgb(_settings.AccentColor),
            Title = Color.FromArgb(_settings.TitleColor),
            Artist = Color.FromArgb(_settings.ArtistColor),
            Card = Color.FromArgb(_settings.CardColor),
            CardOpacity = _settings.CardOpacity / 100f
        };

        HudRenderer.Theme = theme;
        _hud.Render();
    }

    private static Image Swatch(Color color)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Color.FromArgb(255, color.R, color.G, color.B)))
                g.FillRectangle(fill, 2, 2, 12, 12);
            using (var pen = new Pen(Color.FromArgb(110, 0, 0, 0)))
                g.DrawRectangle(pen, 2, 2, 11, 11);
        }
        return bmp;
    }

    private ToolStripMenuItem MakeColorItem(string text, Color current, Action<Color> apply)
    {
        var item = new ToolStripMenuItem(text) { Image = Swatch(current) };
        item.Click += (_, _) =>
        {
            using (var dialog = new ColorDialog { Color = current, FullOpen = true, AnyColor = true })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var picked = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                apply(picked);
                var old = item.Image;
                item.Image = Swatch(picked);
                if (old != null) old.Dispose();
            }
        };
        return item;
    }

    private ToolStripMenuItem BuildColorMenu()
    {
        var menu = new ToolStripMenuItem("颜色");
        menu.DropDownItems.Add(BuildPresetMenu());
        menu.DropDownItems.Add(BuildOpacityMenu());
        menu.DropDownItems.Add(new ToolStripSeparator());

        _itemAccentColor = MakeColorItem(
            "主题色（红点 / 正在播放 / 进度条 / 左侧竖条）",
            Color.FromArgb(_settings.AccentColor),
            color => { _settings.AccentColor = color.ToArgb(); _settings.Save(); ApplyTheme(); });
        menu.DropDownItems.Add(_itemAccentColor);

        _itemTitleColor = MakeColorItem(
            "歌名",
            Color.FromArgb(_settings.TitleColor),
            color => { _settings.TitleColor = color.ToArgb(); _settings.Save(); ApplyTheme(); });
        menu.DropDownItems.Add(_itemTitleColor);

        _itemArtistColor = MakeColorItem(
            "歌手 / 次要文字",
            Color.FromArgb(_settings.ArtistColor),
            color => { _settings.ArtistColor = color.ToArgb(); _settings.Save(); ApplyTheme(); });
        menu.DropDownItems.Add(_itemArtistColor);


        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("恢复默认颜色", null, (_, _) =>
        {
            var defaults = AppSettings.Default();
            _settings.AccentColor = defaults.AccentColor;
            _settings.TitleColor = defaults.TitleColor;
            _settings.ArtistColor = defaults.ArtistColor;
            _settings.Save();
            ApplyTheme();

            RefreshColorSwatches();
        }));

        return menu;
    }

    private ToolStripMenuItem BuildOpacityMenu()
    {
        var menu = new ToolStripMenuItem("背景透明度");

        var presets = new[] { 100, 95, 90, 80, 70, 60, 40 };
        foreach (var percent in presets)
        {
            var value = percent;
            menu.DropDownItems.Add(new ToolStripMenuItem(percent + "%", null, (_, _) => SetOpacity(value)));
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("自定义…", null, (_, _) =>
        {
            using (var dialog = new OpacityDialog(_settings.CardOpacity, ApplyOpacity, SetOpacity))
            {
                dialog.ShowDialog();
            }
        }));

        return menu;
    }

    /// <summary>只改内存并重绘（滑块实时预览用，不落盘）。</summary>
    private void ApplyOpacity(int percent)
    {
        if (percent < 10) percent = 10;
        if (percent > 100) percent = 100;
        _settings.CardOpacity = percent;
        ApplyTheme();

    }

    private void SetOpacity(int percent)
    {
        ApplyOpacity(percent);
        _settings.Save();
    }
    private ToolStripMenuItem BuildPresetMenu()
    {
        var menu = new ToolStripMenuItem("配色预设");
        menu.DropDownItems.Add(MakePreset("默认（红）", unchecked((int)0xFFFF3B30), unchecked((int)0xFFF5F8FC), unchecked((int)0xFF9FB0C6)));
        menu.DropDownItems.Add(MakePreset("霓虹青", unchecked((int)0xFF00E5FF), unchecked((int)0xFFE8FBFF), unchecked((int)0xFF8BD8E8)));
        menu.DropDownItems.Add(MakePreset("薄荷绿", unchecked((int)0xFF00E676), unchecked((int)0xFFE8FFF1), unchecked((int)0xFF9BD9B8)));
        menu.DropDownItems.Add(MakePreset("紫罗兰", unchecked((int)0xFFB388FF), unchecked((int)0xFFF3EDFF), unchecked((int)0xFFC5B8E8)));
        menu.DropDownItems.Add(MakePreset("暖金", unchecked((int)0xFFFFC107), unchecked((int)0xFFFFF8E1), unchecked((int)0xFFD7C9A3)));
        return menu;
    }

    private ToolStripMenuItem MakePreset(string name, int accent, int title, int artist)
    {
        return new ToolStripMenuItem(name, null, (_, _) =>
        {
            _settings.AccentColor = accent;
            _settings.TitleColor = title;
            _settings.ArtistColor = artist;
            _settings.Save();
            ApplyTheme();

            RefreshColorSwatches();
        });
    }
    private void RefreshColorSwatches()
    {
        UpdateSwatch(_itemAccentColor, Color.FromArgb(_settings.AccentColor));
        UpdateSwatch(_itemTitleColor, Color.FromArgb(_settings.TitleColor));
        UpdateSwatch(_itemArtistColor, Color.FromArgb(_settings.ArtistColor));
    }

    private static void UpdateSwatch(ToolStripMenuItem item, Color color)
    {
        if (item == null) return;
        var old = item.Image;
        item.Image = Swatch(color);
        if (old != null) old.Dispose();
    }

    private void MoveHudTo(string anchor)
    {
        if (!_hud.IsHandleCreated) return;
        if (_reanchoring) return;

        if (_settings.CaptureOffscreen)
        {
            _settings.CaptureOffscreen = false;
            _itemOffscreen.Checked = false;
            _settings.Save();
            _hud.SetCaptureOffscreen(false);
        }

        if (!string.IsNullOrEmpty(anchor))
        {
            _anchor = anchor;
            _settings.Anchor = anchor;
        }

        _reanchoring = true;
        try
        {
            _hud.Render();

            var area = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1920, 1080);
            var w = _hud.Width;
            var h = _hud.Height;
            const int margin = 24;

            int x, y;
            switch (anchor)
            {
                case "tl": x = area.Left + margin; y = area.Top + 90; break;
                case "tr": x = area.Right - w - margin; y = area.Top + 90; break;
                case "bl": x = area.Left + margin; y = area.Bottom - h - margin; break;
                case "br": x = area.Right - w - margin; y = area.Bottom - h - margin; break;
                default: x = area.Left + (area.Width - w) / 2; y = area.Top + (area.Height - h) / 2; break;
            }

            var clamped = ClampToWorkArea(new Rectangle(x, y, w, h));
            _hud.Location = clamped.Location;
            _settings.X = clamped.X;
            _settings.Y = clamped.Y;
            _settings.Save();
            _hud.Render();
        }
        finally
        {
            _reanchoring = false;
        }
    }

    private static string UpdateManifestUrl(AppSettings settings)
    {
        if (settings != null && !string.IsNullOrWhiteSpace(settings.UpdateUrl)) return settings.UpdateUrl.Trim();
        return Updater.DefaultManifestUrl;
    }

    private void CheckForUpdates(bool manual)
    {
        if (_checkingUpdate) return;

        var url = UpdateManifestUrl(_settings);
        if (string.IsNullOrWhiteSpace(url))
        {
            if (manual)
            {
                _tray.ShowBalloonTip(2500, "Music Toppin", "尚未配置更新地址（发布版会内置）", ToolTipIcon.Info);
            }
            return;
        }

        _checkingUpdate = true;
        if (manual) _tray.ShowBalloonTip(1500, "Music Toppin", "正在检查更新…", ToolTipIcon.Info);

        Task.Run(() =>
        {
            UpdateInfo info = null;
            Exception error = null;
            try { info = Updater.Check(url); }
            catch (Exception ex) { error = ex; }

            _ui.Post(_ =>
            {
                _checkingUpdate = false;

                if (error != null)
                {
                    Logger.Write("update check: " + error.Message);
                    if (manual) _tray.ShowBalloonTip(3000, "Music Toppin", "检查更新失败：" + error.Message, ToolTipIcon.Warning);
                    return;
                }

                if (info == null)
                {
                    Logger.Write("update check: up to date (" + Updater.CurrentVersionText + ")");
                    if (manual)
                    {
                        _tray.ShowBalloonTip(2500, "Music Toppin",
                            "已是最新版本 " + Updater.CurrentVersionText, ToolTipIcon.Info);
                    }
                    return;
                }

                Logger.Write("update check: found " + info.VersionText);
                ShowUpdateDialog(info);
            }, null);
        });
    }

    private void ShowUpdateDialog(UpdateInfo info)
    {
        if (_updateDialogOpen) return;
        _updateDialogOpen = true;
        try
        {
            using (var dialog = new UpdateDialog(info, ExitApp))
            {
                dialog.ShowDialog(_hud);
            }
        }
        catch (Exception ex)
        {
            Logger.Write("dialog: " + ex.GetType().Name + " " + ex.Message + " | " + ex.StackTrace);
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }
    private void PromptAndPlayBv()
    {
        using (var prompt = new PromptDialog("播放 B 站视频", "输入 BV 号或视频链接（例如 BV1xx411c7mD）"))
        {
            if (prompt.ShowDialog(_hud) != DialogResult.OK) return;
            var text = prompt.Value;
            if (string.IsNullOrWhiteSpace(text)) return;

            string bvid;
            if (!Bilibili.TryExtractBvid(text, out bvid))
            {
                if (text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    bvid = Bilibili.ResolveShortLink(text.Trim());
                }
            }

            if (string.IsNullOrWhiteSpace(bvid))
            {
                _tray.ShowBalloonTip(2500, "Music Toppin", "没认出 BV 号", ToolTipIcon.Warning);
                return;
            }

            _ = PlayBvidAsync(bvid);
        }
    }

    private async Task PlayBvidAsync(string bvid)
    {
        try
        {
            // 单播放：手动点播前先停掉队列里正在放的那首
            if (_playingFromQueue) StopOwnPlayback();

            _tray.ShowBalloonTip(1500, "Music Toppin", "正在解析 " + bvid + " …", ToolTipIcon.Info);

            var song = await Task.Run(() => Bilibili.Resolve(bvid));
            if (song == null)
            {
                _tray.ShowBalloonTip(3000, "Music Toppin", "解析失败：可能需要在 B 站登录后才能播放高码率", ToolTipIcon.Warning);
                return;
            }

            _videoBvid = bvid;

            if (_settings.VideoAudio == 1)
            {
                StartVideoPlayback(song);
                return;
            }

            await EnsurePlayer().PlayAsync(song);
            PushOwnState(song, new PlaybackState { Playing = true, Duration = song.Duration });
        }
        catch (Exception ex)
        {
            Logger.Write("play bv: " + ex.Message);
            _tray.ShowBalloonTip(4000, "Music Toppin", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void OnOwnPlaybackState(PlaybackState state)
    {
        var song = _player == null ? null : _player.Current;

        if (state.Ended)
        {
            // 一首放完：清掉当前项，队列里还有就接着放
            StopOwnPlayback();
            StartNextQueuedSong();
            return;
        }

        if (song == null) return;
        PushOwnState(song, state);
    }

    private void PushOwnState(ResolvedSong song, PlaybackState state)
    {
        if (_service == null || song == null) return;

        var np = new NowPlaying(
            true, song.Title, song.Artist, state.Position,
            state.Duration > TimeSpan.Zero ? state.Duration : song.Duration,
            state.Playing, true,
            new string[NowPlaying.HistorySlots], new string[NowPlaying.HistorySlots], 0, 0);

        // 点歌模式：HUD 多显示一行「下一首」
        var next = _queue.PeekNext();
        if (next != null)
        {
            np.NextTitle = next.DisplayName;
            np.NextArtist = next.Artist;
            np.QueueCount = _queue.Count;
        }

        _service.SetOwnPlayback(np);
    }

    private void StopOwnPlayback()
    {
        _playingFromQueue = false;
        _videoBvid = "";
        StopVideoTimer();
        _queue.ClearCurrent();
        try { if (_player != null) _ = _player.StopAsync(); } catch { }
        if (_service != null) _service.SetOwnPlayback(null);
        UpdateQueueMenu();
    }

    private WebView2Player EnsurePlayer()
    {
        if (_player == null)
        {
            _player = new WebView2Player { Volume = ClampVolume(_settings.PlayerVolume) / 100f };
            _player.StateChanged += OnOwnPlaybackState;
            _player.Failed += OnPlayerFailed;
        }
        return _player;
    }

    // ---- 点歌音量 ----

    private ToolStripMenuItem BuildVolumeMenu()
    {
        var menu = new ToolStripMenuItem("点歌音量");

        foreach (var percent in new[] { 100, 80, 60, 50, 40, 20, 0 })
        {
            var value = percent;
            var item = new ToolStripMenuItem(percent == 0 ? "静音" : percent + "%", null, (_, _) => SetPlayerVolume(value));
            item.Tag = percent;
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("自定义…", null, (_, _) =>
        {
            using (var dialog = new VolumeDialog(_settings.PlayerVolume, ApplyPlayerVolume, SetPlayerVolume))
            {
                dialog.ShowDialog(_hud);
            }
        }));

        menu.DropDownOpening += (_, _) => SyncVolumeMenu(menu);
        return menu;
    }

    private void SyncVolumeMenu(ToolStripMenuItem menu)
    {
        menu.Text = "点歌音量：" + _settings.PlayerVolume + "%";

        foreach (ToolStripItem item in menu.DropDownItems)
        {
            var menuItem = item as ToolStripMenuItem;
            if (menuItem == null || !(menuItem.Tag is int)) continue;

            menuItem.Checked = (int)menuItem.Tag == _settings.PlayerVolume;
        }
    }

    private static int ClampVolume(int percent)
    {
        return Math.Max(0, Math.Min(100, percent));
    }

    /// <summary>只改内存并立即生效（拖动滑块时实时听），不写注册表。</summary>
    private void ApplyPlayerVolume(int percent)
    {
        _settings.PlayerVolume = ClampVolume(percent);
        if (_player != null) _ = _player.SetVolumeAsync(_settings.PlayerVolume / 100f);
    }

    private void SetPlayerVolume(int percent)
    {
        ApplyPlayerVolume(percent);
        _settings.Save();
    }

    // ---- 视频声音模式 ----

    /// <summary>视频自带声音：不在应用内播音频，用计时器推进 HUD 进度并在时长到点后切下一首。</summary>
    private void StartVideoPlayback(ResolvedSong song)
    {
        _videoSong = song;
        _videoStartedUtc = DateTime.UtcNow;
        _videoDuration = song.Duration > TimeSpan.Zero ? song.Duration : TimeSpan.Zero;

        if (_videoTimer == null)
        {
            _videoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _videoTimer.Tick += (_, _) => VideoTick();
        }
        _videoTimer.Start();

        PushOwnState(song, new PlaybackState { Playing = true, Position = TimeSpan.Zero, Duration = _videoDuration });
        Logger.Write("video play (sound from video): " + song.Title +
                     (_videoDuration > TimeSpan.Zero ? " / " + (int)_videoDuration.TotalSeconds + "s" : ""));
    }

    private void VideoTick()
    {
        if (_videoSong == null || _videoBvid.Length == 0)
        {
            StopVideoTimer();
            return;
        }

        var position = DateTime.UtcNow - _videoStartedUtc;

        if (_videoDuration > TimeSpan.Zero && position >= _videoDuration - TimeSpan.FromSeconds(1))
        {
            Logger.Write("video ended: " + _videoSong.Title);
            StopOwnPlayback();
            StartNextQueuedSong();
            return;
        }

        PushOwnState(_videoSong, new PlaybackState { Playing = true, Position = position, Duration = _videoDuration });
    }

    private void StopVideoTimer()
    {
        if (_videoTimer != null && _videoTimer.Enabled) _videoTimer.Stop();
        _videoSong = null;
    }

    private ToolStripMenuItem BuildVideoAudioMenu()
    {
        var menu = new ToolStripMenuItem("视频声音");

        var follow = new ToolStripMenuItem("跟随视频（音画同步，音量在 OBS 调）", null, (_, _) => SetVideoAudio(1)) { Tag = 1 };
        var app = new ToolStripMenuItem("走应用播放器（可调音量，可能轻微不同步）", null, (_, _) => SetVideoAudio(0)) { Tag = 0 };

        menu.DropDownItems.Add(follow);
        menu.DropDownItems.Add(app);
        menu.DropDownOpening += (_, _) =>
        {
            follow.Checked = _settings.VideoAudio == 1;
            app.Checked = _settings.VideoAudio == 0;
            menu.Text = _settings.VideoAudio == 1 ? "视频声音：跟随视频" : "视频声音：应用播放器";
        };

        return menu;
    }

    private void SetVideoAudio(int mode)
    {
        _settings.VideoAudio = mode == 0 ? 0 : 1;
        _settings.Save();
        Logger.Write("video audio mode: " + (_settings.VideoAudio == 1 ? "follow video" : "app player"));

        // 正在放视频就停掉，下一首按新模式重新开始
        if (_playingFromQueue && _videoBvid.Length > 0)
        {
            StopOwnPlayback();
            StartNextQueuedSong();
        }
    }

    private void OnPlayerFailed(string message)
    {
        Logger.Write("player failed: " + message);

        // 直播中点歌是自动流程，默认不弹气泡（避免右下角通知干扰直播/游戏输入）
        if (_playingFromQueue) LiveNotify("Music Toppin", message, ToolTipIcon.Warning);
        else _tray.ShowBalloonTip(4000, "Music Toppin", message, ToolTipIcon.Warning);

        if (!_playingFromQueue) return;

        // 点歌播放失败：清掉当前项，接着放下面的，别卡住
        StopOwnPlayback();
        StartNextQueuedSong();
    }

    /// <summary>
    /// 点歌/播放这类「自动发生」的提示：默认只写日志 + 更新托盘菜单状态，不弹 Windows 气泡。
    /// 气泡会在屏幕右下角弹窗，直播或游戏时可能干扰键鼠输入；想要提示可在
    /// 「弹幕点歌 → 点歌/播放时弹气泡提示」里打开。
    /// </summary>
    private void LiveNotify(string title, string text, ToolTipIcon icon)
    {
        Logger.Write("notify: " + title + " | " + (text ?? "").Replace("\n", " "));

        if (!_settings.LiveToast) return;

        try { _tray.ShowBalloonTip(2500, title, text, icon); } catch { }
    }

    private void ToggleLiveToast()
    {
        _settings.LiveToast = !_settings.LiveToast;
        _itemLiveToast.Checked = _settings.LiveToast;
        _settings.Save();
    }

    /// <summary>给观众看的一次性提示（叠加层网页顶部弹 4.5 秒），不影响主播的画面和键鼠。</summary>
    private void ShowOverlayToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _toastText = text;
        _toastId++;
        _toastUntilUtc = DateTime.UtcNow.AddSeconds(6);
        Logger.Write("toast: " + text);
    }

    // ---- 曲源登录（S3）----

    private void LoginNetease()
    {
        using (var form = new WebLoginForm(
            "登录网易云音乐",
            "https://music.163.com/#/login",
            "https://music.163.com",
            cookies => cookies.ContainsKey("MUSIC_U") && !string.IsNullOrWhiteSpace(cookies["MUSIC_U"]),
            "扫码或用手机号登录；成功后窗口会自动关闭并保存"))
        {
            if (form.ShowDialog(_hud) != DialogResult.OK) return;

            _settings.NeteaseCookie = form.CookieHeader;
            _settings.NeteaseNick = "";
            _settings.Save();

            Netease.Cookie = _settings.NeteaseCookie;
            _neteaseStatus.Text = "网易云：已登录（校验中…）";
            _itemNeteaseLogout.Enabled = true;

            _ = ValidateNeteaseAsync();
        }
    }

    private void LogoutNetease()
    {
        _settings.NeteaseCookie = "";
        _settings.NeteaseNick = "";
        _settings.Save();

        Netease.Cookie = "";
        RefreshSourceStatus();
    }

    private async Task ValidateNeteaseAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.NeteaseCookie))
        {
            RefreshSourceStatus();
            return;
        }

        var nickname = await Task.Run(() => Netease.CheckAccount()).ConfigureAwait(false);
        Logger.Write("netease account: " + (string.IsNullOrEmpty(nickname) ? "not logged in" : nickname));

        _ui.Post(_ =>
        {
            if (string.IsNullOrWhiteSpace(_settings.NeteaseCookie)) return;

            if (string.IsNullOrEmpty(nickname))
            {
                _neteaseStatus.Text = "网易云：登录已失效，请重新登录";
                return;
            }

            if (_settings.NeteaseNick != nickname)
            {
                _settings.NeteaseNick = nickname;
                _settings.Save();
            }
            _neteaseStatus.Text = "网易云：已登录 " + nickname;
        }, null);
    }

    private void LoginQQMusic()
    {
        using (var form = new WebLoginForm(
            "登录 QQ 音乐",
            "https://y.qq.com/",
            "https://y.qq.com",
            cookies =>
            {
                string uin, key;
                if (!cookies.TryGetValue("uin", out uin) || string.IsNullOrWhiteSpace(uin)) return false;

                var normalized = QQMusic.NormalizeUin(uin);
                if (string.IsNullOrEmpty(normalized) || normalized == "0") return false;

                return (cookies.TryGetValue("qm_keyst", out key) && !string.IsNullOrWhiteSpace(key))
                    || (cookies.TryGetValue("qqmusic_key", out key) && !string.IsNullOrWhiteSpace(key));
            },
            "点右上角「登录」，扫码或 QQ / 微信授权；成功后窗口会自动关闭并保存"))
        {
            if (form.ShowDialog(_hud) != DialogResult.OK) return;

            var uin = "";
            string value;
            if (form.CookieValues.TryGetValue("uin", out value)) uin = QQMusic.NormalizeUin(value);
            if (string.IsNullOrEmpty(uin) && form.CookieValues.TryGetValue("p_uin", out value))
            {
                uin = QQMusic.NormalizeUin(value);
            }

            _settings.QQMusicCookie = form.CookieHeader;
            _settings.QQMusicUin = uin;
            _settings.QQMusicNick = "";
            _settings.Save();

            QQMusic.Cookie = _settings.QQMusicCookie;
            QQMusic.Uin = uin;
            RefreshSourceStatus();
        }
    }

    private void LogoutQQMusic()
    {
        _settings.QQMusicCookie = "";
        _settings.QQMusicUin = "";
        _settings.QQMusicNick = "";
        _settings.Save();

        QQMusic.Cookie = "";
        QQMusic.Uin = "";
        RefreshSourceStatus();
    }

    private void RefreshSourceStatus()
    {
        if (_neteaseStatus != null)
        {
            _neteaseStatus.Text = string.IsNullOrWhiteSpace(_settings.NeteaseCookie)
                ? "网易云：未登录（游客曲库）"
                : "网易云：已登录" + (string.IsNullOrWhiteSpace(_settings.NeteaseNick) ? "" : " " + _settings.NeteaseNick);
        }

        if (_qqStatus != null)
        {
            _qqStatus.Text = string.IsNullOrWhiteSpace(_settings.QQMusicCookie)
                ? "QQ 音乐：未登录（只能搜索）"
                : "QQ 音乐：已登录" + (string.IsNullOrWhiteSpace(_settings.QQMusicUin) ? "" : "（uin " + _settings.QQMusicUin + "）");
        }

        if (_itemNeteaseLogout != null) _itemNeteaseLogout.Enabled = !string.IsNullOrWhiteSpace(_settings.NeteaseCookie);
        if (_itemQqLogout != null) _itemQqLogout.Enabled = !string.IsNullOrWhiteSpace(_settings.QQMusicCookie);
    }

    private void SyncNeteaseFromClient()
    {
        _neteaseStatus.Text = "网易云：正在读取客户端登录…";
        _ = ImportNeteaseFromClientAsync(report: true);
    }

    /// <summary>从本机「网易云音乐」客户端读已登录的 cookie（客户端登录过就不用用户再操作）。</summary>
    private async Task ImportNeteaseFromClientAsync(bool report)
    {
        var cookie = await Task.Run(() => NeteaseClient.ImportCookie()).ConfigureAwait(false);

        _ui.Post(_ =>
        {
            if (string.IsNullOrEmpty(cookie))
            {
                if (report)
                {
                    _tray.ShowBalloonTip(3000, "Music Toppin",
                        "没读到网易云客户端的登录状态。请确认客户端已安装并登录，或用「应用内登录」。",
                        ToolTipIcon.Warning);
                }

                // 同步失败时，已保存的 cookie 还能继续用，顺便校验一下
                if (!string.IsNullOrWhiteSpace(_settings.NeteaseCookie)) _ = ValidateNeteaseAsync();
                else RefreshSourceStatus();
                return;
            }

            _settings.NeteaseCookie = cookie;
            _settings.Save();

            Netease.Cookie = cookie;
            _itemNeteaseLogout.Enabled = true;
            _neteaseStatus.Text = "网易云：已从客户端同步登录";
            _ = ValidateNeteaseAsync();
        }, null);
    }

    private void ShowPlayerCheck()
    {
        using (var form = new PlayerCheckForm())
        {
            form.ShowDialog(_hud);
        }
    }

    // ---- 弹幕点歌（S2）----

    private void PromptLiveRoom()
    {
        var initial = _settings.LiveRoomId > 0 ? _settings.LiveRoomId.ToString() : "";
        using (var prompt = new PromptDialog("连接直播间", "输入 B 直播间号（如 5050），或粘贴 live.bilibili.com 链接", initial))
        {
            if (prompt.ShowDialog(_hud) != DialogResult.OK) return;

            var roomId = BilibiliLive.ParseRoomId(prompt.Value);
            if (roomId <= 0)
            {
                if (BilibiliLive.IsDouyinLink(prompt.Value))
                {
                    _tray.ShowBalloonTip(3500, "Music Toppin",
                        "抖音直播间暂不支持：抖音弹幕要签名 + protobuf，后面再接。\n目前支持 B 站直播间（房间号或链接）。",
                        ToolTipIcon.Warning);
                    return;
                }

                _tray.ShowBalloonTip(2500, "Music Toppin", "没认出直播间号（支持 5050 或 live.bilibili.com 链接）", ToolTipIcon.Warning);
                return;
            }

            ConnectLive(roomId, remember: true);
        }
    }

    private void ConnectLive(long roomId, bool remember)
    {
        if (_danmaku == null)
        {
            _danmaku = new DanmakuClient();
            _danmaku.Received += OnDanmaku;
            _danmaku.ConnectionChanged += connected => _ui.Post(_ => OnLiveConnectionChanged(connected), null);
            _danmaku.StatusChanged += status =>
            {
                Logger.Write("danmaku: " + status);
                _ui.Post(_ => UpdateLiveStatus(status), null);
            };
        }

        _danmaku.Start(roomId);

        if (remember)
        {
            _settings.LiveRoomId = (int)roomId;
            _settings.LiveAutoConnect = true;
            _settings.Save();
        }

        _itemLiveDisconnect.Enabled = true;
        UpdateLiveStatus("正在连接直播间 " + roomId + " …");
        LiveNotify("Music Toppin", "正在连接直播间 " + roomId + " 的弹幕…", ToolTipIcon.Info);
    }

    private void DisconnectLive()
    {
        if (_danmaku != null) _danmaku.Stop();

        _settings.LiveAutoConnect = false;
        _settings.Save();

        _itemLiveDisconnect.Enabled = false;
        UpdateLiveStatus("未连接直播间");
    }

    private void ShowLiveStatus()
    {
        using (var form = new LiveStatusForm(BuildLiveSnapshot, () =>
        {
            var room = _settings.LiveRoomId > 0
                ? _settings.LiveRoomId
                : (int)(_danmaku != null ? _danmaku.RoomId : 0);
            if (room > 0) ConnectLive(room, remember: false);
        }, () => HandleDanmaku(new DanmakuMessage { Text = "点歌 起风了", UserName = "本地测试", Uid = 1 })))
        {
            form.ShowDialog(_hud);
        }
    }

    private LiveStatusSnapshot BuildLiveSnapshot()
    {
        var client = _danmaku;

        return new LiveStatusSnapshot
        {
            RoomId = client != null && client.RoomId > 0 ? client.RoomId : _settings.LiveRoomId,
            LiveStatus = client != null ? client.LiveStatus : -1,
            Connected = client != null && client.IsConnected,
            Running = client != null && client.IsRunning,
            MessageCount = client != null ? client.MessageCount : 0,
            LastMessageUtc = client != null ? client.LastMessageUtc : DateTime.MinValue,
            LastError = client != null ? client.LastError : "",
            LastRequestResult = _lastRequestResult,
            Recent = new List<string>(_recentDanmaku)
        };
    }

    private void OnLiveConnectionChanged(bool connected)
    {
        if (_danmaku == null) return;

        if (connected)
        {
            if (_danmaku.LiveStatus == 0)
            {
                // 连接是用户主动点的，这种情况值得弹一次气泡（平时点歌流程仍然静默）
                _tray.ShowBalloonTip(6000, "直播间未开播",
                    "直播间 " + _danmaku.RoomId + " 现在没有开播，B 站不会推送弹幕，点歌不会有反应。\n" +
                    "开播后在「直播间状态」里点「重新连接」。",
                    ToolTipIcon.Warning);
                UpdateLiveStatus("直播间 " + _danmaku.RoomId + " 未开播（收不到弹幕）");
            }
            else
            {
                LiveNotify("Music Toppin",
                    "弹幕已连接：直播间 " + _danmaku.RoomId + "\n观众发「点歌 BV…」即可入队。", ToolTipIcon.Info);
            }
        }

        UpdateQueueMenu();
    }

    /// <summary>更新一行状态（连接中/重连/断开这类瞬时文案）。</summary>
    private void UpdateLiveStatus(string status)
    {
        if (_liveStatus == null) return;
        _liveStatus.Text = status;
        UpdateTrayText();
    }

    /// <summary>按当前连接/队列状态重算那一行。</summary>
    private void RefreshLiveStatus()
    {
        if (_liveStatus == null) return;

        if (_danmaku != null && _danmaku.IsConnected)
        {
            var live = _danmaku.LiveStatus == 0 ? "（未开播）" : "";
            _liveStatus.Text = "直播间 " + _danmaku.RoomId + live +
                               " · 弹幕 " + _danmaku.MessageCount + " 条 · 队列 " + _queue.Count + " 首";
        }
        else
        {
            _liveStatus.Text = "未连接直播间";
        }

        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        var text = "Music Toppin";
        if (_danmaku != null && _danmaku.IsConnected) text += " · 直播间 " + _danmaku.RoomId;
        if (_queue.Count > 0) text += " · 点歌 " + _queue.Count;

        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private void OnDanmaku(DanmakuMessage message)
    {
        if (message == null) return;

        if (_danmakuReceived == 0)
        {
            Logger.Write("danmaku first: " + message.UserName + ": " + message.Text);
        }
        _danmakuReceived++;

        _ui.Post(_ => HandleDanmaku(message), null);
    }

    private void HandleDanmaku(DanmakuMessage message)
    {
        if (message == null) return;

        AddRecentDanmaku(message.UserName + "：" + message.Text);

        SongRequest request;
        if (!SongRequestParser.TryParse(message.Text, message.UserName, message.Uid, out request))
        {
            var text = message.Text ?? "";
            var looksLikeRequest = text.Contains("点歌") || text.Contains("點歌") ||
                                   text.StartsWith("qq ", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("wy ", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("am ", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("bv ", StringComparison.OrdinalIgnoreCase);

            if (looksLikeRequest)
            {
                _lastRequestResult = "格式没认出来（必须以「点歌 」开头）：" + text;
                Logger.Write("danmaku parse miss: " + message.UserName + ": " + text);
            }
            return;
        }

        var result = _queue.Enqueue(request);
        if (result.Accepted)
        {
            _lastRequestResult = "已入队（第 " + result.Position + " 位）：" + request.SourceLabel + " " + request.Query;
            Logger.Write("song request: " + message.UserName + " -> " + request.SourceLabel + " " + request.Query + " (#" + result.Position + ")");
            LiveNotify("已加入点歌队列（第 " + result.Position + " 位）",
                message.UserName + "：" + request.Query, ToolTipIcon.Info);
            ShowOverlayToast("已加入队列：" + request.Query + "　" + message.UserName);
            UpdateQueueMenu();
            StartNextQueuedSong();
        }
        else
        {
            _lastRequestResult = "被拒绝（" + result.Message + "）：" + request.Query;
            Logger.Write("song request rejected: " + result.Message + " | " + message.UserName + ": " + message.Text);
        }

        RefreshLiveStatus();
    }

    private static string DescribeResolveFailure(SongRequest request)
    {
        if (request == null) return "没有点歌请求";

        if (request.Source == SongSource.QQ && !QQMusic.IsLoggedIn)
        {
            return "QQ 音乐没在应用内登录（客户端登录不导出取流 key），去「曲源（登录）→ QQ 音乐：登录」扫码一次";
        }

        if (request.Source == SongSource.Netease && string.IsNullOrWhiteSpace(Netease.Cookie))
        {
            return "网易云是游客身份，这首没有免费版权";
        }

        return "没版权 / 需要登录 / 取不到直链";
    }

    private void AddRecentDanmaku(string line)
    {
        _recentDanmaku.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        while (_recentDanmaku.Count > 12) _recentDanmaku.RemoveAt(0);
    }

    private void UpdateQueueMenu()
    {
        if (_itemQueueNext == null) return;

        var count = _queue.Count;
        _itemQueueNext.Text = count > 0 ? "队列：下一首（还有 " + count + " 首）" : "队列：空";
        _itemQueueNext.Enabled = count > 0;
        _itemQueueClear.Enabled = count > 0;
        if (_itemQueueList != null)
        {
            _itemQueueList.Text = count > 0 ? "点歌队列（" + count + " 首）" : "点歌队列：空";
        }

        RebuildQueueList();
        RefreshLiveStatus();
    }

    /// <summary>托盘里的队列列表：每次展开时重建，最多列 10 首。</summary>
    private void RebuildQueueList()
    {
        if (_itemQueueList == null) return;

        _itemQueueList.DropDownItems.Clear();

        if (_queue.Count == 0)
        {
            _itemQueueList.DropDownItems.Add(new ToolStripMenuItem("队列是空的（观众发「点歌 歌名」）") { Enabled = false });
        }
        else
        {
            var index = 1;
            var preview = new List<string>();
            foreach (var item in _queue.Items.Take(10))
            {
                var text = index + ". " + item.DisplayName +
                           (string.IsNullOrWhiteSpace(item.Requester) ? "" : "  ·  " + item.Requester);
                _itemQueueList.DropDownItems.Add(new ToolStripMenuItem(text) { Enabled = false });
                preview.Add(text);
                index++;
            }

            Logger.Write("queue list: " + string.Join(" | ", preview));

            if (_queue.Count > 10)
            {
                _itemQueueList.DropDownItems.Add(new ToolStripMenuItem("… 还有 " + (_queue.Count - 10) + " 首") { Enabled = false });
            }
        }

        _itemQueueList.DropDownItems.Add(new ToolStripSeparator());
        _itemQueueList.DropDownItems.Add(_itemQueueNext);
        _itemQueueList.DropDownItems.Add(_itemQueueClear);
    }

    private async void StartNextQueuedSong()
    {
        if (_playingFromQueue) return;

        // 手动点播（托盘「点歌测试」）还在放：等它结束，避免两路同时出声
        if (_player != null && _player.Current != null) return;

        var item = _queue.TakeNext();
        if (item == null) return;

        _playingFromQueue = true;
        _videoBvid = "";
        StopVideoTimer();
        UpdateQueueMenu();

        ResolvedSong song = null;
        try
        {
            song = await ResolveQueuedSongAsync(item);
        }
        catch (Exception ex)
        {
            Logger.Write("queue resolve: " + ex.Message);
        }

        if (song == null)
        {
            var label = item.Request != null ? item.Request.SourceLabel : "未知来源";
            var query = item.Request != null ? item.Request.Query : item.DisplayName;
            var reason = DescribeResolveFailure(item.Request);

            _lastRequestResult = "已入队但放不了：" + label + " " + query + "（" + reason + "）";
            Logger.Write("queue skip: " + label + " " + item.DisplayName + " | " + reason);
            LiveNotify("Music Toppin",
                "这首放不了：" + label + " " + query + "\n" + reason,
                ToolTipIcon.Warning);
            ShowOverlayToast("放不了：" + label + " " + query);

            _queue.ClearCurrent();
            _playingFromQueue = false;
            UpdateQueueMenu();
            StartNextQueuedSong();
            return;
        }

        item.Title = song.Title;
        item.Artist = song.Artist;
        item.ResolvedUrl = song.AudioUrl;
        item.Duration = song.Duration;
        item.Resolved = true;

        // 视频点播：叠加层 /video 小窗里同步播放
        _videoBvid = item.Request != null && item.Request.IsVideo ? ExtractVideoBvid(item, song) : "";

        if (_videoBvid.Length > 0 && _settings.VideoAudio == 1)
        {
            StartVideoPlayback(song);
            return;
        }

        try
        {
            await EnsurePlayer().PlayAsync(song);
            PushOwnState(song, new PlaybackState { Playing = true, Duration = song.Duration });
            Logger.Write("queue play: " + song.Title + (string.IsNullOrEmpty(song.Artist) ? "" : " - " + song.Artist));
            LiveNotify("正在播放点歌",
                item.Requester + "：" + song.Title + (string.IsNullOrEmpty(song.Artist) ? "" : " - " + song.Artist),
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Write("queue play failed: " + ex.Message);
            _lastRequestResult = "播放失败：" + song.Title + "（" + ex.Message + "）";
            LiveNotify("Music Toppin", ex.Message, ToolTipIcon.Warning);

            _queue.ClearCurrent();
            _playingFromQueue = false;
            UpdateQueueMenu();
            StartNextQueuedSong();
        }
    }

    private static string ExtractVideoBvid(QueueItem item, ResolvedSong song)
    {
        string bvid;

        var pageUrl = song != null ? song.PageUrl : null;
        if (!string.IsNullOrEmpty(pageUrl) && Bilibili.TryExtractBvid(pageUrl, out bvid)) return bvid;

        var query = item != null && item.Request != null ? item.Request.Query : null;
        if (!string.IsNullOrEmpty(query) && Bilibili.TryExtractBvid(query, out bvid)) return bvid;

        return "";
    }

    private async Task<ResolvedSong> ResolveQueuedSongAsync(QueueItem item)
    {
        if (item == null || item.Request == null) return null;

        var request = item.Request;
        var song = await Task.Run(() => SongResolvers.Resolve(request));
        if (song != null)
        {
            Logger.Write("resolved: " + request.SourceLabel + " " + request.Query + " -> " + song.Title);
        }
        return song;
    }

    private void ClearQueue()
    {
        _queue.Clear();
        UpdateQueueMenu();
        _tray.ShowBalloonTip(1800, "Music Toppin", "点歌队列已清空", ToolTipIcon.Info);
    }

    private OverlayState BuildOverlayState()
    {
        var np = _latest;
        var theme = HudRenderer.Theme;

        return new OverlayState
        {
            Title = np.HasSession ? np.Title : "",
            Artist = np.HasSession ? np.Artist : "",
            Playing = np.Playing,
            Position = np.Position.TotalSeconds,
            Duration = np.Duration.TotalSeconds,
            HasTimeline = np.HasTimeline && np.Duration > TimeSpan.Zero,
            PrevTitle = np.PrevTitles != null && np.PrevTitles.Length > 0 ? (np.PrevTitles[0] ?? "") : "",
            PrevArtist = np.PrevArtists != null && np.PrevArtists.Length > 0 ? (np.PrevArtists[0] ?? "") : "",
            Accent = ToHex(theme.Accent),
            TitleColor = ToHex(theme.Title),
            ArtistColor = ToHex(theme.Artist),
            Opacity = Math.Max(0.05f, Math.Min(1f, _settings.CardOpacity / 100f)),
            NextTitle = np.NextTitle ?? "",
            NextArtist = np.NextArtist ?? "",
            QueueCount = np.QueueCount,
            Toast = DateTime.UtcNow < _toastUntilUtc ? _toastText : "",
            ToastId = _toastId,
            VideoBvid = _videoBvid,
            VideoSound = _settings.VideoAudio == 1,
            Show = np.HasSession && np.Title.Length > 0 && _hud.Visible || (np.HasSession && np.Title.Length > 0 && _settings.Overlay)
        };
    }

    private static string ToHex(Color color)
    {
        return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
    }

    private void ToggleOverlay()
    {
        _settings.Overlay = !_settings.Overlay;
        _itemOverlay.Checked = _settings.Overlay;
        _settings.Save();

        if (_settings.Overlay) StartOverlay(notify: true);
        else StopOverlay();
    }

    private void StartOverlay(bool notify)
    {
        if (_overlay == null)
        {
            _overlay = new OverlayServer(BuildOverlayState);
        }

        if (!_overlay.Start(_settings.OverlayPort))
        {
            _settings.Overlay = false;
            _itemOverlay.Checked = false;
            _settings.Save();
            _tray.ShowBalloonTip(3000, "Music Toppin", "启动叠加层失败：4681-4750 端口都被占用", ToolTipIcon.Warning);
            return;
        }

        if (_settings.OverlayPort != _overlay.Port)
        {
            _settings.OverlayPort = _overlay.Port;
            _settings.Save();
        }

        _itemCopyOverlay.Enabled = true;
        _itemCopyVideo.Enabled = true;
        _itemCopyOverlay.Text = "复制叠加层地址（端口 " + _overlay.Port + "）";

        var url = _overlay.Url;
        try { Clipboard.SetText(url); } catch { }
        Logger.Write("overlay url: " + url);

        // 启动时自动恢复的叠加层不弹气泡（直播/开机时不想被通知打扰），只在手动开启时提示
        if (!notify) return;

        _tray.ShowBalloonTip(6000, "Music Toppin",
            "叠加层地址已复制：" + url + "\n在 OBS 添加「浏览器源」，宽 400 高 320。\n" +
            "视频点播再加一个浏览器源：" + url + "video（480×270）。\n现在可以关掉桌面浮窗，观众仍能看到。",
            ToolTipIcon.Info);
    }

    private void StopOverlay()
    {
        if (_overlay == null) return;
        _overlay.Dispose();
        _overlay = null;
        _itemCopyOverlay.Enabled = false;
        _itemCopyVideo.Enabled = false;
        _itemCopyOverlay.Text = "复制叠加层地址";
        Logger.Write("overlay server stopped");
    }

    private void CopyOverlayUrl(string suffix)
    {
        if (_overlay == null) return;

        var url = _overlay.Url + suffix;
        try { Clipboard.SetText(url); } catch { }
        _tray.ShowBalloonTip(2500, "Music Toppin", "已复制：" + url, ToolTipIcon.Info);
    }
    private void ToggleOccluded()
    {
        _settings.CaptureOccluded = !_settings.CaptureOccluded;
        _itemOccluded.Checked = _settings.CaptureOccluded;

        // 两种采集模式互斥
        if (_settings.CaptureOccluded && _settings.CaptureOffscreen)
        {
            _settings.CaptureOffscreen = false;
            _itemOffscreen.Checked = false;
            _hud.SetCaptureOffscreen(false);
        }

        _settings.Save();

        if (_settings.CaptureOccluded)
        {
            if (!_hud.Visible)
            {
                _settings.Visible = true;
                _itemShow.Checked = true;
                _settings.Save();
                ShowHud();
            }

            _hud.SetCaptureOccluded(true);
            _tray.ShowBalloonTip(7000, "Music Toppin",
                "已进入遮挡模式：浮窗压在其它窗口后面。\n全屏/无边框游戏会盖住它；切回桌面时可能露出来。\n直播软件里用「窗口采集」选 Music Toppin。\n想恢复：再点一次这个菜单。",
                ToolTipIcon.Info);
            return;
        }

        _hud.SetCaptureOccluded(false);
        _tray.ShowBalloonTip(2500, "Music Toppin", "已退出遮挡模式", ToolTipIcon.Info);
    }
    private void ToggleOffscreen()
    {
        _settings.CaptureOffscreen = !_settings.CaptureOffscreen;
        _itemOffscreen.Checked = _settings.CaptureOffscreen;

        if (_settings.CaptureOffscreen && _settings.CaptureOccluded)
        {
            _settings.CaptureOccluded = false;
            _itemOccluded.Checked = false;
            _hud.SetCaptureOccluded(false);
        }

        _settings.Save();

        if (_settings.CaptureOffscreen)
        {
            if (!_hud.Visible)
            {
                _settings.Visible = true;
                _itemShow.Checked = true;
                _settings.Save();
                ShowHud();
            }

            _hud.SetCaptureOffscreen(true);

            _tray.ShowBalloonTip(7000, "Music Toppin",
                "浮窗已移到屏幕外。\n在直播姬/直播伴侣里添加「窗口采集」，选择窗口「Music Toppin」。\n想恢复：再点一次这个菜单。",
                ToolTipIcon.Info);
            return;
        }

        _hud.SetCaptureOffscreen(false);
        _tray.ShowBalloonTip(2500, "Music Toppin", "浮窗已回到屏幕", ToolTipIcon.Info);
    }
    private void ShowWizard()
    {
        if (_wizardOpen) return;
        _wizardOpen = true;
        _settings.WizardShown = true;
        _settings.Save();

        try
        {
            using (var wizard = new WizardForm(_settings, green =>
            {
                _settings.Green = green;
                _hud.SetGreen(green);
                _itemGreen.Checked = green;
                _settings.Save();
            }))
            {
                wizard.ShowDialog(_hud);
            }
        }
        catch (Exception ex)
        {
            Logger.Write("wizard: " + ex.Message);
        }
        finally
        {
            _wizardOpen = false;
        }
    }
    private static Icon LoadIcon()
    {
        try
        {
            var path = AppPaths.ExePath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    /// <summary>把矩形夹回它所在显示器的工作区。负坐标是合法的副屏坐标，不能当非法值。</summary>
    private static Rectangle ClampToWorkArea(Rectangle rect)
    {
        var screen = Screen.FromRectangle(rect);
        var area = screen != null ? screen.WorkingArea : new Rectangle(0, 0, 1920, 1080);

        var width = Math.Min(rect.Width, area.Width);
        var height = Math.Min(rect.Height, area.Height);

        var x = Math.Max(area.Left, Math.Min(rect.X, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(rect.Y, area.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private static bool IsOnScreen(Rectangle rect)
    {
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(rect)) return true;
        }
        return false;
    }

    /// <summary>按窗口实际像素尺寸定位，避免高 DPI 下用设计宽度算位置导致右侧被裁掉。</summary>
    private void PlaceHud()
    {
        var width = _hud.Width > 0 ? _hud.Width : (int)HudRenderer.DesignWidth;
        var height = _hud.Height > 0 ? _hud.Height : 180;

        var x = _settings.X;
        var y = _settings.Y;

        if (!IsOnScreen(new Rectangle(x, y, width, height)))
        {
            var area = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1920, 1080);
            x = area.Right - width - 24;
            y = area.Top + 110;
        }

        var clamped = ClampToWorkArea(new Rectangle(x, y, width, height));
        _hud.Location = clamped.Location;
    }

    private void ShowHud()
    {
        if (!_hud.Visible)
        {
            _hud.Show();
        }
        _hud.TopMost = true;
        _hud.Render();
        PlaceHud();
        EnsureHotkeys();
    }

    private void HideHud()
    {
        _hud.CancelDrag();
        _hud.Hide();
    }

    private void ToggleVisible()
    {
        // 以真实可见性为准：自动隐藏后勾选态会和实际状态漂移
        if (_hud.Visible)
        {
            _settings.Visible = false;
            HideHud();
        }
        else
        {
            _settings.Visible = true;
            ShowHud();
            _hud.SetState(_latest, animate: false);
        }

        _itemShow.Checked = _hud.Visible;
        _settings.Save();
    }

    private void ToggleLock()
    {
        _lastLockToggleUtc = DateTime.UtcNow;
        _settings.Locked = !_settings.Locked;
        _hud.SetLocked(_settings.Locked);
        _hud.SetEditing(!_settings.Locked);
        _hud.SetEditing(!_settings.Locked);
        _itemLock.Checked = _settings.Locked;
        _settings.Save();
    }

    private void ToggleGreen()
    {
        _settings.Green = !_settings.Green;
        _hud.SetGreen(_settings.Green);
        _itemGreen.Checked = _settings.Green;
        _settings.Save();
    }

    private void ToggleAutoStart()
    {
        _settings.ApplyAutoStart(!_settings.AutoStart);
        _itemAutoStart.Checked = _settings.AutoStart;
        _settings.Save();
    }

    private void SetStyle(bool compact)
    {
        _settings.Compact = compact;
        _hud.SetCompact(compact);
        _itemCompact.Checked = compact;
        _itemKillfeed.Checked = !compact;
        _settings.Save();
    }

    /// <summary>
    /// 只注册一个「锁定/解锁」热键。不同机器占用情况差别很大（本机 Ctrl+Alt+M 就被占了），
    /// 按顺序挑第一个能注册成功的组合，并把结果写进托盘菜单。
    /// </summary>
    private void EnsureHotkeys()
    {
        if (!_settings.Hotkeys || _hotkeysRegistered) return;
        if (!_hud.IsHandleCreated) return;

        // 用户自定义的快捷键优先
        if (!string.IsNullOrWhiteSpace(_settings.Hotkey))
        {
            uint customMods, customVk;
            string customDisplay;
            if (TryParseHotkey(_settings.Hotkey, out customMods, out customVk, out customDisplay))
            {
                if (RegisterHotKey(_hud.Handle, HK_LOCK, customMods | MOD_NOREPEAT, customVk))
                {
                    _hotkeyLabel = customDisplay;
                    _hotkeysRegistered = true;
                    Log("registered custom " + customDisplay);
                    UpdateHotkeyLabel();
                    return;
                }
                Log("custom hotkey busy: " + _settings.Hotkey);
            }
        }

        var modsList = new[]
        {
            MOD_ALT | MOD_SHIFT | MOD_NOREPEAT,
            MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT
        };
        var names = new[] { "Alt+Shift+M", "Ctrl+Shift+M", "Ctrl+Alt+M", "Ctrl+Alt+Shift+M" };

        for (var i = 0; i < modsList.Length; i++)
        {
            if (RegisterHotKey(_hud.Handle, HK_LOCK, modsList[i], VK_M))
            {
                _hotkeyLabel = names[i];
                _hotkeysRegistered = true;
                Log("registered " + names[i]);
                UpdateHotkeyLabel();
                return;
            }
        }

        _hotkeyLabel = "注册失败（快捷键都被占用）";
        Log("hotkey registration failed");
        UpdateHotkeyLabel();
    }

    private static bool TryParseHotkey(string text, out uint modifiers, out uint vk, out string display)
    {
        modifiers = 0;
        vk = 0;
        display = "";

        if (string.IsNullOrWhiteSpace(text)) return false;

        Keys key = Keys.None;
        var parts = text.Split('+');
        foreach (var raw in parts)
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    continue;
            }

            Keys parsed;
            if (!Enum.TryParse(token, true, out parsed) || parsed == Keys.None) return false;
            key = parsed;
        }

        if (key == Keys.None || modifiers == 0) return false;

        vk = (uint)key;
        display = FormatHotkey(modifiers, key);
        return true;
    }

    private static string FormatHotkey(uint modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private void ShowHotkeyDialog()
    {
        using (var dialog = new HotkeyDialog(_settings.Hotkey))
        {
            if (dialog.ShowDialog(_hud) != DialogResult.OK) return;

            var result = dialog.Result;
            if (result == null) return;

            if (result.Length == 0)
            {
                ReleaseHotkeys();
                _settings.Hotkeys = false;
                _settings.Hotkey = "";
                _hotkeyLabel = "已禁用";
                UpdateHotkeyLabel();
                _settings.Save();
                _tray.ShowBalloonTip(2000, "Music Toppin", "快捷键已禁用（托盘菜单仍可解锁）", ToolTipIcon.Info);
                return;
            }

            ApplyCustomHotkey(result);
        }
    }

    /// <summary>尝试注册自定义组合；失败则回滚到原来的（自动挑选的）组合。</summary>
    private void ApplyCustomHotkey(string combo)
    {
        var previous = _settings.Hotkey;
        var previousHotkeys = _settings.Hotkeys;

        ReleaseHotkeys();
        _hotkeysRegistered = false;

        uint modifiers, vk;
        string display;
        if (TryParseHotkey(combo, out modifiers, out vk, out display) &&
            RegisterHotKey(_hud.Handle, HK_LOCK, modifiers | MOD_NOREPEAT, vk))
        {
            _hotkeysRegistered = true;
            _hotkeyLabel = display;
            _settings.Hotkey = display;
            _settings.Hotkeys = true;
            _settings.Save();
            UpdateHotkeyLabel();
            Log("registered custom " + display);
            _tray.ShowBalloonTip(2000, "Music Toppin", "锁定快捷键已改为 " + display, ToolTipIcon.Info);
            return;
        }

        _settings.Hotkey = previous;
        _settings.Hotkeys = previousHotkeys;
        EnsureHotkeys();
        _settings.Save();
        _tray.ShowBalloonTip(2500, "Music Toppin",
            "这个组合已被其它程序占用，仍使用 " + _hotkeyLabel, ToolTipIcon.Warning);
    }
    private void UpdateHotkeyLabel()
    {
        if (_itemHotkey == null) return;
        _itemHotkey.Text = "锁定快捷键：" + _hotkeyLabel + "（点击修改）";
    }

    private void ReleaseHotkeys()
    {
        if (!_hotkeysRegistered || !_hud.IsHandleCreated) return;
        UnregisterHotKey(_hud.Handle, HK_LOCK);
        _hotkeysRegistered = false;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "musictoppin.log"),
                DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    private void OnHotkey(int id)
    {
        if (id == HK_LOCK) ToggleLock();
    }

    private void CheckRelock()
    {
        if (_settings.Locked || !_hud.IsEditing) return;
        if ((DateTime.UtcNow - _lastLockToggleUtc).TotalSeconds < 3) return;   // 解锁后的宽限
        if ((DateTime.UtcNow - _hud.LastInteractionUtc).TotalSeconds < 12) return;
        ToggleLock();
    }

    private void ShowMenuAtCursor()
    {
        _menu.Show(Cursor.Position);
    }

    private async Task SkipAsync(bool previous)
    {
        if (_service is null) return;
        if (previous) await _service.SkipPreviousAsync();
        else await _service.SkipNextAsync();
    }

    private void Apply(NowPlaying np)
    {
        _latest = np;
        if (np.HasSession)
        {
            _lastSessionUtc = DateTime.UtcNow;
            if (_settings.Visible && !_hud.Visible) ShowHud();
            if (_hud.Visible) _hud.SetState(np, animate: true);
        }
        else if (_hud.Visible)
        {
            _hud.SetState(np, animate: false);
        }
    }

    private void CheckIdle()
    {
        if (_demo) return;

        // 2 秒一次，约每分钟清一次过期的冷却记录；顺手记一下弹幕计数，方便排查
        if (++_idleTicks % 30 == 0)
        {
            _queue.TrimCooldowns();

            if (_danmaku != null && _danmaku.IsConnected)
            {
                Logger.Write("danmaku tick: room " + _danmaku.RoomId + " live=" + _danmaku.LiveStatus +
                             " messages=" + _danmaku.MessageCount + " queue=" + _queue.Count);
            }
        }

        if (_latest.HasSession || !_hud.Visible) return;
        if ((DateTime.UtcNow - _lastSessionUtc).TotalSeconds < 4) return;
        HideHud();
    }

    private void DemoTick()
    {
        var track = DemoTracks[_demoIndex % DemoTracks.Length];
        var elapsed = (DateTime.UtcNow - _demoStart).TotalSeconds;

        var trackSeconds = _demoFast ? 12 : track.Seconds;
        if (elapsed >= trackSeconds)
        {
            _demoPrevTitle = track.Title;
            _demoPrevArtist = track.Artist;
            _demoPlayed++;
            _demoIndex++;
            _demoStart = DateTime.UtcNow;
            track = DemoTracks[_demoIndex % DemoTracks.Length];
            elapsed = 0;
        }

        var titles = new string[NowPlaying.HistorySlots];
        var artists = new string[NowPlaying.HistorySlots];
        var historyCount = 0;
        var total = DemoTracks.Length;
        if (_demoIndex > 0)
        {
            var prev1 = DemoTracks[(_demoIndex - 1 + total) % total];
            titles[0] = prev1.Title;
            artists[0] = prev1.Artist;
            historyCount = 1;
        }
        if (_demoIndex > 1)
        {
            var prev2 = DemoTracks[(_demoIndex - 2 + total) % total];
            titles[1] = prev2.Title;
            artists[1] = prev2.Artist;
            historyCount = 2;
        }

        var np = new NowPlaying(
            true, track.Title, track.Artist,
            TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(trackSeconds),
            true, true, titles, artists, historyCount, Math.Max(1, _demoPlayed));

        // 演示数据也带上「下一首」，方便上屏预览
        var nextTrack = DemoTracks[(_demoIndex + 1) % DemoTracks.Length];
        np.NextTitle = nextTrack.Title;
        np.NextArtist = nextTrack.Artist;
        np.QueueCount = 2;

        Apply(np);
    }

    private void ExitApp()
    {
        try
        {
            _idleTimer.Stop();
            _relockTimer.Stop();
            _idleTimer.Dispose();
            _relockTimer.Dispose();
            _demoTimer?.Stop();
            _demoTimer?.Dispose();
            ReleaseHotkeys();
            try { _danmaku?.Dispose(); } catch { }
            try { _player?.Dispose(); } catch { }
            _service?.Dispose();
            StopOverlay();

            foreach (Form form in Application.OpenForms)
            {
                if (ReferenceEquals(form, _hud)) continue;
                try { form.Close(); } catch { }
            }

            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _settings.X = _hud.Left;
            _settings.Y = _hud.Top;
            _settings.Save();
            _hud.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Write("exit: " + ex.Message);
        }

        ExitThread();
    }
}
