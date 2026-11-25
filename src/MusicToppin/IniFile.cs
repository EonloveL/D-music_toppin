namespace MusicToppin;

/// <summary>极简 ini 读取（只支持 [section] / key=value / ; 注释）。
/// QQ 音乐、酷狗把登录账号写在 ini 里，值都是纯文本。</summary>
internal static class IniFile
{
    public static string Read(string path, string section, string key)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            return Parse(File.ReadAllText(path), section, key);
        }
        catch (Exception ex)
        {
            Logger.Write("ini: " + ex.Message);
            return "";
        }
    }

    /// <summary>section 传空表示不限段落。</summary>
    public static string Parse(string text, string section, string key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return "";

        var current = "";
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (section.Length > 0 && !string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            if (!string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;

            return line.Substring(equals + 1).Trim();
        }

        return "";
    }
}
