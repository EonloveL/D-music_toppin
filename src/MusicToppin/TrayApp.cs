using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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
    private readonly ToolStripMenuItem _itemRing;
    private ToolStripMenuItem _itemSafeMedia;
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
        VideoCache.CleanupOld();

        // 曲源登录态：cookie 存注册表，启动时装回静态字段
        Netease.Cookie = _settings.NeteaseCookie ?? "";
        QQMusic.Cookie = _settings.QQMusicCookie ?? "";
        QQMusic.Uin = _settings.QQMusicUin ?? "";
        QQMusic.ClientLoggedIn = SourceCheck.IsQQMusicClientLoggedIn();
        WindowPlayers.MediaKeysEnabled = !_settings.DisableMediaKeys;
        _noBiliLogin = Environment.GetCommandLineArgs().Any(a => a.Equals("--no-bili-login", StringComparison.OrdinalIgnoreCase));
        BilibiliLive.WsNoCookie = Environment.GetCommandLineArgs().Any(a => a.Equals("--ws-no-cookie", StringComparison.OrdinalIgnoreCase));
        BilibiliLive.Cookie = _noBiliLogin ? "" : (_settings.BilibiliCookie ?? "");
        Logger.Write("bilibili cookie: len=" + BilibiliLive.Cookie.Length + " uidSetting=" + (_settings.BilibiliUid ?? ""));

        if (!string.IsNullOrWhiteSpace(_settings.BilibiliCookie) && ParseUid(_settings.BilibiliUid) == 0)
        {
            var savedCookie = _settings.BilibiliCookie;
            Task.Run(() => FetchBilibiliUid(savedCookie)).ContinueWith(task =>
            {
                var uid = task.Result;
                Logger.Write("bilibili uid backfill: " + uid);
                if (uid <= 0) return;

                _settings.BilibiliUid = uid.ToString();
                _settings.Save();
                _ui.Post(_ => { if (_danmaku != null) _danmaku.Uid = uid; }, null);
            });
        }
        Logger.Write("sources: netease=" + (Netease.IsLoggedIn ? "logged in" : "guest") +
                     ", qq=" + (QQMusic.IsLoggedIn ? "logged in" : "guest"));

        _hud = new HudForm();
        _hud.SetCompact(_settings.Compact);
        _hud.SetRing(_settings.Ring);
        _hud.SetGreen(_settings.Green);
        _hud.SetLocked(_settings.Locked);
        _hud.SetEditing(!_settings.Locked);
        _hud.PrevRequested += () => _ = SkipAsync(previous: true);
        _hud.NextRequested += () => _ = SkipAsync(previous: false);
        _hud.ToggleLockRequested += ToggleLock;
        _hud.MenuRequested += ShowMenuAtCursor;
        _hud.HotkeyPressed += OnHotkey;
        _hud.DragFinished += () =>
        {
            _anchor = "";
            _settings.Anchor = "";

            // 先夹回工作区，再按最终位置判断贴边方向
            var clamped = ClampToWorkArea(_hud.Bounds);
            _hud.Location = clamped.Location;
            UpdateRingSide();

            // 环状方向可能改变窗口尺寸，再夹一次
            if (_hud.IsRing) _hud.Location = ClampToWorkArea(_hud.Bounds).Location;

            _settings.X = _hud.Left;
            _settings.Y = _hud.Top;
            _settings.Save();
        };
        _hud.SizeChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_anchor)) MoveHudTo(_anchor, fromUser: false);
        };

        PlaceHud();

        _itemShow = new ToolStripMenuItem("显示浮窗", null, (_, _) => ToggleVisible()) { Checked = _settings.Visible };
        _itemLock = new ToolStripMenuItem("锁定浮窗（鼠标穿透）", null, (_, _) => ToggleLock()) { Checked = _settings.Locked };
        _itemGreen = new ToolStripMenuItem("绿幕模式（直播采集）", null, (_, _) => ToggleGreen()) { Checked = _settings.Green };
        _itemKillfeed = new ToolStripMenuItem("击杀播报", null, (_, _) => SetStyle(0)) { Checked = _settings.Style == 0 };
        _itemCompact = new ToolStripMenuItem("极简", null, (_, _) => SetStyle(1)) { Checked = _settings.Style == 1 };
        _itemRing = new ToolStripMenuItem("环状毛玻璃（新）", null, (_, _) => SetStyle(2)) { Checked = _settings.Style == 2 };
        _itemAutoStart = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart()) { Checked = _settings.AutoStart };

        _itemWizard = new ToolStripMenuItem("上屏向导…", null, (_, _) => ShowWizard());
        _itemCheckUpdate = new ToolStripMenuItem("检查更新…", null, (_, _) => CheckForUpdates(manual: true));
        _itemPlayBv = new ToolStripMenuItem("点歌测试（播放 BV）…", null, (_, _) => PromptAndPlayBv());
        _itemStopPlay = new ToolStripMenuItem("停止播放", null, (_, _) => StopAllPlayback());

        _liveStatus = new ToolStripMenuItem("未连接直播间") { Enabled = false };
        _itemLiveDiag = new ToolStripMenuItem("直播间状态…", null, (_, _) => ShowLiveStatus());
        _itemRequestPolicy = BuildRequestPolicyMenu();
        _itemSkipPolicy = BuildSkipPolicyMenu();
        _itemRemovePolicy = BuildRemovePolicyMenu();
        _itemSessionRefresh = BuildSessionRefreshMenu();
        _itemLiveConnect = new ToolStripMenuItem("连接直播间…", null, (_, _) => PromptLiveRoom());
        _itemLiveDisconnect = new ToolStripMenuItem("断开直播间", null, (_, _) => DisconnectLive()) { Enabled = false };
        _itemBiliLogin = new ToolStripMenuItem("B 站：登录（扫码，弹幕用）…", null, (_, _) => LoginBilibili());
        _itemBiliLogout = new ToolStripMenuItem("B 站：退出登录", null, (_, _) => LogoutBilibili());
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
            _itemBiliLogin,
            _itemBiliLogout,
            new ToolStripSeparator(),
            _liveStatus,
            _itemLiveDiag,
            _itemQueueList,
            _itemRequestPolicy,
            _itemSkipPolicy,
            _itemRemovePolicy,
            _itemSessionRefresh,
            new ToolStripSeparator(),
            _itemLiveToast
        });

        // 托盘根菜单也放一份权限入口：直播中不用展开两层就能随时切回「所有人」
        _permissionMenu = new ToolStripMenuItem("弹幕权限（点歌 / 切歌）");
        _permissionMenu.DropDownItems.Add(BuildRequestPolicyMenu());
        _permissionMenu.DropDownItems.Add(BuildSkipPolicyMenu());
        _permissionMenu.DropDownOpening += (_, _) => UpdatePermissionMenuText();
        UpdatePermissionMenuText();

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
        _itemLyricsOverlay = new ToolStripMenuItem("歌词叠层（暹罗猫）", null, (_, _) => ToggleLyricsOverlay()) { Checked = _settings.LyricsOverlayEnabled };
        _itemCopyLyrics = new ToolStripMenuItem("复制歌词叠层地址", null, (_, _) => CopyOverlayUrl("lyrics")) { Enabled = false };

        var styleMenu = new ToolStripMenuItem("样式");
        styleMenu.DropDownItems.Add(_itemKillfeed);
        styleMenu.DropDownItems.Add(_itemCompact);
        styleMenu.DropDownItems.Add(_itemRing);

        _colorMenu = BuildColorMenu();

        var positionMenu = new ToolStripMenuItem("位置");
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("左上", null, (_, _) => MoveHudTo("tl")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("右上", null, (_, _) => MoveHudTo("tr")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("左下", null, (_, _) => MoveHudTo("bl")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("右下", null, (_, _) => MoveHudTo("br")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("左侧", null, (_, _) => MoveHudTo("left")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("右侧", null, (_, _) => MoveHudTo("right")));
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("屏幕居中", null, (_, _) => MoveHudTo("center")));
        positionMenu.DropDownItems.Add(new ToolStripSeparator());
        positionMenu.DropDownItems.Add(new ToolStripMenuItem("自由拖动：先按快捷键解锁") { Enabled = false });

        var controlsMenu = new ToolStripMenuItem("播放控制");
        controlsMenu.DropDownItems.Add(new ToolStripMenuItem("切歌（下一首）", null, (_, _) => _ = SkipAsync(previous: false)));
        controlsMenu.DropDownItems.Add(new ToolStripMenuItem("上一首", null, (_, _) => _ = SkipAsync(previous: true)));
        controlsMenu.DropDownItems.Add(new ToolStripMenuItem("重播当前（本应用播放）", null, (_, _) => RestartOwnPlayback()));
        controlsMenu.DropDownItems.Add(new ToolStripSeparator());
        _volumeMenu = BuildVolumeMenu();
        controlsMenu.DropDownItems.Add(_volumeMenu);
        controlsMenu.DropDownItems.Add(BuildVideoAudioMenu());
        _itemVideoRequest = new ToolStripMenuItem("视频点播（BV 点歌）", null, (_, _) => ToggleVideoRequest()) { Checked = _settings.VideoRequestEnabled };
        controlsMenu.DropDownItems.Add(_itemVideoRequest);
        _itemSafeMedia = new ToolStripMenuItem("反作弊保守模式（禁用媒体键切歌）", null, (_, _) => ToggleMediaKeys()) { Checked = _settings.DisableMediaKeys };
        controlsMenu.DropDownItems.Add(new ToolStripSeparator());
        controlsMenu.DropDownItems.Add(_itemSafeMedia);

        _defaultPlaylistMenu = BuildDefaultPlaylistMenu();

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
            _defaultPlaylistMenu,
            liveMenu,
            _permissionMenu,
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
            _itemLyricsOverlay,
            _itemCopyLyrics,
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
        if (playBv != null && _settings.VideoRequestEnabled)
        {
            var bvid = playBv.Substring("--play-bv=".Length).Trim();
            if (bvid.Length > 0)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 1200 };
                timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); _ = PlayBvidAsync(bvid); };
                timer.Start();
            }
        }

        // 配置过默认歌单就自动续播；有手动点播/点歌正在放时会跳过，等它们结束再续
        if (!_demo && _settings.DefaultPlaylistEnabled && _settings.DefaultPlaylistSource != 0)
        {
            var playlistTimer = new System.Windows.Forms.Timer { Interval = 4500 };
            playlistTimer.Tick += (_, _) =>
            {
                playlistTimer.Stop();
                playlistTimer.Dispose();
                _ = StartDefaultPlaylistAsync();
            };
            playlistTimer.Start();
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

        // 调试用：--skip-after=6 在 6 秒后自动切下一首（验证切歌链路）
        var skipArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--skip-after=", StringComparison.OrdinalIgnoreCase));
        int skipSeconds;
        if (skipArg != null &&
            int.TryParse(skipArg.Substring("--skip-after=".Length), out skipSeconds) &&
            skipSeconds > 0)
        {
            var skipTimer = new System.Windows.Forms.Timer { Interval = skipSeconds * 1000 };
            skipTimer.Tick += (_, _) =>
            {
                skipTimer.Stop();
                skipTimer.Dispose();
                _ = SkipAsync(previous: false);
            };
            skipTimer.Start();
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
                    Uid = 10000 + fakeIndex,
                    IsLocalTest = true
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
    private ToolStripMenuItem _itemVideoRequest;
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
    private ToolStripMenuItem _itemLyricsOverlay;
    private ToolStripMenuItem _itemCopyLyrics;
    private ToolStripMenuItem _itemSkipPolicy;
    private ToolStripMenuItem _itemRequestPolicy;
    private ToolStripMenuItem _permissionMenu;
    private ToolStripMenuItem _itemRemovePolicy;
    private ToolStripMenuItem _itemSessionRefresh;
    private bool _noBiliLogin;
    private ToolStripMenuItem _itemBiliLogin;
    private ToolStripMenuItem _itemBiliLogout;
    private ToolStripMenuItem _itemLiveToast;
    private ToolStripMenuItem _defaultPlaylistMenu;
    private ToolStripMenuItem _defaultPlaylistStatus;
    private ToolStripMenuItem _defaultPlaylistToggle;
    private ToolStripMenuItem _defaultPlaylistNext;
    private ToolStripMenuItem _defaultPlaylistClear;
    private ToolStripMenuItem _defaultPlaylistUseNetease;
    private ToolStripMenuItem _defaultPlaylistUseQQ;
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
    private bool _playingDefaultPlaylist;
    private readonly List<PlaylistTrack> _defaultPlaylistTracks = new List<PlaylistTrack>();
    private int _defaultPlaylistIndex;
    private string _defaultPlaylistLoadedKey = "";
    private string _defaultPlaylistLoadedName = "";
    private Task<bool> _defaultPlaylistLoadTask;
    private string _defaultPlaylistFailedKey = "";
    private DateTime _defaultPlaylistFailedUtc;
    private DateTime _defaultPlaylistPauseUntil;
    private int _defaultPlaylistFailStreak;
    private bool _manualPlaybackPending;

    /// <summary>是否有应用自己的播放（点歌 / 默认歌单 / 视频 / 手动 BV）。</summary>
    private bool HasOwnPlayback
    {
        get
        {
            return _manualPlaybackPending || _playingFromQueue || _playingDefaultPlaylist ||
                   _videoBvid.Length > 0 || _videoSong != null ||
                   (_player != null && _player.Current != null);
        }
    }
    private int _idleTicks;
    private int _danmakuReceived;
    private readonly List<string> _recentDanmaku = new List<string>();
    private int _recentDanmakuVersion;
    private string _lastRequestResult = "";
    private string _toastText = "";
    private int _toastId;
    private DateTime _toastUntilUtc = DateTime.MinValue;
    private string _videoBvid = "";
    private System.Windows.Forms.Timer _videoTimer;
    private DateTime _videoStartedUtc;
    private TimeSpan _videoDuration;
    private ResolvedSong _videoSong;
    private int _videoNonce;
    private int _playGeneration;
    private ResolvedSong _activeSong;
    private OwnHistoryEntry _currentOwnEntry;
    private readonly List<OwnHistoryEntry> _ownHistory = new List<OwnHistoryEntry>();
    private readonly List<OwnHistoryEntry> _forwardOwn = new List<OwnHistoryEntry>();
    private bool _suppressOwnHistory;
    private bool _ringReverseOnce;
    private int _ownPlayedCount;
    private volatile OverlayState _overlaySnapshot;
    private volatile bool _exiting;
    private DateTime _lastSkipCommandUtc;
    private DateTime _lastSkipNoticeUtc;
    private DateTime _lastRequestDeniedNoticeUtc;
    private DateTime _lastRemoveCommandUtc;
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

    private enum OwnKind { Queue, DefaultPlaylist, Bilibili }

    /// <summary>应用自己播放过的一首，用于「上一首」真正回退（URL 过期时按需重解析）。</summary>
    private sealed class OwnHistoryEntry
    {
        public OwnKind Kind;
        public ResolvedSong Song;
        public QueueItem QueueItem;
        public PlaylistTrack Track;
        public int TrackIndex = -1;
        public string Bvid = "";
        public bool Started;
    }
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
            "主题色（红点 / 正在播放 / 进度条 / 强调竖条，贴左时在右）",
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

    private void UpdateRingSide()
    {
        // 离屏采集时窗口在屏幕外，按屏幕坐标判方向没有意义；退出离屏时会重算
        if (_settings.CaptureOffscreen || _hud.IsOffscreen) return;

        int side;
        bool left;

        switch (_settings.Anchor)
        {
            case "tl":
                side = 0;
                left = true;
                break;
            case "tr":
                side = 0;
                left = false;
                break;
            case "bl":
                side = 1;
                left = true;
                break;
            case "br":
            case "center":
                side = 1;
                left = false;
                break;
            case "left":
                side = 2;
                left = true;
                break;
            case "right":
                side = 3;
                left = false;
                break;
            default:
                var bounds = _hud.Bounds;
                var area = Screen.FromRectangle(bounds).WorkingArea;
                side = NearestRingSide(bounds, area);
                left = bounds.Left + bounds.Width / 2f < area.Left + area.Width / 2f;
                break;
        }

        _hud.SetRingSide(side);
        _hud.SetClassicLeft(left);
    }

    private static int NearestRingSide(Rectangle bounds, Rectangle area)
    {
        var top = bounds.Top - area.Top;
        var bottom = area.Bottom - bounds.Bottom;
        var left = bounds.Left - area.Left;
        var right = area.Right - bounds.Right;
        var min = Math.Min(Math.Min(top, bottom), Math.Min(left, right));

        if (min == top) return 0;
        if (min == bottom) return 1;
        if (min == left) return 2;
        return 3;
    }

    private void MoveHudTo(string anchor, bool fromUser = true)
    {
        if (!_hud.IsHandleCreated) return;
        if (_reanchoring) return;

        // 尺寸变化（标题换行 / 下一首行出现）也会调用这里；不能因此退出离屏或遮挡
        if (!fromUser && (_settings.CaptureOffscreen || _settings.CaptureOccluded)) return;

        if (fromUser && _settings.CaptureOffscreen)
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
            UpdateRingSide();
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
                case "left": x = area.Left + margin; y = area.Top + (area.Height - h) / 2; break;
                case "right": x = area.Right - w - margin; y = area.Top + (area.Height - h) / 2; break;
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
        if (!_settings.VideoRequestEnabled)
        {
            _tray.ShowBalloonTip(2600, "Music Toppin", "视频点播已关闭，可在托盘「播放控制 → 视频点播」里开启。", ToolTipIcon.Warning);
            return;
        }

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
        var generation = 0;
        try
        {
            // 单播放：手动点播前先停掉队列/默认歌单/视频里正在放的那首
            if (HasOwnPlayback) StopOwnPlayback();
            _manualPlaybackPending = true;
            _forwardOwn.Clear();
            _ringReverseOnce = false;
            generation = ++_playGeneration;

            _tray.ShowBalloonTip(1500, "Music Toppin", "正在解析 " + bvid + " …", ToolTipIcon.Info);

            var song = await Task.Run(() => Bilibili.Resolve(bvid));
            if (generation != _playGeneration) return;
            if (song == null)
            {
                _tray.ShowBalloonTip(3000, "Music Toppin", "解析失败：视频不存在、需要登录或当前网络取不到直链", ToolTipIcon.Warning);
                return;
            }

            _activeSong = song;
            _videoBvid = bvid;
            VideoCache.Prefetch(bvid);
            _currentOwnEntry = new OwnHistoryEntry
            {
                Kind = OwnKind.Bilibili,
                Song = song,
                Bvid = bvid
            };

            if (_settings.VideoAudio == 1)
            {
                if (_currentOwnEntry != null) _currentOwnEntry.Started = true;
                _ownPlayedCount++;
                StartVideoPlayback(song);
                return;
            }

            await EnsurePlayer().PlayAsync(song);
            if (generation != _playGeneration) return;
            if (_currentOwnEntry != null) _currentOwnEntry.Started = true;
            _ownPlayedCount++;
            PushOwnState(song, new PlaybackState { Playing = true, Duration = song.Duration });
        }
        catch (Exception ex)
        {
            Logger.Write("play bv: " + ex.Message);
            _tray.ShowBalloonTip(4000, "Music Toppin", ex.Message, ToolTipIcon.Warning);
            if (generation == _playGeneration) StopOwnPlayback();
        }
        finally
        {
            if (generation == _playGeneration) _manualPlaybackPending = false;
        }
    }

    private void OnOwnPlaybackState(PlaybackState state)
    {
        var song = _player == null ? null : _player.Current;

        if (state.Ended)
        {
            // 只处理「当前这首」的结束，避免重复 Ended 把下一首也切掉
            if (song == null || song != _activeSong) return;

            // 一首放完：点歌队列优先，空了再回默认歌单；切歌间隙保留上一份显示，避免 OBS 闪白
            StopOwnPlayback(keepDisplayed: true);
            ContinuePlayback();
            return;
        }

        if (song == null) return;
        PushOwnState(song, state);
    }

    private void PushOwnState(ResolvedSong song, PlaybackState state)
    {
        if (_service == null || song == null) return;

        // 本应用播放时也把会话历史喂给 HUD/环状，让「上一首」显示真实曲目
        var prevTitles = new string[NowPlaying.HistorySlots];
        var prevArtists = new string[NowPlaying.HistorySlots];
        var prevCount = 0;
        for (var i = _ownHistory.Count - 1; i >= 0 && prevCount < NowPlaying.HistorySlots; i--)
        {
            var past = _ownHistory[i];
            if (past == null || past.Song == null || string.IsNullOrWhiteSpace(past.Song.Title)) continue;
            prevTitles[prevCount] = past.Song.Title;
            prevArtists[prevCount] = past.Song.Artist ?? "";
            prevCount++;
        }

        var np = new NowPlaying(
            true, song.Title, song.Artist, state.Position,
            state.Duration > TimeSpan.Zero ? state.Duration : song.Duration,
            state.Playing, true,
            prevTitles, prevArtists, prevCount, _ownPlayedCount);
        np.RingReverse = _ringReverseOnce;
        np.Requester = _currentOwnEntry != null && _currentOwnEntry.QueueItem != null
            ? _currentOwnEntry.QueueItem.Requester
            : "";

        // 侧卡「下一首」优先显示前进栈里的歌（回退后点下一首会回到它）
        var forward = _forwardOwn.Count > 0 ? _forwardOwn[_forwardOwn.Count - 1] : null;
        if (forward != null && forward.Song != null)
        {
            np.NextTitle = forward.Song.Title;
            np.NextArtist = forward.Song.Artist;
            np.QueueCount = 1;
        }
        else
        {
            // 点歌队列优先显示；没有点歌时显示默认歌单的下一首
            var next = _queue.PeekNext();
            if (next != null)
            {
                np.NextTitle = next.DisplayName;
                np.NextArtist = next.Artist;
                np.QueueCount = _queue.Count;
            }
            else if (_settings.DefaultPlaylistEnabled && _defaultPlaylistTracks.Count > 0)
            {
                var track = _defaultPlaylistTracks[_defaultPlaylistIndex % _defaultPlaylistTracks.Count];
                np.NextTitle = track.Title;
                np.NextArtist = track.Artist;
                np.QueueCount = 1;
            }
        }

        _service.SetOwnPlayback(np);
        RefreshOverlaySnapshot();
        UpdateLyrics(song);
    }

    private void UpdateLyrics(ResolvedSong song)
    {
        if (!_settings.LyricsOverlayEnabled || song == null)
        {
            LyricsService.Clear();
            return;
        }

        LyricsService.SetSong(song.Title, song.Artist, song.SourceLabel, song.PageUrl);
    }

    private void StopOwnPlayback(bool keepDisplayed = false)
    {
        if (!_suppressOwnHistory) RememberOwnEntry(_currentOwnEntry);
        _currentOwnEntry = null;
        _ringReverseOnce = false;

        _playingFromQueue = false;
        _playingDefaultPlaylist = false;
        _manualPlaybackPending = false;
        _playGeneration++;
        _activeSong = null;
        _videoBvid = "";
        StopVideoTimer();
        _queue.ClearCurrent();
        try { if (_player != null) _ = _player.StopAsync(); } catch { }
        LyricsService.Clear();
        if (!keepDisplayed && _service != null) _service.SetOwnPlayback(null);
        UpdateQueueMenu();
        UpdateDefaultPlaylistMenu();
    }

    private void RememberOwnEntry(OwnHistoryEntry entry)
    {
        if (entry == null || entry.Song == null || !entry.Started) return;
        if (_ownHistory.Count > 0 && ReferenceEquals(_ownHistory[_ownHistory.Count - 1], entry)) return;

        _ownHistory.Add(entry);
        while (_ownHistory.Count > 12) _ownHistory.RemoveAt(0);
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

    // ---- 默认歌单 ----

    private ToolStripMenuItem BuildDefaultPlaylistMenu()
    {
        var menu = new ToolStripMenuItem("默认歌单");

        _defaultPlaylistStatus = new ToolStripMenuItem("默认歌单：未设置") { Enabled = false };
        _defaultPlaylistUseNetease = new ToolStripMenuItem("使用网易云歌单", null, (_, _) => SetDefaultPlaylistSource(PlaylistSource.Netease)) { Tag = 1 };
        _defaultPlaylistUseQQ = new ToolStripMenuItem("使用 QQ 音乐歌单", null, (_, _) => SetDefaultPlaylistSource(PlaylistSource.QQMusic)) { Tag = 2 };
        _defaultPlaylistToggle = new ToolStripMenuItem("暂停默认歌单", null, (_, _) => ToggleDefaultPlaylist());
        _defaultPlaylistNext = new ToolStripMenuItem("下一首（默认歌单）", null, (_, _) => SkipDefaultPlaylist());
        _defaultPlaylistClear = new ToolStripMenuItem("清除默认歌单", null, (_, _) => ClearDefaultPlaylist());

        menu.DropDownItems.Add(_defaultPlaylistStatus);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("设置网易云歌单…", null, (_, _) => PromptDefaultPlaylist(PlaylistSource.Netease)));
        menu.DropDownItems.Add(new ToolStripMenuItem("设置 QQ 音乐歌单…", null, (_, _) => PromptDefaultPlaylist(PlaylistSource.QQMusic)));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(_defaultPlaylistUseNetease);
        menu.DropDownItems.Add(_defaultPlaylistUseQQ);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(_defaultPlaylistToggle);
        menu.DropDownItems.Add(_defaultPlaylistNext);
        menu.DropDownItems.Add(_defaultPlaylistClear);
        menu.DropDownOpening += (_, _) => UpdateDefaultPlaylistMenu();

        return menu;
    }

    private void UpdateDefaultPlaylistMenu()
    {
        if (_defaultPlaylistStatus == null) return;

        var source = CurrentPlaylistSource;
        var id = PlaylistId(source);
        var name = PlaylistName(source);
        var loaded = _defaultPlaylistTracks.Count > 0 &&
                     string.Equals(_defaultPlaylistLoadedKey, PlaylistKey(source, id), StringComparison.Ordinal);
        if (loaded && !string.IsNullOrWhiteSpace(_defaultPlaylistLoadedName)) name = _defaultPlaylistLoadedName;

        if (source == PlaylistSource.None || string.IsNullOrWhiteSpace(id))
        {
            _defaultPlaylistStatus.Text = "默认歌单：未设置";
        }
        else
        {
            var label = source == PlaylistSource.Netease ? "网易云" : "QQ 音乐";
            var text = "默认歌单：" + label + " · " + (string.IsNullOrWhiteSpace(name) ? id : name);
            if (loaded) text += "（" + _defaultPlaylistTracks.Count + " 首）";
            if (!_settings.DefaultPlaylistEnabled) text += " · 已暂停";
            else if (_playingDefaultPlaylist) text += " · 播放中";
            _defaultPlaylistStatus.Text = text;
        }

        _defaultPlaylistUseNetease.Checked = source == PlaylistSource.Netease;
        _defaultPlaylistUseQQ.Checked = source == PlaylistSource.QQMusic;
        _defaultPlaylistToggle.Text = _settings.DefaultPlaylistEnabled ? "暂停默认歌单" : "开始默认歌单";
        _defaultPlaylistToggle.Enabled = source != PlaylistSource.None && !string.IsNullOrWhiteSpace(id);
        _defaultPlaylistNext.Enabled = source != PlaylistSource.None && !string.IsNullOrWhiteSpace(id);
        _defaultPlaylistClear.Enabled = source != PlaylistSource.None || !string.IsNullOrWhiteSpace(_settings.NeteasePlaylistId) ||
                                        !string.IsNullOrWhiteSpace(_settings.QQPlaylistId);
    }

    private PlaylistSource CurrentPlaylistSource
    {
        get
        {
            var value = _settings.DefaultPlaylistSource;
            return value == 1 ? PlaylistSource.Netease : value == 2 ? PlaylistSource.QQMusic : PlaylistSource.None;
        }
    }

    private string PlaylistId(PlaylistSource source)
    {
        return source == PlaylistSource.Netease ? (_settings.NeteasePlaylistId ?? "")
            : source == PlaylistSource.QQMusic ? (_settings.QQPlaylistId ?? "") : "";
    }

    private string PlaylistName(PlaylistSource source)
    {
        return source == PlaylistSource.Netease ? (_settings.NeteasePlaylistName ?? "")
            : source == PlaylistSource.QQMusic ? (_settings.QQPlaylistName ?? "") : "";
    }

    private static string PlaylistKey(PlaylistSource source, string id)
    {
        return (int)source + "|" + (id ?? "");
    }

    private void PromptDefaultPlaylist(PlaylistSource source)
    {
        var label = source == PlaylistSource.Netease ? "网易云" : "QQ 音乐";
        var hint = source == PlaylistSource.Netease
            ? "粘贴网易云歌单链接或 ID（例如 https://music.163.com/playlist?id=123456）"
            : "粘贴 QQ 音乐歌单链接或 ID（例如 https://y.qq.com/n/ryqq/playlist/123456）";
        var initial = PlaylistId(source);

        using (var prompt = new PromptDialog("导入默认歌单 · " + label, hint, initial))
        {
            if (prompt.ShowDialog(_hud) != DialogResult.OK) return;

            var id = ExtractPlaylistId(source, prompt.Value);
            if (string.IsNullOrWhiteSpace(id))
            {
                _tray.ShowBalloonTip(3000, "Music Toppin", "没认出歌单 ID，请粘贴完整歌单链接或数字 ID。", ToolTipIcon.Warning);
                return;
            }

            SetDefaultPlaylist(source, id);
        }
    }

    private void SetDefaultPlaylist(PlaylistSource source, string id)
    {
        if (source == PlaylistSource.Netease)
        {
            _settings.NeteasePlaylistId = id;
            _settings.NeteasePlaylistName = "";
        }
        else if (source == PlaylistSource.QQMusic)
        {
            _settings.QQPlaylistId = id;
            _settings.QQPlaylistName = "";
        }

        _settings.DefaultPlaylistSource = (int)source;
        _settings.DefaultPlaylistEnabled = true;
        _settings.Save();

        ResetDefaultPlaylistCache();
        if (_playingDefaultPlaylist) StopOwnPlayback();
        UpdateDefaultPlaylistMenu();

        if (!_playingFromQueue && (_player == null || _player.Current == null))
            _ = StartDefaultPlaylistAsync();
    }

    private void SetDefaultPlaylistSource(PlaylistSource source)
    {
        if (string.IsNullOrWhiteSpace(PlaylistId(source)))
        {
            PromptDefaultPlaylist(source);
            return;
        }

        if (CurrentPlaylistSource == source)
        {
            if (!_settings.DefaultPlaylistEnabled) ToggleDefaultPlaylist();
            else if (!_playingDefaultPlaylist && !_playingFromQueue && (_player == null || _player.Current == null))
                _ = StartDefaultPlaylistAsync();
            return;
        }

        if (_playingDefaultPlaylist) StopOwnPlayback();
        _settings.DefaultPlaylistSource = (int)source;
        _settings.DefaultPlaylistEnabled = true;
        _settings.Save();
        ResetDefaultPlaylistCache();
        UpdateDefaultPlaylistMenu();

        if (!_playingFromQueue && (_player == null || _player.Current == null))
            _ = StartDefaultPlaylistAsync();
    }

    private void ToggleDefaultPlaylist()
    {
        if (CurrentPlaylistSource == PlaylistSource.None || string.IsNullOrWhiteSpace(PlaylistId(CurrentPlaylistSource))) return;

        _settings.DefaultPlaylistEnabled = !_settings.DefaultPlaylistEnabled;
        _settings.Save();

        if (!_settings.DefaultPlaylistEnabled)
        {
            if (_playingDefaultPlaylist)
            {
                StopOwnPlayback();
                if (_queue.Count > 0) StartNextQueuedSong();
            }
        }
        else if (!_playingFromQueue && (_player == null || _player.Current == null))
        {
            _ = StartDefaultPlaylistAsync();
        }

        UpdateDefaultPlaylistMenu();
    }

    private void SkipDefaultPlaylist()
    {
        if (CurrentPlaylistSource == PlaylistSource.None) return;

        if (_playingDefaultPlaylist)
        {
            StopOwnPlayback();
            _ = StartNextDefaultSongAsync();
            return;
        }

        if (!_playingFromQueue && (_player == null || _player.Current == null))
            _ = StartDefaultPlaylistAsync();
    }

    private void ClearDefaultPlaylist()
    {
        _settings.DefaultPlaylistSource = 0;
        _settings.DefaultPlaylistEnabled = false;
        _settings.NeteasePlaylistId = "";
        _settings.NeteasePlaylistName = "";
        _settings.QQPlaylistId = "";
        _settings.QQPlaylistName = "";
        _settings.Save();

        ResetDefaultPlaylistCache();
        if (_playingDefaultPlaylist)
        {
            StopOwnPlayback();
            if (_queue.Count > 0) StartNextQueuedSong();
        }
        UpdateDefaultPlaylistMenu();
    }

    private static string ExtractPlaylistId(PlaylistSource source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var value = text.Trim();
        var patterns = source == PlaylistSource.Netease
            ? new[] { @"[?&#]id=(\d+)", @"playlist/(\d+)", @"^\s*(\d{3,})\s*$" }
            : new[] { @"disstid=(\d+)", @"playlist/(\d+)", @"[?&#]id=(\d+)", @"^\s*(\d{3,})\s*$" };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }

        var digits = Regex.Match(value, @"\d{3,}");
        return digits.Success ? digits.Value : "";
    }

    private void ResetDefaultPlaylistCache()
    {
        _defaultPlaylistTracks.Clear();
        _defaultPlaylistIndex = 0;
        _defaultPlaylistLoadedKey = "";
        _defaultPlaylistLoadedName = "";
        _defaultPlaylistLoadTask = null;
        _defaultPlaylistFailedKey = "";
        _defaultPlaylistPauseUntil = DateTime.MinValue;
        _defaultPlaylistFailStreak = 0;
        _forwardOwn.Clear();
    }

    private async Task<bool> EnsureDefaultPlaylistAsync()
    {
        var source = CurrentPlaylistSource;
        var id = PlaylistId(source);
        if (source == PlaylistSource.None || string.IsNullOrWhiteSpace(id)) return false;

        var key = PlaylistKey(source, id);
        if (string.Equals(_defaultPlaylistLoadedKey, key, StringComparison.Ordinal) && _defaultPlaylistTracks.Count > 0)
            return true;

        if (string.Equals(_defaultPlaylistFailedKey, key, StringComparison.Ordinal) &&
            (DateTime.UtcNow - _defaultPlaylistFailedUtc).TotalSeconds < 60)
        {
            return false;
        }

        if (_defaultPlaylistLoadTask != null && !_defaultPlaylistLoadTask.IsCompleted)
            return await _defaultPlaylistLoadTask;

        _defaultPlaylistLoadTask = LoadDefaultPlaylistCoreAsync(source, id, key);
        try
        {
            return await _defaultPlaylistLoadTask;
        }
        finally
        {
            _defaultPlaylistLoadTask = null;
        }
    }

    private async Task<bool> LoadDefaultPlaylistCoreAsync(PlaylistSource source, string id, string key)
    {
        PlaylistData data = null;
        try
        {
            data = await Task.Run(() => DefaultPlaylist.Load(source, id));
        }
        catch (Exception ex)
        {
            Logger.Write("default playlist load: " + ex.Message);
        }

        if (data == null || data.Tracks.Count == 0)
        {
            _defaultPlaylistFailedKey = key;
            _defaultPlaylistFailedUtc = DateTime.UtcNow;
            Logger.Write("default playlist load failed: " + source + " " + id);
            _lastRequestResult = "默认歌单读取失败（检查歌单 ID、可见性或登录状态）";
            LiveNotify("Music Toppin", _lastRequestResult, ToolTipIcon.Warning);
            ShowOverlayToast("默认歌单读取失败");
            return false;
        }

        if (!string.Equals(PlaylistKey(CurrentPlaylistSource, PlaylistId(CurrentPlaylistSource)), key, StringComparison.Ordinal))
            return false;

        _defaultPlaylistTracks.Clear();
        _defaultPlaylistTracks.AddRange(data.Tracks);
        _defaultPlaylistIndex = 0;
        _defaultPlaylistLoadedKey = key;
        _defaultPlaylistLoadedName = string.IsNullOrWhiteSpace(data.Name) ? id : data.Name;
        _defaultPlaylistFailedKey = "";
        _defaultPlaylistFailStreak = 0;

        if (source == PlaylistSource.Netease) _settings.NeteasePlaylistName = _defaultPlaylistLoadedName;
        else if (source == PlaylistSource.QQMusic) _settings.QQPlaylistName = _defaultPlaylistLoadedName;
        _settings.Save();

        Logger.Write("default playlist loaded: " + source + " " + _defaultPlaylistLoadedName + " (" + data.Tracks.Count + " tracks)");
        UpdateDefaultPlaylistMenu();
        return true;
    }

    private async Task StartDefaultPlaylistAsync()
    {
        if (_demo || !_settings.DefaultPlaylistEnabled) return;
        if (DateTime.UtcNow < _defaultPlaylistPauseUntil) return;
        if (HasOwnPlayback) return;

        if (_queue.Count > 0)
        {
            StartNextQueuedSong();
            return;
        }

        if (!await EnsureDefaultPlaylistAsync()) return;
        if (_queue.Count > 0 || HasOwnPlayback) return;

        await StartNextDefaultSongAsync();
    }

    private async Task StartNextDefaultSongAsync()
    {
        if (!_settings.DefaultPlaylistEnabled || _defaultPlaylistTracks.Count == 0) return;
        _forwardOwn.Clear();
        _ringReverseOnce = false;

        if (_queue.Count > 0)
        {
            StartNextQueuedSong();
            return;
        }

        var count = _defaultPlaylistTracks.Count;
        var maxAttempts = Math.Min(count, 20);
        var generation = ++_playGeneration;
        _playingDefaultPlaylist = true;
        _playingFromQueue = false;
        _videoBvid = "";
        StopVideoTimer();

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (generation != _playGeneration) return;

            var index = _defaultPlaylistIndex % count;
            _defaultPlaylistIndex = (index + 1) % count;
            var track = _defaultPlaylistTracks[index];

            ResolvedSong song = null;
            try
            {
                song = await Task.Run(() => DefaultPlaylist.Resolve(track));
            }
            catch (Exception ex)
            {
                Logger.Write("default playlist resolve: " + ex.Message);
            }

            if (generation != _playGeneration) return;
            if (song == null)
            {
                Logger.Write("default playlist skip: " + track.DisplayName);
                continue;
            }

            _activeSong = song;
            _currentOwnEntry = new OwnHistoryEntry
            {
                Kind = OwnKind.DefaultPlaylist,
                Song = song,
                Track = track,
                TrackIndex = index
            };
            try
            {
                await EnsurePlayer().PlayAsync(song);
                if (generation != _playGeneration) return;
                if (_currentOwnEntry != null) _currentOwnEntry.Started = true;
                _ownPlayedCount++;
                PushOwnState(song, new PlaybackState { Playing = true, Duration = song.Duration });
                Logger.Write("default playlist play: " + song.Title +
                             (string.IsNullOrEmpty(song.Artist) ? "" : " - " + song.Artist));
                _defaultPlaylistFailStreak = 0;
                return;
            }
            catch (Exception ex)
            {
                Logger.Write("default playlist play failed: " + ex.Message);
                _defaultPlaylistFailStreak++;
                _activeSong = null;
            }
        }

        if (generation != _playGeneration) return;

        _playingDefaultPlaylist = false;
        _activeSong = null;
        if (_service != null) _service.SetOwnPlayback(null);
        _lastRequestResult = "默认歌单暂时放不了（可能需要登录或没有可播曲目）";
        LiveNotify("Music Toppin", _lastRequestResult, ToolTipIcon.Warning);
        ShowOverlayToast("默认歌单暂时放不了");
        Logger.Write("default playlist: no playable track");

        if (_queue.Count > 0) StartNextQueuedSong();
    }

    private void ContinuePlayback()
    {
        if (_queue.Count > 0)
        {
            StartNextQueuedSong();
            return;
        }

        if (_settings.DefaultPlaylistEnabled && CurrentPlaylistSource != PlaylistSource.None &&
            !string.IsNullOrWhiteSpace(PlaylistId(CurrentPlaylistSource)))
        {
            _ = StartDefaultPlaylistAsync();
            return;
        }

        // 没有后续播放源：清掉切歌间隙保留的上一首显示
        if (_service != null) _service.SetOwnPlayback(null);
    }

    private void StopAllPlayback()
    {
        // 只停止当前曲目，不修改「默认歌单已启用」这个配置；
        // 要关闭自动续播请用「默认歌单 → 暂停」。
        _forwardOwn.Clear();
        StopOwnPlayback();
        UpdateDefaultPlaylistMenu();
    }

    // ---- 弹幕点歌 / 切歌权限 ----

    private ToolStripMenuItem BuildRequestPolicyMenu()
    {
        var menu = new ToolStripMenuItem("点歌权限");

        var everyone = new ToolStripMenuItem("所有人（普通观众也能点歌）", null, (_, _) => SetRequestPolicy(DanmakuPermissions.RequestAll)) { Tag = DanmakuPermissions.RequestAll };
        var fans = new ToolStripMenuItem("粉丝团灯牌 或 舰长（推荐）", null, (_, _) => SetRequestPolicy(DanmakuPermissions.RequestFansOrGuard)) { Tag = DanmakuPermissions.RequestFansOrGuard };
        var guard = new ToolStripMenuItem("仅舰长 / 大航海", null, (_, _) => SetRequestPolicy(DanmakuPermissions.RequestGuardOnly)) { Tag = DanmakuPermissions.RequestGuardOnly };
        var off = new ToolStripMenuItem("关闭点歌", null, (_, _) => SetRequestPolicy(DanmakuPermissions.RequestOff)) { Tag = DanmakuPermissions.RequestOff };

        menu.DropDownItems.Add(everyone);
        menu.DropDownItems.Add(fans);
        menu.DropDownItems.Add(guard);
        menu.DropDownItems.Add(off);

        menu.DropDownOpening += (_, _) =>
        {
            everyone.Checked = _settings.RequestPolicy == DanmakuPermissions.RequestAll;
            fans.Checked = _settings.RequestPolicy == DanmakuPermissions.RequestFansOrGuard;
            guard.Checked = _settings.RequestPolicy == DanmakuPermissions.RequestGuardOnly;
            off.Checked = _settings.RequestPolicy == DanmakuPermissions.RequestOff;

            menu.Text = "点歌权限：" + DescribeRequestPolicy(_settings.RequestPolicy);
        };

        return menu;
    }

    private void SetRequestPolicy(int policy)
    {
        _settings.RequestPolicy = policy < 0 || policy > 3 ? DanmakuPermissions.RequestFansOrGuard : policy;
        _settings.Save();
        Logger.Write("request policy: " + _settings.RequestPolicy);
        UpdatePermissionMenuText();
    }

    private void UpdatePermissionMenuText()
    {
        if (_permissionMenu == null) return;
        _permissionMenu.Text = "弹幕权限：点歌 " + DescribeRequestPolicy(_settings.RequestPolicy) +
                               " / 切歌 " + DescribeSkipPolicy(_settings.SkipPolicy);
    }

    private static string DescribeRequestPolicy(int policy)
    {
        switch (policy)
        {
            case DanmakuPermissions.RequestFansOrGuard: return "粉丝团 / 舰长";
            case DanmakuPermissions.RequestGuardOnly: return "仅舰长";
            case DanmakuPermissions.RequestOff: return "关闭";
            default: return "所有人";
        }
    }

    private ToolStripMenuItem BuildSkipPolicyMenu()
    {
        var menu = new ToolStripMenuItem("弹幕切歌");

        var everyone = new ToolStripMenuItem("所有人（普通观众可切任意歌）", null, (_, _) => SetSkipPolicy(DanmakuPermissions.SkipAll)) { Tag = DanmakuPermissions.SkipAll };
        var requester = new ToolStripMenuItem("仅点歌人（房管可切全部，推荐）", null, (_, _) => SetSkipPolicy(DanmakuPermissions.SkipRequester)) { Tag = DanmakuPermissions.SkipRequester };
        var admin = new ToolStripMenuItem("仅房管", null, (_, _) => SetSkipPolicy(DanmakuPermissions.SkipAdmin)) { Tag = DanmakuPermissions.SkipAdmin };
        var off = new ToolStripMenuItem("关闭", null, (_, _) => SetSkipPolicy(DanmakuPermissions.SkipOff)) { Tag = DanmakuPermissions.SkipOff };

        menu.DropDownItems.Add(everyone);
        menu.DropDownItems.Add(requester);
        menu.DropDownItems.Add(admin);
        menu.DropDownItems.Add(off);

        menu.DropDownOpening += (_, _) =>
        {
            everyone.Checked = _settings.SkipPolicy == DanmakuPermissions.SkipAll;
            requester.Checked = _settings.SkipPolicy == DanmakuPermissions.SkipRequester;
            admin.Checked = _settings.SkipPolicy == DanmakuPermissions.SkipAdmin;
            off.Checked = _settings.SkipPolicy == DanmakuPermissions.SkipOff;

            menu.Text = "弹幕切歌：" + DescribeSkipPolicy(_settings.SkipPolicy);
        };

        return menu;
    }

    private void SetSkipPolicy(int policy)
    {
        _settings.SkipPolicy = policy < 0 || policy > 3 ? DanmakuPermissions.SkipRequester : policy;
        _settings.Save();
        Logger.Write("skip policy: " + _settings.SkipPolicy);
        UpdatePermissionMenuText();
    }

    private static string DescribeSkipPolicy(int policy)
    {
        switch (policy)
        {
            case DanmakuPermissions.SkipAll: return "所有人";
            case DanmakuPermissions.SkipAdmin: return "仅房管";
            case DanmakuPermissions.SkipOff: return "关闭";
            default: return "仅点歌人（房管可切全部）";
        }
    }

    private ToolStripMenuItem BuildRemovePolicyMenu()
    {
        var menu = new ToolStripMenuItem("弹幕撤回");

        var everyone = new ToolStripMenuItem("所有人（可按序号删）", null, (_, _) => SetRemovePolicy(0)) { Tag = 0 };
        var owner = new ToolStripMenuItem("仅本人（房管可撤全部，推荐）", null, (_, _) => SetRemovePolicy(1)) { Tag = 1 };
        var off = new ToolStripMenuItem("关闭", null, (_, _) => SetRemovePolicy(2)) { Tag = 2 };

        menu.DropDownItems.Add(everyone);
        menu.DropDownItems.Add(owner);
        menu.DropDownItems.Add(off);

        menu.DropDownOpening += (_, _) =>
        {
            everyone.Checked = _settings.RemovePolicy == 0;
            owner.Checked = _settings.RemovePolicy == 1;
            off.Checked = _settings.RemovePolicy == 2;

            menu.Text = "弹幕撤回：" + (_settings.RemovePolicy == 0 ? "所有人"
                : _settings.RemovePolicy == 1 ? "仅本人" : "关闭");
        };

        return menu;
    }

    private void SetRemovePolicy(int policy)
    {
        _settings.RemovePolicy = policy < 0 || policy > 2 ? 0 : policy;
        _settings.Save();
        Logger.Write("remove policy: " + _settings.RemovePolicy);
    }

    private ToolStripMenuItem BuildSessionRefreshMenu()
    {
        var menu = new ToolStripMenuItem("会话刷新");

        foreach (var seconds in new[] { 60, 120, 300, 0 })
        {
            var value = seconds;
            menu.DropDownItems.Add(new ToolStripMenuItem(
                seconds == 0 ? "关闭（不主动重连）" : seconds + " 秒",
                null,
                (_, _) => SetSessionRefresh(value)) { Tag = seconds });
        }

        menu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripItem item in menu.DropDownItems)
            {
                var menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.Tag is int) menuItem.Checked = (int)menuItem.Tag == _settings.SessionRefreshSeconds;
            }

            menu.Text = "会话刷新：" + (_settings.SessionRefreshSeconds <= 0 ? "关闭" : _settings.SessionRefreshSeconds + " 秒");
        };

        return menu;
    }

    private void SetSessionRefresh(int seconds)
    {
        _settings.SessionRefreshSeconds = Math.Max(0, seconds);
        _settings.Save();

        if (_danmaku != null) _danmaku.SessionRefreshSeconds = _settings.SessionRefreshSeconds;
        Logger.Write("danmaku session refresh: " + _settings.SessionRefreshSeconds + "s");
    }

    // ---- 视频声音模式 ----

    /// <summary>视频自带声音：不在应用内播音频，用计时器推进 HUD 进度并在时长到点后切下一首。</summary>
    private void StartVideoPlayback(ResolvedSong song)
    {
        _videoSong = song;
        _activeSong = song;
        _videoNonce++;
        _videoStartedUtc = DateTime.UtcNow;
        _videoDuration = song.Duration > TimeSpan.Zero ? song.Duration : TimeSpan.FromMinutes(10);

        if (song.Duration <= TimeSpan.Zero)
        {
            Logger.Write("video: 没有时长信息，按 10 分钟兜底（可手动切歌）");
        }

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
            StopOwnPlayback(keepDisplayed: true);
            ContinuePlayback();
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

        var app = new ToolStripMenuItem("走应用播放器（默认，确保有声）", null, (_, _) => SetVideoAudio(0)) { Tag = 0 };
        var follow = new ToolStripMenuItem("跟随视频（OBS 允许带声播放时可用）", null, (_, _) => SetVideoAudio(1)) { Tag = 1 };

        menu.DropDownItems.Add(app);
        menu.DropDownItems.Add(follow);
        menu.DropDownOpening += (_, _) =>
        {
            follow.Checked = _settings.VideoAudio == 1;
            app.Checked = _settings.VideoAudio == 0;
            menu.Text = _settings.VideoAudio == 1 ? "视频声音：跟随视频" : "视频声音：应用播放器";
        };

        return menu;
    }

    private void ToggleVideoRequest()
    {
        _settings.VideoRequestEnabled = !_settings.VideoRequestEnabled;
        _itemVideoRequest.Checked = _settings.VideoRequestEnabled;
        _settings.Save();
        Logger.Write("video request: " + (_settings.VideoRequestEnabled ? "on" : "off"));

        _tray.ShowBalloonTip(2200, "Music Toppin",
            _settings.VideoRequestEnabled ? "已开启视频点播" : "已关闭视频点播（BV 点歌会被拒绝）",
            ToolTipIcon.Info);
    }

    private void SetVideoAudio(int mode)
    {
        _settings.VideoAudio = mode == 0 ? 0 : 1;
        _settings.Save();
        Logger.Write("video audio mode: " + (_settings.VideoAudio == 1 ? "follow video" : "app player"));

        // 正在播的这一首保持不变（避免丢歌），下一首按新模式走
    }

    private void OnPlayerFailed(string message)
    {
        Logger.Write("player failed: " + message);

        // 失败曲目不再写回「上一首」历史，避免反复重试同一首坏链
        if (_currentOwnEntry != null) _currentOwnEntry.Started = false;

        // 直播中点歌是自动流程，默认不弹气泡（避免右下角通知干扰直播/游戏输入）
        if (_playingFromQueue || _playingDefaultPlaylist) LiveNotify("Music Toppin", message, ToolTipIcon.Warning);
        else _tray.ShowBalloonTip(4000, "Music Toppin", message, ToolTipIcon.Warning);

        // 默认歌单连续失败时暂停一段时间，避免直链 403/版权问题无限重试
        var keepDisplayed = true;
        if (_playingDefaultPlaylist)
        {
            _defaultPlaylistFailStreak++;
            if (_defaultPlaylistFailStreak >= 3)
            {
                _defaultPlaylistFailStreak = 0;
                _defaultPlaylistPauseUntil = DateTime.UtcNow.AddMinutes(5);
                keepDisplayed = false;
                LiveNotify("Music Toppin", "默认歌单连续 3 首失败，已暂停 5 分钟；请检查歌单/登录状态。",
                    ToolTipIcon.Warning);
                ShowOverlayToast("默认歌单已暂停（连续失败）");
            }
        }
        else
        {
            _defaultPlaylistFailStreak = 0;
        }

        // 失败也按同一套顺序继续：点歌队列优先，空了回默认歌单
        StopOwnPlayback(keepDisplayed: keepDisplayed);
        ContinuePlayback();
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
        if (_exiting || string.IsNullOrWhiteSpace(text)) return;

        _toastText = text;
        _toastId++;
        _toastUntilUtc = DateTime.UtcNow.AddSeconds(6);
        Logger.Write("toast: " + text);
        RefreshOverlaySnapshot();
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

    private static long ParseUid(string text)
    {
        long uid;
        return long.TryParse(text, out uid) ? uid : 0;
    }

    /// <summary>B 站登录：部分直播间只给登录用户下发弹幕（游客只收到人气包）。</summary>
    private void LoginBilibili()
    {
        using (var form = new WebLoginForm(
            "登录 B 站（直播间弹幕用）",
            "https://passport.bilibili.com/login",
            "https://www.bilibili.com",
            cookies => cookies.ContainsKey("SESSDATA") && !string.IsNullOrWhiteSpace(cookies["SESSDATA"]),
            "扫码或用账号登录；登录后弹幕连接会带账号（部分房间只给登录用户下发弹幕）"))
        {
            if (form.ShowDialog(_hud) != DialogResult.OK) return;

            var cookie = form.CookieHeader;
            var uid = FetchBilibiliUid(cookie);

            _settings.BilibiliCookie = cookie;
            _settings.BilibiliUid = uid.ToString();
            _settings.Save();

            BilibiliLive.Cookie = cookie;
            if (_danmaku != null) _danmaku.Uid = uid;

            Logger.Write("bilibili login: uid=" + uid);
            if (_settings.LiveRoomId > 0) ConnectLive(_settings.LiveRoomId, remember: false);
        }
    }

    private static long FetchBilibiliUid(string cookie)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("https://api.bilibili.com/x/web-interface/nav");
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            if (!string.IsNullOrEmpty(cookie)) request.Headers["Cookie"] = cookie;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var root = Json.Parse(reader.ReadToEnd());
                var data = root != null ? root["data"] : null;
                if (data == null) return 0;

                // nav 接口返回的是 mid，不是 uid
                if (data["mid"] != null) return data["mid"].AsLong;
                return data["uid"] != null ? data["uid"].AsLong : 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Write("bilibili uid: " + ex.Message);
            return 0;
        }
    }

    private void LogoutBilibili()
    {
        _settings.BilibiliCookie = "";
        _settings.BilibiliUid = "";
        _settings.Save();

        BilibiliLive.Cookie = "";
        if (_danmaku != null) _danmaku.Uid = 0;
        if (_settings.LiveRoomId > 0) ConnectLive(_settings.LiveRoomId, remember: false);
    }

    private void RefreshSourceStatus()
    {
        if (_neteaseStatus != null)        {
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
            _danmaku.Uid = _noBiliLogin ? 0 : ParseUid(_settings.BilibiliUid);
            _danmaku.SessionRefreshSeconds = _settings.SessionRefreshSeconds;
            var protoArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--protover=", StringComparison.OrdinalIgnoreCase));
            int proto;
            if (protoArg != null && int.TryParse(protoArg.Substring("--protover=".Length), out proto)) _danmaku.ProtoVersion = proto;
            _danmaku.Received += OnDanmaku;
            _danmaku.ConnectionChanged += connected => _ui.Post(_ => OnLiveConnectionChanged(connected), null);
            _danmaku.StatusChanged += status =>
            {
                Logger.Write("danmaku: " + status);
                _ui.Post(_ => UpdateLiveStatus(status), null);
            };
        }
        else
        {
            // 上次秒断可能把会话降级成游客；用户手动重连时从设置恢复登录态
            _danmaku.Uid = _noBiliLogin ? 0 : ParseUid(_settings.BilibiliUid);
            if (!_noBiliLogin) BilibiliLive.Cookie = _settings.BilibiliCookie ?? "";
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
        }, () => HandleDanmaku(new DanmakuMessage { Text = "点歌 起风了", UserName = "本地测试", Uid = 1, IsLocalTest = true })))
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
            LastPacketUtc = client != null ? client.LastPacketUtc : DateTime.MinValue,
            PacketCount = client != null ? client.PacketCount : 0,
            LastError = client != null ? client.LastError : "",
            LastRequestResult = _lastRequestResult,
            RecentVersion = _recentDanmakuVersion,
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
        if (_exiting || _tray == null) return;

        var text = "Music Toppin";
        if (_danmaku != null && _danmaku.IsConnected) text += " · 直播间 " + _danmaku.RoomId;
        if (_queue.Count > 0) text += " · 点歌 " + _queue.Count;

        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private void OnDanmaku(DanmakuMessage message)
    {
        if (_exiting || message == null) return;

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

        if (TryHandleSkipCommand(message))
        {
            RefreshLiveStatus();
            return;
        }

        if (TryHandleRemoveCommand(message))
        {
            RefreshLiveStatus();
            return;
        }

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

                var hint = text.Length > 24 ? text.Substring(0, 24) + "…" : text;
                ShowOverlayToast("没听懂这条点歌：" + hint + "（格式：点歌 + 空格 + 歌名）");
            }
            return;
        }

        // 点歌权限：所有人 / 粉丝团灯牌或舰长 / 仅舰长 / 关闭
        if (!CanUserRequestSong(message)) return;

        if (request.IsVideo && !_settings.VideoRequestEnabled)
        {
            _lastRequestResult = "视频点播已关闭";
            Logger.Write("song request denied: video off | " + message.UserName + ": " + message.Text);
            ShowOverlayToast("主播已关闭视频点播");
            return;
        }

        var result = _queue.Enqueue(request, ignoreLimits: message.IsLocalTest);
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
        _recentDanmakuVersion++;
    }

    // ---- 弹幕切歌 ----

    private static readonly HashSet<string> SkipCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "切歌", "切歌吧", "换歌", "下一首", "跳过", "跳过这首", "跳过这歌", "切了"
    };

    /// <summary>点歌权限：所有人 / 粉丝团灯牌或舰长 / 仅舰长 / 关闭。</summary>
    private bool CanUserRequestSong(DanmakuMessage message)
    {
        if (message.IsLocalTest) return true;

        var policy = _settings.RequestPolicy;
        if (policy == DanmakuPermissions.RequestAll) return true;

        var hasThisRoomMedal = message.HasMedal && IsThisRoomMedal(message);
        if (DanmakuPermissions.CanRequest(policy, hasThisRoomMedal, message.IsGuard)) return true;

        var reason = DanmakuPermissions.RequestDeniedMessage(policy);
        if (policy == DanmakuPermissions.RequestFansOrGuard && message.HasMedal && !hasThisRoomMedal)
        {
            reason = "点歌需要本直播间的粉丝团灯牌（你挂的是其他直播间的灯牌）";
        }

        _lastRequestResult = "被拒绝（" + reason + "）：" + message.UserName;
        Logger.Write("song request denied: " + reason + " | " + message.UserName + " uid=" + message.Uid +
                     " medal=" + (message.HasMedal ? message.MedalLevel + "/" + message.MedalName + "@" + message.MedalRoomId : "none") +
                     " guard=" + message.GuardLevel + " admin=" + message.IsAdmin);
        LiveNotify("点歌被拒绝", reason + "：" + message.UserName, ToolTipIcon.Info);

        if ((DateTime.UtcNow - _lastRequestDeniedNoticeUtc).TotalSeconds >= 3)
        {
            _lastRequestDeniedNoticeUtc = DateTime.UtcNow;
            ShowOverlayToast(reason + "：" + message.UserName);
        }

        RefreshLiveStatus();
        return false;
    }

    /// <summary>粉丝牌是不是本直播间的（没有牌子房间号时按兼容处理，不误杀）。</summary>
    private bool IsThisRoomMedal(DanmakuMessage message)
    {
        var room = _danmaku != null ? _danmaku.RoomId : 0;
        var shortId = _danmaku != null ? _danmaku.ShortId : 0;
        if (message.MedalRoomId <= 0 || room <= 0) return true;
        return message.MedalRoomId == room || (shortId > 0 && message.MedalRoomId == shortId);
    }

    /// <summary>观众发「切歌」切掉当前这首；返回 true 表示这条弹幕已被当作切歌处理。</summary>
    private bool TryHandleSkipCommand(DanmakuMessage message)
    {
        var text = (message.Text ?? "").Trim().TrimEnd('。', '!', '！', '~', '～', ' ');
        if (!SkipCommands.Contains(text)) return false;

        var policy = _settings.SkipPolicy;

        if (policy == DanmakuPermissions.SkipOff && !message.IsLocalTest)
        {
            Logger.Write("danmaku skip: disabled | " + message.UserName);
            return true;
        }

        var current = _queue.Current;
        var ownerUid = current != null && current.Request != null ? current.Request.RequesterUid : 0;
        var isRequester = ownerUid > 0 && ownerUid == message.Uid;

        // 房管永远可以切全部；普通观众按权限设置判断；本地测试消息不受限制
        if (!message.IsLocalTest && !DanmakuPermissions.CanSkip(policy, message.IsAdmin, isRequester))
        {
            var reason = DanmakuPermissions.SkipDeniedMessage(policy);
            if ((DateTime.UtcNow - _lastSkipNoticeUtc).TotalSeconds >= 3)
            {
                _lastSkipNoticeUtc = DateTime.UtcNow;
                _lastRequestResult = "切歌被拒绝（" + reason + "）：" + message.UserName;
                LiveNotify("切歌被拒绝", reason + "：" + message.UserName, ToolTipIcon.Info);
                ShowOverlayToast(reason);
            }
            Logger.Write("danmaku skip: denied (" + reason + ") | " + message.UserName + " uid=" + message.Uid +
                         " admin=" + message.IsAdmin + " owner=" + ownerUid);
            return true;
        }

        // 防刷：3 秒内只响应一次
        if ((DateTime.UtcNow - _lastSkipCommandUtc).TotalSeconds < 3)
        {
            Logger.Write("danmaku skip: throttled | " + message.UserName);
            return true;
        }

        if (!HasOwnPlayback)
        {
            Logger.Write("danmaku skip: nothing playing | " + message.UserName);
            return true;
        }

        _lastSkipCommandUtc = DateTime.UtcNow;
        _lastRequestResult = "弹幕切歌：" + message.UserName;
        Logger.Write("danmaku skip: " + message.UserName + " (uid " + message.Uid + ")");
        ShowOverlayToast("已切歌：" + message.UserName);
        _ = SkipAsync(previous: false, allowForward: false);
        return true;
    }

    private static readonly Regex RemoveCommandPattern = new Regex(
        @"^(?:撤回|取消|删歌|删除|撤销|remove|del)(?:\s+(.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 观众撤掉队列里还没播的歌：
    ///   「撤回」= 自己的那首；「撤回 2」= 队列第 2 首；「撤回 起风了」= 按歌名（唯一匹配）。
    /// </summary>
    private bool TryHandleRemoveCommand(DanmakuMessage message)
    {
        var text = (message.Text ?? "").Trim().TrimEnd('。', '!', '！', '~', '～', ' ');
        var match = RemoveCommandPattern.Match(text);
        if (!match.Success) return false;

        var arg = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "";

        if (_settings.RemovePolicy == 2)
        {
            Logger.Write("danmaku remove: disabled | " + message.UserName);
            return true;
        }

        if ((DateTime.UtcNow - _lastRemoveCommandUtc).TotalSeconds < 2)
        {
            Logger.Write("danmaku remove: throttled | " + message.UserName);
            return true;
        }
        _lastRemoveCommandUtc = DateTime.UtcNow;

        QueueItem removed = null;
        string error = null;

        if (arg.Length == 0)
        {
            removed = _queue.RemoveByRequesterUid(message.Uid);
            if (removed == null) error = "队列里没有你点的歌（可用「撤回 序号」删指定的一首）";
        }
        else if (int.TryParse(arg, out var position))
        {
            var target = position >= 1 && position <= _queue.Items.Count ? _queue.Items[position - 1] : null;

            if (target == null)
            {
                error = "队列里没有第 " + position + " 首";
            }
            else if (_settings.RemovePolicy == 1 && !message.IsAdmin && !IsOwnedBy(target, message))
            {
                error = "只能撤回自己的歌（「撤回」不带序号）";
            }
            else
            {
                removed = _queue.RemoveAt(position);
                if (removed == null) error = "队列里没有第 " + position + " 首";
            }
        }
        else
        {
            int matches;
            var target = _queue.FindByKeyword(arg, out matches);

            if (target != null && _settings.RemovePolicy == 1 && !message.IsAdmin && !IsOwnedBy(target, message))
            {
                error = "只能撤回自己的歌（「撤回」不带序号）";
            }
            else if (target != null)
            {
                _queue.Remove(target.Id);
                removed = target;
            }
            else
            {
                error = matches > 1
                    ? "有多首匹配「" + arg + "」，请用「撤回 序号」"
                    : "队列里没找到「" + arg + "」";
            }
        }

        if (removed == null)
        {
            Logger.Write("danmaku remove: " + error + " | " + message.UserName + ": " + text);
            _lastRequestResult = "弹幕撤回失败：" + error;
            ShowOverlayToast(error);
            return true;
        }

        Logger.Write("danmaku remove: " + removed.DisplayName + " by " + message.UserName);
        _lastRequestResult = "弹幕撤回：" + removed.DisplayName + "（" + message.UserName + "）";
        ShowOverlayToast("已移除：" + removed.DisplayName);
        UpdateQueueMenu();
        return true;
    }

    private static bool IsOwnedBy(QueueItem item, DanmakuMessage message)
    {
        return item != null && item.Request != null &&
               item.Request.RequesterUid > 0 &&
               item.Request.RequesterUid == message.Uid;
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

        // 列表内容在 DropDownOpening 时重建即可，避免每次队列变化都新建控件
        UpdateDefaultPlaylistMenu();
        RefreshLiveStatus();
        RefreshOverlaySnapshot();
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
            foreach (var item in _queue.Items.Take(10))
            {
                var position = index;
                var text = position + ". " + item.DisplayName +
                           (string.IsNullOrWhiteSpace(item.Requester) ? "" : "  ·  " + item.Requester);
                var songItem = new ToolStripMenuItem(text);

                songItem.DropDownItems.Add(new ToolStripMenuItem("上移一位", null, (_, _) =>
                {
                    if (_queue.Move(position - 1, position - 2)) UpdateQueueMenu();
                }) { Enabled = position > 1 });

                songItem.DropDownItems.Add(new ToolStripMenuItem("下移一位", null, (_, _) =>
                {
                    if (_queue.Move(position - 1, position)) UpdateQueueMenu();
                }) { Enabled = position < _queue.Count });

                songItem.DropDownItems.Add(new ToolStripMenuItem("移除", null, (_, _) =>
                {
                    if (_queue.RemoveAt(position) != null) UpdateQueueMenu();
                }));

                _itemQueueList.DropDownItems.Add(songItem);
                index++;
            }

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
        if (HasOwnPlayback) return;
        _forwardOwn.Clear();
        _ringReverseOnce = false;

        var item = _queue.TakeNext();
        if (item == null) return;

        _playingFromQueue = true;
        _playingDefaultPlaylist = false;
        _videoBvid = "";
        StopVideoTimer();
        UpdateQueueMenu();

        var generation = ++_playGeneration;

        ResolvedSong song = null;
        try
        {
            song = await ResolveQueuedSongAsync(item);
        }
        catch (Exception ex)
        {
            Logger.Write("queue resolve: " + ex.Message);
        }

        // 解析期间可能已经被切歌/停止/又开始了新的一首：旧流程必须作废
        if (generation != _playGeneration)
        {
            Logger.Write("queue resolve: stale (generation " + generation + " != " + _playGeneration + "), discard");
            return;
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
            ContinuePlayback();
            return;
        }

        item.Title = song.Title;
        item.Artist = song.Artist;
        item.ResolvedUrl = song.AudioUrl;
        item.Duration = song.Duration;
        item.Resolved = true;

        // 视频点播：叠加层 /video 小窗里同步播放
        _videoBvid = item.Request != null && item.Request.IsVideo ? ExtractVideoBvid(item, song) : "";

        if (!string.IsNullOrEmpty(_videoBvid)) VideoCache.Prefetch(_videoBvid);

        _activeSong = song;
        _currentOwnEntry = new OwnHistoryEntry
        {
            Kind = OwnKind.Queue,
            Song = song,
            QueueItem = item,
            Bvid = _videoBvid
        };

        if (_videoBvid.Length > 0 && _settings.VideoAudio == 1)
        {
            if (_currentOwnEntry != null) _currentOwnEntry.Started = true;
            _ownPlayedCount++;
            StartVideoPlayback(song);
            return;
        }

        try
        {
            await EnsurePlayer().PlayAsync(song);
            if (generation != _playGeneration) return;
            if (_currentOwnEntry != null) _currentOwnEntry.Started = true;
            _ownPlayedCount++;
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

            StopOwnPlayback(keepDisplayed: true);
            ContinuePlayback();
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
        _forwardOwn.Clear();
        UpdateQueueMenu();
        _tray.ShowBalloonTip(1800, "Music Toppin", "点歌队列已清空", ToolTipIcon.Info);
    }

    private OverlayState BuildOverlayState()
    {
        // HTTP 线程只读快照：不要在非 UI 线程读 _latest / _hud.Visible
        return _overlaySnapshot ?? new OverlayState();
    }

    /// <summary>在 UI 线程生成不可变快照，供叠加层 HTTP 线程读取。</summary>
    private void RefreshOverlaySnapshot()
    {
        var np = _latest;
        var theme = HudRenderer.Theme;

        _overlaySnapshot = new OverlayState
        {
            Title = np.HasSession ? np.Title : "",
            Artist = np.HasSession ? np.Artist : "",
            Requester = np.HasSession ? (np.Requester ?? "") : "",
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
            VideoNonce = _videoNonce,
            Show = np.HasSession && np.Title.Length > 0 && (_hud.Visible || _settings.Overlay)
        };
    }

    private static string ToHex(Color color)
    {
        return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
    }

    private void ToggleOverlay()
    {
        SetOverlayEnabled(!_settings.Overlay);
    }

    private void ToggleLyricsOverlay()
    {
        _settings.LyricsOverlayEnabled = !_settings.LyricsOverlayEnabled;
        _itemLyricsOverlay.Checked = _settings.LyricsOverlayEnabled;
        _settings.Save();

        if (_settings.LyricsOverlayEnabled)
        {
            StartOverlay(notify: false);
            _itemCopyLyrics.Enabled = _overlay != null;
            if (_activeSong != null)
            {
                LyricsService.SetSong(_activeSong.Title, _activeSong.Artist, _activeSong.SourceLabel, _activeSong.PageUrl);
            }
            Logger.Write("lyrics overlay: on");
            _tray.ShowBalloonTip(3000, "Music Toppin", "歌词叠层已开启：" + (_overlay != null ? _overlay.Url + "lyrics" : ""), ToolTipIcon.Info);
        }
        else
        {
            _itemCopyLyrics.Enabled = false;
            LyricsService.Clear();
            Logger.Write("lyrics overlay: off");
        }
    }

    /// <summary>托盘和上屏向导共用：显式开启/关闭浏览器源叠加层。</summary>
    private void SetOverlayEnabled(bool enabled)
    {
        if (_settings.Overlay == enabled)
        {
            if (enabled && _overlay == null) StartOverlay(notify: true);
            else if (!enabled && _overlay != null) StopOverlay();
            else if (enabled && _overlay != null)
            {
                try { Clipboard.SetText(_overlay.Url); } catch { }
            }
            _itemOverlay.Checked = enabled;
            return;
        }

        _settings.Overlay = enabled;
        _itemOverlay.Checked = enabled;
        _settings.Save();

        if (enabled) StartOverlay(notify: true);
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
        _itemCopyLyrics.Enabled = _settings.LyricsOverlayEnabled;
        _itemCopyOverlay.Text = "复制叠加层地址（端口 " + _overlay.Port + "）";
        RefreshOverlaySnapshot();

        var url = _overlay.Url;
        Logger.Write("overlay url: " + url);

        // 启动时自动恢复的叠加层不弹气泡、也不覆盖剪贴板；手动开启才复制地址并提示
        if (!notify) return;

        try { Clipboard.SetText(url); } catch { }

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
        _itemCopyLyrics.Enabled = false;
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
        SetOffscreenEnabled(!_settings.CaptureOffscreen);
    }

    private void SetOffscreenEnabled(bool enabled)
    {
        if (_settings.CaptureOffscreen == enabled) return;

        _settings.CaptureOffscreen = enabled;
        _itemOffscreen.Checked = enabled;

        if (enabled && _settings.CaptureOccluded)
        {
            _settings.CaptureOccluded = false;
            _itemOccluded.Checked = false;
            _hud.SetCaptureOccluded(false);
        }

        _settings.Save();

        if (enabled)
        {
            if (!_hud.Visible) SetHudVisible(true);
            _hud.SetCaptureOffscreen(true);

            _tray.ShowBalloonTip(7000, "Music Toppin",
                "浮窗已移到屏幕外。\n在直播姬/直播伴侣里添加「窗口采集」，选择窗口「Music Toppin」。\n想恢复：再点一次这个菜单。",
                ToolTipIcon.Info);
            return;
        }

        _hud.SetCaptureOffscreen(false);
        UpdateRingSide();
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
            var context = new WizardContext
            {
                Settings = _settings,
                OverlayRunning = () => _overlay != null,
                SetOverlay = SetOverlayEnabled,
                HudVisible = () => _hud.Visible,
                SetHudVisible = SetHudVisible,
                SetGreen = SetGreenEnabled,
                Offscreen = () => _settings.CaptureOffscreen,
                SetOffscreen = SetOffscreenEnabled,
                Occluded = () => _settings.CaptureOccluded,
                SetOccluded = enabled => { if (_settings.CaptureOccluded != enabled) ToggleOccluded(); },
                ConnectLive = PromptLiveRoom,
                SyncNetease = SyncNeteaseFromClient,
                LoginNetease = LoginNetease,
                LoginQQ = LoginQQMusic,
                ShowPlayerCheck = ShowPlayerCheck,
                OpenLogFolder = OpenLogFolder,
                LiveStatusText = BuildLiveStatusText,
                SourceStatusText = BuildSourceStatusText,
                SetRequestPolicy = SetRequestPolicy,
                SetSkipPolicy = SetSkipPolicy
            };

            using (var wizard = new WizardForm(context))
            {
                wizard.ShowDialog(_hud);
            }
        }
        catch (Exception ex)
        {
            Logger.Write("wizard: " + ex.GetType().Name + " " + ex.Message + " | " + ex.StackTrace);
        }
        finally
        {
            _wizardOpen = false;
        }
    }

    private string BuildLiveStatusText()
    {
        if (_danmaku == null) return "未连接直播间";

        if (_danmaku.IsConnected)
        {
            var live = _danmaku.LiveStatus == 0 ? "（未开播，收不到弹幕）" : "";
            return "直播间 " + _danmaku.RoomId + live + " · 已收到 " + _danmaku.MessageCount + " 条弹幕 · 队列 " + _queue.Count + " 首";
        }

        if (_danmaku.IsRunning) return "直播间 " + _danmaku.RoomId + " · 正在连接 / 重连中…";
        return _settings.LiveRoomId > 0 ? "上次直播间 " + _settings.LiveRoomId + " · 当前未连接" : "未连接直播间";
    }

    private string BuildSourceStatusText()
    {
        var parts = new List<string>();
        parts.Add("网易云：" + (Netease.IsLoggedIn
            ? (string.IsNullOrWhiteSpace(_settings.NeteaseNick) ? "已登录" : "已登录 " + _settings.NeteaseNick)
            : "游客"));
        parts.Add("QQ 音乐：" + (QQMusic.IsLoggedIn
            ? "已登录"
            : (QQMusic.ClientLoggedIn ? "客户端已登录（应用内未登录）" : "未登录")));
        return string.Join("　·　", parts);
    }

    private void OpenLogFolder()
    {
        try
        {
            if (File.Exists(Logger.FilePath))
            {
                Process.Start("explorer.exe", "/select,\"" + Logger.FilePath + "\"");
            }
            else
            {
                Process.Start("explorer.exe", Path.GetTempPath());
            }
        }
        catch (Exception ex)
        {
            Logger.Write("open log folder: " + ex.Message);
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

        // 先按最终位置落位，再判断贴边方向（自由拖动时方向取决于落位后的中心）
        if (!_settings.CaptureOffscreen) PlaceHud();
        UpdateRingSide();
        _hud.Render();

        // 环状方向可能改变窗口尺寸，再落位一次
        if (!_settings.CaptureOffscreen && _hud.IsRing) PlaceHud();

        if (_settings.CaptureOccluded) _hud.SetCaptureOccluded(true);
        else _hud.TopMost = true;

        if (_settings.CaptureOffscreen) _hud.SetCaptureOffscreen(true);

        EnsureHotkeys();
    }

    private void HideHud()
    {
        _hud.CancelDrag();
        _hud.Hide();
    }

    private void ToggleVisible()
    {
        SetHudVisible(!_hud.Visible);
    }

    /// <summary>托盘/向导共用：显式显示或隐藏桌面浮窗（不影响浏览器源叠加层）。</summary>
    private void SetHudVisible(bool visible)
    {
        if (visible == _hud.Visible)
        {
            _settings.Visible = visible;
            _itemShow.Checked = visible;
            _settings.Save();
            return;
        }

        if (visible)
        {
            _settings.Visible = true;
            ShowHud();
            _hud.SetState(_latest, animate: false);
        }
        else
        {
            _settings.Visible = false;
            HideHud();
        }

        _itemShow.Checked = _hud.Visible;
        _settings.Save();
        RefreshOverlaySnapshot();
    }

    private void ToggleLock()
    {
        _lastLockToggleUtc = DateTime.UtcNow;
        _settings.Locked = !_settings.Locked;
        _hud.SetLocked(_settings.Locked);
        _hud.SetEditing(!_settings.Locked);
        _itemLock.Checked = _settings.Locked;
        _settings.Save();
    }

    private void ToggleGreen()
    {
        SetGreenEnabled(!_settings.Green);
    }

    private void SetGreenEnabled(bool green)
    {
        _settings.Green = green;
        _hud.SetGreen(green);
        _itemGreen.Checked = green;
        _settings.Save();
    }

    private void ToggleMediaKeys()
    {
        _settings.DisableMediaKeys = !_settings.DisableMediaKeys;
        WindowPlayers.MediaKeysEnabled = !_settings.DisableMediaKeys;
        _itemSafeMedia.Checked = _settings.DisableMediaKeys;
        _settings.Save();

        _tray.ShowBalloonTip(2500, "Music Toppin",
            _settings.DisableMediaKeys
                ? "已开启反作弊保守模式：不再发送系统媒体键，外部播放器的上一首/下一首不可用。"
                : "已关闭反作弊保守模式：恢复媒体键切歌。",
            ToolTipIcon.Info);
    }

    private void ToggleAutoStart()
    {
        _settings.ApplyAutoStart(!_settings.AutoStart);
        _itemAutoStart.Checked = _settings.AutoStart;
        _settings.Save();
    }

    private void SetStyle(int style)
    {
        style = Math.Max(0, Math.Min(2, style));
        _settings.Style = style;
        _hud.SetCompact(style == 1);
        _hud.SetRing(style == 2);
        _itemCompact.Checked = style == 1;
        _itemKillfeed.Checked = style == 0;
        _itemRing.Checked = style == 2;
        _settings.Save();

        if (!string.IsNullOrEmpty(_anchor))
        {
            MoveHudTo(_anchor, fromUser: false);
        }
        else if (!_settings.CaptureOffscreen)
        {
            var clamped = ClampToWorkArea(_hud.Bounds);
            _hud.Location = clamped.Location;
            UpdateRingSide();
            if (_hud.IsRing) _hud.Location = ClampToWorkArea(_hud.Bounds).Location;
        }
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
        Logger.Write(message);
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

    private async Task SkipAsync(bool previous, bool allowForward = true)
    {
        if (previous)
        {
            // 手动 BV 还在解析：按上一首先取消这次待起播，避免随后又突然开播
            if (_manualPlaybackPending)
            {
                _manualPlaybackPending = false;
                _playGeneration++;
            }

            // 历史非空就优先回退；播放已停止/自然播完时也能恢复上一首
            if ((HasOwnPlayback || ShouldResumeOwnHistory()) && await PlayPreviousOwnAsync()) return;

            if (HasOwnPlayback)
            {
                // 还没有历史：把当前曲目重头播
                if (_videoSong != null)
                {
                    Logger.Write("skip: restart video (no history)");
                    RestartVideo();
                    return;
                }

                if (_player != null && _player.Current != null)
                {
                    Logger.Write("skip: restart track (no history)");
                    _ = _player.SeekAsync(TimeSpan.Zero);
                    return;
                }
            }

            if (_service is null) return;
            await _service.SkipPreviousAsync();
            return;
        }

        // 下一首：如果刚点过上一首，优先前进回退前那首；外部播放/弹幕切歌不要抢系统下一首
        if (allowForward && HasOwnPlayback && await PlayForwardOwnAsync()) return;

        _forwardOwn.Clear();

        if (HasOwnPlayback)
        {
            // 切歌：停掉当前（视频/音乐），点歌队列优先，空了回默认歌单
            Logger.Write("skip: next (own playback)");
            StopOwnPlayback(keepDisplayed: true);
            ContinuePlayback();
            return;
        }

        if (_service is null) return;
        await _service.SkipNextAsync();
    }

    /// <summary>本应用已停止播放时，如果历史顶部的标题仍是当前外部状态，说明是刚停掉的那首。</summary>
    private bool ShouldResumeOwnHistory()
    {
        if (_ownHistory.Count == 0) return false;
        if (!_latest.HasSession) return true;

        var entry = _ownHistory[_ownHistory.Count - 1];
        return entry != null && entry.Song != null &&
               string.Equals(_latest.Title, entry.Song.Title, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> PlayPreviousOwnAsync()
    {
        while (_ownHistory.Count > 0 &&
               (_ownHistory[_ownHistory.Count - 1] == null || _ownHistory[_ownHistory.Count - 1].Song == null))
        {
            _ownHistory.RemoveAt(_ownHistory.Count - 1);
        }
        if (_ownHistory.Count == 0) return false;

        var entry = _ownHistory[_ownHistory.Count - 1];
        _ownHistory.RemoveAt(_ownHistory.Count - 1);

        // 当前这首留作「下一首」的前进目标
        var current = _currentOwnEntry;
        if (current != null && current.Started && !ReferenceEquals(current, entry))
        {
            if (_forwardOwn.Count == 0 || !ReferenceEquals(_forwardOwn[_forwardOwn.Count - 1], current))
                _forwardOwn.Add(current);
            while (_forwardOwn.Count > 12) _forwardOwn.RemoveAt(0);
        }

        return await PlayOwnHistoryEntryAsync(entry, goingBack: true);
    }

    private async Task<bool> PlayForwardOwnAsync()
    {
        while (_forwardOwn.Count > 0 &&
               (_forwardOwn[_forwardOwn.Count - 1] == null || _forwardOwn[_forwardOwn.Count - 1].Song == null))
        {
            _forwardOwn.RemoveAt(_forwardOwn.Count - 1);
        }
        if (_forwardOwn.Count == 0) return false;

        var entry = _forwardOwn[_forwardOwn.Count - 1];
        _forwardOwn.RemoveAt(_forwardOwn.Count - 1);
        return await PlayOwnHistoryEntryAsync(entry, goingBack: false);
    }

    private async Task<bool> PlayOwnHistoryEntryAsync(OwnHistoryEntry entry, bool goingBack)
    {
        if (entry == null || entry.Song == null) return false;

        // 只有真正起播成功才重新计入历史；失败条目不会再次压回「上一首」
        entry.Started = false;

        if (goingBack)
        {
            _suppressOwnHistory = true;
            try { StopOwnPlayback(keepDisplayed: true); }
            finally { _suppressOwnHistory = false; }
        }
        else
        {
            StopOwnPlayback(keepDisplayed: true);
        }

        // StopOwnPlayback 会清方向；在停止之后再设置本次起播方向
        _ringReverseOnce = goingBack;

        var generation = ++_playGeneration;
        _activeSong = entry.Song;
        _currentOwnEntry = entry;
        _videoBvid = entry.Bvid ?? "";
        _playingFromQueue = entry.Kind == OwnKind.Queue;
        _playingDefaultPlaylist = entry.Kind == OwnKind.DefaultPlaylist;
        StopVideoTimer();

        if (entry.Kind == OwnKind.Queue && entry.QueueItem != null)
            _queue.RestoreCurrent(entry.QueueItem);

        // 视频跟随模式：直接用原 bvid 让 overlay 重开，不必等音频直链
        if (_videoBvid.Length > 0 && _settings.VideoAudio == 1)
        {
            entry.Started = true;
            _ownPlayedCount++;
            StartVideoPlayback(entry.Song);
            return true;
        }

        ResolvedSong fresh = null;
        try
        {
            if (entry.Kind == OwnKind.DefaultPlaylist && entry.Track != null)
                fresh = await Task.Run(() => DefaultPlaylist.Resolve(entry.Track));
            else if (entry.Kind == OwnKind.Bilibili && !string.IsNullOrEmpty(entry.Bvid))
                fresh = await Task.Run(() => Bilibili.Resolve(entry.Bvid));
            else if (entry.Kind == OwnKind.Queue && entry.QueueItem != null && entry.QueueItem.Request != null)
                fresh = await Task.Run(() => SongResolvers.Resolve(entry.QueueItem.Request));
        }
        catch (Exception ex)
        {
            Logger.Write("history resolve: " + ex.Message);
        }

        if (generation != _playGeneration) return true;
        if (fresh != null) entry.Song = fresh;
        _activeSong = entry.Song;

        if (entry.Song == null || string.IsNullOrWhiteSpace(entry.Song.AudioUrl))
        {
            _ringReverseOnce = false;
            StopOwnPlayback();
            LiveNotify("Music Toppin", "这首歌暂时放不了", ToolTipIcon.Warning);
            return true;
        }

        if (entry.Kind == OwnKind.DefaultPlaylist && entry.Track != null &&
            entry.Track.Source == CurrentPlaylistSource &&
            entry.TrackIndex >= 0 && entry.TrackIndex < _defaultPlaylistTracks.Count &&
            string.Equals(_defaultPlaylistTracks[entry.TrackIndex].Id, entry.Track.Id, StringComparison.Ordinal))
        {
            _defaultPlaylistIndex = (entry.TrackIndex + 1) % _defaultPlaylistTracks.Count;
        }

        try
        {
            await EnsurePlayer().PlayAsync(entry.Song);
            if (generation != _playGeneration) return true;
            entry.Started = true;
            _ownPlayedCount++;
            PushOwnState(entry.Song, new PlaybackState { Playing = true, Duration = entry.Song.Duration });
            if (entry.Kind == OwnKind.DefaultPlaylist) _defaultPlaylistFailStreak = 0;
            Logger.Write((goingBack ? "previous" : "forward") + " play: " + entry.Song.Title +
                         (string.IsNullOrEmpty(entry.Song.Artist) ? "" : " - " + entry.Song.Artist));
        }
        catch (Exception ex)
        {
            Logger.Write("history play failed: " + ex.Message);
            entry.Started = false;
            _ringReverseOnce = false;
            StopOwnPlayback();
            LiveNotify("Music Toppin", "这首歌暂时放不了：" + entry.Song.Title, ToolTipIcon.Warning);

            // 回退失败不再自动前进，避免点「上一首」却跳到下一首
        }

        return true;
    }

    private void RestartOwnPlayback()
    {
        if (_videoSong != null)
        {
            RestartVideo();
            return;
        }

        if (_player != null && _player.Current != null)
        {
            _ = _player.SeekAsync(TimeSpan.Zero);
            return;
        }

        Logger.Write("restart current: no own playback");
    }

    private void RestartVideo()
    {
        if (_videoBvid.Length == 0 || _videoSong == null) return;

        _videoNonce++;
        _videoStartedUtc = DateTime.UtcNow;
        PushOwnState(_videoSong, new PlaybackState { Playing = true, Position = TimeSpan.Zero, Duration = _videoDuration });
    }

    private void Apply(NowPlaying np)
    {
        if (_exiting) return;

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

        RefreshOverlaySnapshot();
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
                var packetAge = _danmaku.LastPacketUtc == DateTime.MinValue
                    ? -1
                    : (int)(DateTime.UtcNow - _danmaku.LastPacketUtc).TotalSeconds;

                Logger.Write("danmaku tick: room " + _danmaku.RoomId + " live=" + _danmaku.LiveStatus +
                             " messages=" + _danmaku.MessageCount + " queue=" + _queue.Count +
                             " lastPacket=" + packetAge + "s");

                // 「先连上、后开播」的情况：每分钟重新拉一次开播状态
                if (_danmaku.LiveStatus != 1)
                {
                    _ = _danmaku.RefreshLiveStatusAsync();
                }
            }

            RefreshOverlaySnapshot();
        }

        if (_latest.HasSession || !_hud.Visible) return;

        // 离屏/遮挡是给采集软件用的：窗口一旦真 Hide，采集源会从列表里消失，
        // 再 Show 时 PlaceHud 也会把窗口搬回桌面，采集模式实际失效。
        if (_settings.CaptureOffscreen || _settings.CaptureOccluded) return;

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
        _exiting = true;

        try
        {
            _idleTimer.Stop();
            _relockTimer.Stop();
            _idleTimer.Dispose();
            _relockTimer.Dispose();
            _demoTimer?.Stop();
            _demoTimer?.Dispose();
            StopVideoTimer();
            if (_videoTimer != null)
            {
                _videoTimer.Dispose();
                _videoTimer = null;
            }
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
