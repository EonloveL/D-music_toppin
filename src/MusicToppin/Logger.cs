namespace MusicToppin;

/// <summary>极简日志：写到 %TEMP%\musictoppin.log，超过 512KB 自动截断。</summary>
internal static class Logger
{
    private static readonly object Gate = new object();

    public static string FilePath
    {
        get { return Path.Combine(Path.GetTempPath(), "musictoppin.log"); }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    // 轮转一份历史，别把证据直接删掉
                    try
                    {
                        var previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    catch { try { File.Delete(path); } catch { } }
                }

                // 弹幕原文可能带换行：转义掉，避免伪造日志行
                var text = (message ?? "")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");

                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            }
        }
        catch { }
    }
}
