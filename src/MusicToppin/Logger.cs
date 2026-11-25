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
                    File.Delete(path);
                }
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}
