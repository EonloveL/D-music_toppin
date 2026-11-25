using System.Runtime.InteropServices;
using System.Text;

namespace MusicToppin;

/// <summary>
/// 窗口标题通道：网易云、QQ音乐等桌面播放器不注册 SMTC，但会把「歌名 - 歌手」写在窗口标题上。
/// 用 EnumWindows + 2 秒扫描缓存，避免每 500ms 枚举全系统进程（那会让 CPU 从 0.2% 涨到 1.5%）。
/// </summary>
internal static class WindowPlayers
{
    private sealed class PlayerDef
    {
        public string Process;
        public bool ArtistFirst;   // foobar2000 / AIMP / MusicBee 默认是「歌手 - 歌名」
        public string[] Strip;     // 标题里要剥掉的应用名后缀
    }

    private static readonly PlayerDef[] Known =
    {
        new PlayerDef { Process = "cloudmusic",      ArtistFirst = false, Strip = new[] { "网易云音乐", "NetEase Cloud Music", "CloudMusic" } },
        new PlayerDef { Process = "qqmusic",         ArtistFirst = false, Strip = new[] { "QQ音乐", "QQMusic" } },
        new PlayerDef { Process = "qqmusicclient",   ArtistFirst = false, Strip = new[] { "QQ音乐", "QQMusic" } },
        new PlayerDef { Process = "kugou",           ArtistFirst = false, Strip = new[] { "酷狗音乐", "酷狗", "KuGou" } },
        new PlayerDef { Process = "kgmusic",         ArtistFirst = false, Strip = new[] { "酷狗音乐", "酷狗", "KuGou" } },
        new PlayerDef { Process = "kwmusic",         ArtistFirst = false, Strip = new[] { "酷我音乐", "酷我", "Kuwo" } },
        new PlayerDef { Process = "migu",            ArtistFirst = false, Strip = new[] { "咪咕音乐", "咪咕", "Migu" } },
        new PlayerDef { Process = "spotify",         ArtistFirst = false, Strip = new[] { "Spotify" } },
        // Apple Music（Windows 新版，MSIX）与旧版 iTunes
        new PlayerDef { Process = "applemusic",      ArtistFirst = false, Strip = new[] { "Apple Music" } },
        new PlayerDef { Process = "apple music",     ArtistFirst = false, Strip = new[] { "Apple Music" } },
        new PlayerDef { Process = "itunes",          ArtistFirst = false, Strip = new[] { "iTunes" } },
        new PlayerDef { Process = "potplayermini64", ArtistFirst = false, Strip = new[] { "PotPlayer" } },
        new PlayerDef { Process = "potplayermini",   ArtistFirst = false, Strip = new[] { "PotPlayer" } },
        new PlayerDef { Process = "foobar2000",      ArtistFirst = true,  Strip = new[] { "foobar2000" } },
        new PlayerDef { Process = "aimp",            ArtistFirst = true,  Strip = new[] { "AIMP" } },
        new PlayerDef { Process = "aimp64",          ArtistFirst = true,  Strip = new[] { "AIMP" } },
        new PlayerDef { Process = "musicbee",        ArtistFirst = true,  Strip = new[] { "MusicBee" } },
        new PlayerDef { Process = "ttplayer",        ArtistFirst = false, Strip = new[] { "千千静听", "TTPlayer" } },
        new PlayerDef { Process = "wmplayer",        ArtistFirst = false, Strip = new[] { "Windows Media Player" } },
        new PlayerDef { Process = "mpc-hc64",        ArtistFirst = false, Strip = new[] { "MPC-HC" } },
        new PlayerDef { Process = "mpc-be64",        ArtistFirst = false, Strip = new[] { "MPC-BE" } }
    };

    private static readonly string[] Separators = { " - ", " – ", " — " };

    private sealed class Candidate
    {
        public IntPtr Handle;
        public PlayerDef Def;
        public uint Pid;
        public bool Visible;
    }

    private static readonly List<Candidate> Candidates = new List<Candidate>();
    private static DateTime _lastScanUtc = DateTime.MinValue;
    private static readonly object Gate = new object();

    public static bool TryRead(out string title, out string artist, out string processName, out int pid, out bool visible)
    {
        title = "";
        artist = "";
        processName = "";
        pid = 0;
        visible = false;

        lock (Gate)
        {
            if ((DateTime.UtcNow - _lastScanUtc).TotalSeconds > 2.0)
            {
                Scan();
                _lastScanUtc = DateTime.UtcNow;
            }

            if (Candidates.Count == 0) return false;

            // 正序遍历：候选已按「可见优先」排序，不能倒着走，否则会先命中隐藏的辅助窗口
            List<IntPtr> dead = null;
            for (var i = 0; i < Candidates.Count; i++)
            {
                var candidate = Candidates[i];
                if (!IsWindow(candidate.Handle))
                {
                    if (dead == null) dead = new List<IntPtr>();
                    dead.Add(candidate.Handle);
                    continue;
                }

                var raw = GetTitle(candidate.Handle);
                if (raw.Length == 0) continue;

                var cleaned = Clean(raw, candidate.Def);
                if (cleaned.Length == 0) continue;
                if (!TrySplit(cleaned, candidate.Def.ArtistFirst, out title, out artist)) continue;

                processName = candidate.Def.Process;
                pid = (int)candidate.Pid;
                visible = candidate.Visible;
                return true;
            }

            if (dead != null)
            {
                foreach (var handle in dead) Candidates.RemoveAll(c => c.Handle == handle);
            }

            // 有窗口但都没标题（可能播放器刚启动），下次立刻重扫
            if (Candidates.Count == 0) _lastScanUtc = DateTime.MinValue;
            return false;
        }
    }

    private static void Scan()
    {
        Candidates.Clear();

        EnumWindows((handle, _) =>
        {
            try
            {
                // 先看标题：没有标题的窗口占绝大多数，避免对每个窗口都查进程名
                if (GetTitle(handle).Length == 0) return true;

                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                if (pid == 0) return true;

                var name = GetProcessName(pid);
                if (name.Length == 0) return true;

                var def = FindDef(name);
                if (def == null) return true;

                Candidates.Add(new Candidate { Handle = handle, Def = def, Pid = pid, Visible = IsWindowVisible(handle) });
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        // 最小化到托盘的播放器窗口不可见但仍有标题，排在后面兜底
        Candidates.Sort((a, b) => b.Visible.CompareTo(a.Visible));
    }

    private static readonly Dictionary<uint, string> NameCache = new Dictionary<uint, string>();

    private static string GetProcessName(uint pid)
    {
        string cached;
        if (NameCache.TryGetValue(pid, out cached)) return cached;
        try
        {
            var name = Process.GetProcessById((int)pid).ProcessName;
            if (NameCache.Count > 128) NameCache.Clear();
            NameCache[pid] = name;
            return name;
        }
        catch
        {
            return "";
        }
    }

    private static PlayerDef FindDef(string processName)
    {
        var name = (processName ?? "").ToLowerInvariant();
        for (var i = 0; i < Known.Length; i++)
        {
            if (Known[i].Process == name) return Known[i];
        }
        return null;
    }

    private static string Clean(string raw, PlayerDef def)
    {
        var text = raw.Trim();

        foreach (var suffix in def.Strip)
        {
            var tail = " - " + suffix;
            if (text.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - tail.Length).Trim();
            if (string.Equals(text, suffix, StringComparison.OrdinalIgnoreCase))
                return "";
        }

        if (text.Length == 0) return "";
        if (string.Equals(text, "网易云音乐", StringComparison.OrdinalIgnoreCase)) return "";
        if (string.Equals(text, "QQ音乐", StringComparison.OrdinalIgnoreCase)) return "";
        if (IsJunkTitle(text)) return "";
        return text;
    }

    private static bool IsJunkTitle(string text)
    {
        if (text.StartsWith("GDI+ Window", StringComparison.Ordinal)) return true;
        if (text.StartsWith("MSCTFIME", StringComparison.Ordinal)) return true;
        if (string.Equals(text, "Default IME", StringComparison.Ordinal)) return true;
        if (string.Equals(text, "桌面歌词", StringComparison.Ordinal)) return true;
        if (string.Equals(text, "迷你播放器", StringComparison.Ordinal)) return true;
        if (text.EndsWith(".exe)", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TrySplit(string text, bool artistFirst, out string title, out string artist)
    {
        title = text.Trim();
        artist = "";

        var index = -1;
        var length = 0;
        for (var s = 0; s < Separators.Length; s++)
        {
            var i = text.IndexOf(Separators[s], StringComparison.Ordinal);
            if (i > 0 && (index < 0 || i < index))
            {
                index = i;
                length = Separators[s].Length;
            }
        }

        if (index <= 0) return title.Length > 0;

        var left = text.Substring(0, index).Trim();
        var right = text.Substring(index + length).Trim();
        if (left.Length == 0 || right.Length == 0) return left.Length > 0;

        if (artistFirst)
        {
            artist = left;
            title = right;
        }
        else
        {
            title = left;
            artist = right;
        }

        return title.Length > 0;
    }

    private static string GetTitle(IntPtr handle)
    {
        var buffer = new StringBuilder(512);
        try
        {
            GetWindowText(handle, buffer, buffer.Capacity);
        }
        catch { return ""; }
        return buffer.ToString().Trim();
    }

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    public static bool Next()
    {
        return Press(VK_MEDIA_NEXT_TRACK);
    }

    public static bool Previous()
    {
        return Press(VK_MEDIA_PREV_TRACK);
    }

    private static bool Press(byte vk)
    {
        try
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
