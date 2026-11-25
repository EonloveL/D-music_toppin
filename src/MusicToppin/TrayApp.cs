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
            styleMenu,
            _colorMenu,
            positionMenu,
            _itemWizard,
            _itemCheckUpdate,
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
        if (!string.IsNullOrEmpty(_settings.Anchor)) MoveHudTo(_settings.Anchor);
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
        if (!string.IsNullOrEmpty(anchor)) MoveHudTo(anchor);

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
    private ToolStripMenuItem _colorMenu;
    private ToolStripMenuItem _itemAccentColor;
    private ToolStripMenuItem _itemTitleColor;
    private ToolStripMenuItem _itemArtistColor;
    private bool _wizardOpen;
    private bool _updateDialogOpen;
    private bool _checkingUpdate;
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
            _service?.Dispose();

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
