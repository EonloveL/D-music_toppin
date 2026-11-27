using System.Net;
using System.Text;

namespace MusicToppin;

internal sealed class VideoCacheEntry
{
    public string Bvid = "";
    public string Path = "";
    public long Total;
    public long Downloaded;
    public volatile bool Complete;
    public volatile bool Failed;
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

    /// <summary>BV 号大小写敏感、Windows 文件名不敏感：用 hex 编码做文件名，避免大小写变体截断同一份缓存。</summary>
    private static string FileNameFor(string bvid)
    {
        var bytes = Encoding.UTF8.GetBytes(bvid);
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex + ".mp4.part";
    }

    public static VideoCacheEntry Get(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return null;

        lock (Sync)
        {
            VideoCacheEntry entry;
            if (Entries.TryGetValue(bvid, out entry))
            {
                // 失败过、或文件已被清掉：按新任务重建，避免永久黑屏/空等
                if (entry.Failed || (entry.Complete && !File.Exists(entry.Path)))
                {
                    Entries.Remove(bvid);
                }
                else
                {
                    return entry;
                }
            }

            entry = new VideoCacheEntry
            {
                Bvid = bvid,
                Path = Path.Combine(DirectoryPath, FileNameFor(bvid))
            };
            Entries[bvid] = entry;

            ThreadPool.QueueUserWorkItem(_ => Download(entry));
            return entry;
        }
    }

    public static void Remove(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return;
        lock (Sync) Entries.Remove(bvid);
    }

    /// <summary>只在字典里仍是同一个任务时才移除，避免删掉刚重建的新任务。</summary>
    public static void RemoveIfSame(string bvid, VideoCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(bvid) || entry == null) return;
        lock (Sync)
        {
            VideoCacheEntry current;
            if (Entries.TryGetValue(bvid, out current) && ReferenceEquals(current, entry))
            {
                Entries.Remove(bvid);
            }
        }
    }

    private static void RemoveByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (Sync)
        {
            List<string> stale = null;
            foreach (var pair in Entries)
            {
                if (string.Equals(pair.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(pair.Key);
                }
            }

            if (stale != null)
            {
                foreach (var key in stale) Entries.Remove(key);
            }
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

            // 只接受 B 站 CDN 的 https 直链，避免被污染成 file:// 等 scheme
            Uri uri;
            if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
            {
                Fail(entry, "视频地址不合法");
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
                lock (entry.Gate) entry.Total = response.ContentLength;

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

                // 完整性校验：有 Content-Length 时必须读满；无长度时用实际字节数
                if (entry.Total <= 0)
                {
                    lock (entry.Gate) entry.Total = written;
                }
                else if (written != entry.Total)
                {
                    throw new IOException("下载不完整 " + written + "/" + entry.Total);
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
            HashSet<string> busy;
            lock (Sync)
            {
                busy = new HashSet<string>(
                    Entries.Values
                        .Where(e => !e.Complete && !e.Failed)
                        .Select(e => e.Path),
                    StringComparer.OrdinalIgnoreCase);
            }

            var files = new DirectoryInfo(DirectoryPath).GetFiles("*.part");
            var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = ordered.Sum(f => f.Length);

            foreach (var file in ordered)
            {
                if (total <= MaxCacheBytes) break;
                if (busy.Contains(file.FullName)) continue;

                try
                {
                    total -= file.Length;
                    file.Delete();
                    RemoveByPath(file.FullName);
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

        // 失败条目不要留在字典里，否则之后每次都把部分文件当完整视频播
        lock (Sync) Entries.Remove(entry.Bvid);
        TryDelete(entry.Path);

        lock (entry.Gate) Monitor.PulseAll(entry.Gate);
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
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
                    try
                    {
                        info.Delete();
                        RemoveByPath(info.FullName);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
