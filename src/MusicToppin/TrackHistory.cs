using System.Text;
using Microsoft.Win32;

namespace MusicToppin;

/// <summary>
/// 曲目历史：SMTC 协议不提供播放列表，只能自己记录「听过什么」，
/// 用来填 HUD 的「上一首」。持久化到注册表，重启后依然有效。
/// </summary>
internal static class TrackHistory
{
    private const string KeyPath = @"Software\MusicToppin";
    private const string ValueName = "RecentTracks";
    private const int Limit = 10;

    private static readonly List<string[]> Items = new List<string[]>();

    public static void Load()
    {
        Items.Clear();
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                var raw = key == null ? null : key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(raw)) return;

                foreach (var line in raw.Split('\n'))
                {
                    var parts = line.Split('\u0001');
                    if (parts.Length >= 2 && parts[0].Trim().Length > 0) Items.Add(parts);
                    if (Items.Count >= Limit) break;
                }
            }
        }
        catch { }
    }

    /// <summary>记录一首「已经播完」的歌（新记录插到最前面）。</summary>
    public static void Push(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (Items.Count > 0 && string.Equals(Items[0][0], title, StringComparison.Ordinal)) return;

        Items.Insert(0, new[] { title, artist ?? "" });
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
        Save();
    }

    /// <summary>取最近 n 首（索引 0 = 最近），返回实际条数。</summary>
    public static int Snapshot(string[] titles, string[] artists)
    {
        if (titles == null || artists == null) return 0;
        var count = Math.Min(Items.Count, Math.Min(titles.Length, artists.Length));
        for (var i = 0; i < count; i++)
        {
            titles[i] = Items[i][0];
            artists[i] = Items[i].Length > 1 ? Items[i][1] : "";
        }
        return count;
    }

    public static bool TryPeek(out string title, out string artist)
    {
        title = "";
        artist = "";
        if (Items.Count == 0) return false;
        title = Items[0][0];
        artist = Items[0].Length > 1 ? Items[0][1] : "";
        return title.Trim().Length > 0;
    }

    private static void Save()
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var item in Items)
            {
                builder.Append(item[0]).Append('\u0001').Append(item.Length > 1 ? item[1] : "").Append('\n');
            }

            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key != null) key.SetValue(ValueName, builder.ToString(), RegistryValueKind.String);
            }
        }
        catch { }
    }
}
