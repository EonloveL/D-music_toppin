using System.Net;
using System.Text;

namespace MusicToppin;

internal sealed class VideoCacheEntry
{
    public string Bvid = "";
    public string Path = "";
    public long Total;
    public long Downloaded;
    public bool Complete;
    public bool Failed;
    public string Error = "";
    public DateTime StartedUtc = DateTime.UtcNow;

    public readonly object Gate = new object();
}

/// <summary>
/// 视频点播的本地缓存：解析到 MP4 后立刻后台下载到 %TEMP%，叠加层页面从本地文件读，
/// 这样游戏抢带宽时只会影响“下载进度”，不会直接卡住播放（磁盘缓冲吸收抖动）。
/// </summary>
internal static class VideoCache
{
    private const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;   // 最多占 2GB 临时空间
    private const long BytesPerSecond = 3L * 1024 * 1024;        // 下载限速 3MB/s，避免抢游戏/直播带宽
    private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1, 1);

    private static readonly object Sync = new object();
    private static readonly Dictionary<string, VideoCacheEntry> Entries = new Dictionary<string, VideoCacheEntry>(StringComparer.Ordinal);

    public static string DirectoryPath
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "MusicToppin", "video");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public static VideoCacheEntry Get(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return null;

        lock (Sync)
        {
            VideoCacheEntry entry;
            if (Entries.TryGetValue(bvid, out entry)) return entry;

            entry = new VideoCacheEntry
            {
                Bvid = bvid,
                Path = Path.Combine(DirectoryPath, bvid + ".mp4.part")
            };
            Entries[bvid] = entry;

            ThreadPool.QueueUserWorkItem(_ => Download(entry));
            return entry;
        }
    }

    public static void Prefetch(string bvid)
    {
        Get(bvid);
    }

    private static void Download(VideoCacheEntry entry)
    {
        DownloadGate.Wait();
        try
        {
            var stream = Bilibili.ResolveVideoStream(entry.Bvid);
            if (stream == null || string.IsNullOrEmpty(stream.Url))
            {
                Fail(entry, "解析失败");
                return;
            }

            var request = (HttpWebRequest)WebRequest.Create(stream.Url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            request.Referer = "https://www.bilibili.com/";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = true;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var remote = response.GetResponseStream())
            using (var file = new FileStream(entry.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                entry.Total = response.ContentLength;

                var buffer = new byte[128 * 1024];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long written = 0;
                int read;
                while ((read = remote.Read(buffer, 0, buffer.Length)) > 0)
                {
                    file.Write(buffer, 0, read);
                    written += read;

                    lock (entry.Gate)
                    {
                        entry.Downloaded += read;
                        Monitor.PulseAll(entry.Gate);
                    }

                    // 限速：按平均速率 sleep，避免下载突发占满带宽
                    var expectedMs = written * 1000 / BytesPerSecond;
                    var actualMs = clock.ElapsedMilliseconds;
                    if (expectedMs > actualMs + 50)
                    {
                        Thread.Sleep((int)Math.Min(200, expectedMs - actualMs));
                    }
                }

                file.Flush();
            }

            entry.Complete = true;
            Logger.Write("video cache: " + entry.Bvid + " 完成 " + (entry.Downloaded / 1024 / 1024) + " MB");
        }
        catch (Exception ex)
        {
            Fail(entry, ex.Message);
        }
        finally
        {
            lock (entry.Gate) Monitor.PulseAll(entry.Gate);
            DownloadGate.Release();
            TrimToLimit();
        }
    }

    /// <summary>超过 2GB 就删最旧的缓存（跳过正在下载的）。</summary>
    public static void TrimToLimit()
    {
        try
        {
            var files = new DirectoryInfo(DirectoryPath).GetFiles("*.part");
            var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = ordered.Sum(f => f.Length);

            foreach (var file in ordered)
            {
                if (total <= MaxCacheBytes) break;

                var busy = Entries.Values.Any(e => string.Equals(e.Path, file.FullName, StringComparison.OrdinalIgnoreCase) && !e.Complete && !e.Failed);
                if (busy) continue;

                try
                {
                    total -= file.Length;
                    file.Delete();
                }
                catch { }
            }
        }
        catch { }
    }

    private static void Fail(VideoCacheEntry entry, string error)
    {
        entry.Failed = true;
        entry.Error = error;
        Logger.Write("video cache: " + entry.Bvid + " 失败：" + error);
        lock (entry.Gate) Monitor.PulseAll(entry.Gate);
    }

    /// <summary>启动时清掉两天前的缓存，避免临时目录膨胀。</summary>
    public static void CleanupOld()
    {
        try
        {
            var dir = DirectoryPath;
            foreach (var file in Directory.GetFiles(dir, "*.part"))
            {
                var info = new FileInfo(file);
                if (info.Exists && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays > 2)
                {
                    try { info.Delete(); } catch { }
                }
            }
        }
        catch { }
    }
}
