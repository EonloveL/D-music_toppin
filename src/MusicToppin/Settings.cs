using Microsoft.Win32;

namespace MusicToppin;

/// <summary>
/// 设置存注册表（HKCU），避免额外 JSON 依赖，也让产物保持几十 KB。
/// 默认「锁定」= 鼠标穿透：打游戏时准星扫过浮窗不会吞掉点击。
/// </summary>
internal sealed class AppSettings
{
    private const string KeyPath = @"Software\MusicToppin";
    private const int CurrentSchema = 2;

    public int X = -1;
    public int Y = -1;
    public bool Green;
    public bool Locked = true;
    public bool Compact;
    public bool AutoStart;
    public bool Visible = true;
    public bool Hotkeys = true;

    /// <summary>自定义的锁定/解锁快捷键（如 "Ctrl+Alt+F9"）；空 = 自动挑选。</summary>
    public string Hotkey = "";
    public bool WizardShown;

    /// <summary>更新清单地址（留空则用编译期默认值）。</summary>
    public string UpdateUrl = "";

    /// <summary>启动时是否自动检查更新。</summary>
    public bool UpdateCheckOnStart = true;

    /// <summary>弹幕点歌：上次连接的直播间号（0 = 未配置）。</summary>
    public int LiveRoomId;

    /// <summary>启动时是否自动重连上次的直播间。</summary>
    public bool LiveAutoConnect = true;

    /// <summary>点歌/播放时是否弹 Windows 气泡（默认关闭：直播中弹窗会干扰键鼠输入）。</summary>
    public bool LiveToast;

    /// <summary>内置播放器音量（0-100）。默认 60，避免直播时盖过游戏/人声。</summary>
    public int PlayerVolume = 60;

    /// <summary>视频点播的声音：1 = 跟随视频（音画同步，默认），0 = 走应用播放器（可调音量）。</summary>
    public int VideoAudio = 1;

    /// <summary>叠加层上次用的端口（0 = 没记录）。</summary>
    public int OverlayPort;

    /// <summary>网易云登录 cookie（MUSIC_U 等；空 = 游客）。</summary>
    public string NeteaseCookie = "";
    public string NeteaseNick = "";

    /// <summary>QQ 音乐登录 cookie + uin（空 = 只能搜索不能播）。</summary>
    public string QQMusicCookie = "";
    public string QQMusicUin = "";
    public string QQMusicNick = "";

    /// <summary>是否开启浏览器源叠加层（给 OBS 用，桌面浮窗可以隐藏）。</summary>
    public bool Overlay;

    /// <summary>把浮窗移到屏幕外（配合只能做窗口采集的直播软件）。</summary>
    public bool CaptureOffscreen;

    /// <summary>遮挡模式：浮窗压在其它窗口后面（主播看不到，窗口采集仍可抓到）。</summary>
    public bool CaptureOccluded;
    public int Schema;

    // 颜色（ARGB），默认值与设计稿一致
    public int AccentColor = unchecked((int)0xFFFF3B30);
    public int TitleColor = unchecked((int)0xFFF5F8FC);
    public int ArtistColor = unchecked((int)0xFF9FB0C6);
    public int CardColor = unchecked((int)0xF60E1117);

    /// <summary>背景不透明度（10~100），默认 96 接近不透明。</summary>
    public int CardOpacity = 96;

    /// <summary>位置锚点：空=自由拖动；tl/tr/bl/br/center=自动贴边。</summary>
    public string Anchor = "";
    public bool Migrated;

    /// <summary>一套默认值（用于「恢复默认颜色」）。</summary>
    public static AppSettings Default()
    {
        return new AppSettings();
    }

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null) return s;
                s.X = ReadInt(key, "X", -1);
                s.Y = ReadInt(key, "Y", -1);
                s.Green = ReadInt(key, "Green", 0) == 1;
                s.Locked = ReadInt(key, "Locked", 1) == 1;
                s.Compact = ReadInt(key, "Compact", 0) == 1;
                s.AutoStart = ReadInt(key, "AutoStart", 0) == 1;
                s.Visible = ReadInt(key, "Visible", 1) == 1;
                s.Hotkeys = ReadInt(key, "Hotkeys", 1) == 1;
                s.Hotkey = ReadString(key, "Hotkey", "");
                s.WizardShown = ReadInt(key, "WizardShown", 0) == 1;
                s.UpdateUrl = ReadString(key, "UpdateUrl", "");
                s.UpdateCheckOnStart = ReadInt(key, "UpdateCheckOnStart", 1) == 1;
                s.LiveRoomId = ReadInt(key, "LiveRoomId", 0);
                s.LiveAutoConnect = ReadInt(key, "LiveAutoConnect", 1) == 1;
                s.LiveToast = ReadInt(key, "LiveToast", 0) == 1;
                s.PlayerVolume = ReadInt(key, "PlayerVolume", 60);
                s.VideoAudio = ReadInt(key, "VideoAudio", 1);
                s.OverlayPort = ReadInt(key, "OverlayPort", 0);
                s.NeteaseCookie = ReadString(key, "NeteaseCookie", "");
                s.NeteaseNick = ReadString(key, "NeteaseNick", "");
                s.QQMusicCookie = ReadString(key, "QQMusicCookie", "");
                s.QQMusicUin = ReadString(key, "QQMusicUin", "");
                s.QQMusicNick = ReadString(key, "QQMusicNick", "");
                s.Overlay = ReadInt(key, "Overlay", 0) == 1;
                s.CaptureOffscreen = ReadInt(key, "CaptureOffscreen", 0) == 1;
                s.CaptureOccluded = ReadInt(key, "CaptureOccluded", 0) == 1;
                s.Schema = ReadInt(key, "Schema", 0);
                s.AccentColor = ReadInt(key, "AccentColor", unchecked((int)0xFFFF3B30));
                s.TitleColor = ReadInt(key, "TitleColor", unchecked((int)0xFFF5F8FC));
                s.ArtistColor = ReadInt(key, "ArtistColor", unchecked((int)0xFF9FB0C6));
                s.CardColor = ReadInt(key, "CardColor", unchecked((int)0xF60E1117));
                s.CardOpacity = ReadInt(key, "CardOpacity", 96);
                var anchorValue = key.GetValue("Anchor") as string;
                s.Anchor = anchorValue ?? "";
            }
        }
        catch { }

        // 旧版本默认是可交互的，升级后统一改成穿透，避免打游戏被吞点击
        if (s.Schema < CurrentSchema)
        {
            s.Locked = true;
            s.Hotkeys = true;
            s.Migrated = true;
            s.Save();
        }

        return s;
    }

    private static int ReadInt(RegistryKey key, string name, int fallback)
    {
        try
        {
            var v = key.GetValue(name);
            if (v is int) return (int)v;
        }
        catch { }
        return fallback;
    }

    private static string ReadString(RegistryKey key, string name, string fallback)
    {
        try
        {
            var value = key.GetValue(name) as string;
            if (value != null) return value;
        }
        catch { }
        return fallback;
    }

    private static void WriteInt(RegistryKey key, string name, int value)
    {
        try { key.SetValue(name, value, RegistryValueKind.DWord); } catch { }
    }

    public void Save()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) return;
                WriteInt(key, "X", X);
                WriteInt(key, "Y", Y);
                WriteInt(key, "Green", Green ? 1 : 0);
                WriteInt(key, "Locked", Locked ? 1 : 0);
                WriteInt(key, "Compact", Compact ? 1 : 0);
                WriteInt(key, "AutoStart", AutoStart ? 1 : 0);
                WriteInt(key, "Visible", Visible ? 1 : 0);
                WriteInt(key, "Hotkeys", Hotkeys ? 1 : 0);
                try { key.SetValue("Hotkey", Hotkey ?? "", RegistryValueKind.String); } catch { }
                WriteInt(key, "WizardShown", WizardShown ? 1 : 0);
                WriteInt(key, "UpdateCheckOnStart", UpdateCheckOnStart ? 1 : 0);
                WriteInt(key, "LiveRoomId", LiveRoomId);
                WriteInt(key, "LiveAutoConnect", LiveAutoConnect ? 1 : 0);
                WriteInt(key, "LiveToast", LiveToast ? 1 : 0);
                WriteInt(key, "PlayerVolume", PlayerVolume);
                WriteInt(key, "VideoAudio", VideoAudio);
                WriteInt(key, "OverlayPort", OverlayPort);
                try { key.SetValue("NeteaseCookie", NeteaseCookie ?? "", RegistryValueKind.String); } catch { }
                try { key.SetValue("NeteaseNick", NeteaseNick ?? "", RegistryValueKind.String); } catch { }
                try { key.SetValue("QQMusicCookie", QQMusicCookie ?? "", RegistryValueKind.String); } catch { }
                try { key.SetValue("QQMusicUin", QQMusicUin ?? "", RegistryValueKind.String); } catch { }
                try { key.SetValue("QQMusicNick", QQMusicNick ?? "", RegistryValueKind.String); } catch { }
                WriteInt(key, "Overlay", Overlay ? 1 : 0);
                WriteInt(key, "CaptureOffscreen", CaptureOffscreen ? 1 : 0);
                WriteInt(key, "CaptureOccluded", CaptureOccluded ? 1 : 0);
                try { key.SetValue("UpdateUrl", UpdateUrl ?? "", RegistryValueKind.String); } catch { }
                WriteInt(key, "Schema", CurrentSchema);
                WriteInt(key, "AccentColor", AccentColor);
                WriteInt(key, "TitleColor", TitleColor);
                WriteInt(key, "ArtistColor", ArtistColor);
                WriteInt(key, "CardColor", CardColor);
                WriteInt(key, "CardOpacity", CardOpacity);
                try { key.SetValue("Anchor", Anchor ?? "", RegistryValueKind.String); } catch { }
            }
        }
        catch { }
    }

    public void ApplyAutoStart(bool enabled)
    {
        AutoStart = enabled;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;
                if (enabled) key.SetValue("MusicToppin", "\"" + AppPaths.ExePath + "\"");
                else key.DeleteValue("MusicToppin", false);
            }
        }
        catch { }
    }
}

internal static class AppPaths
{
    public static string ExePath
    {
        get
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }
            return System.Windows.Forms.Application.ExecutablePath;
        }
    }
}
