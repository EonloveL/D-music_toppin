using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicToppin;

internal sealed class UpdateInfo
{
    public Version Version;
    public string VersionText;
    public string Url;
    public string Sha256;
    public string Notes;
}

/// <summary>
/// 更新检查：读一个 JSON 清单（自建地址或 GitHub Releases API），比较版本，下载并校验后静默升级。
/// 只发一个 GET 请求，不上报任何信息。
/// </summary>
internal static class Updater
{
    /// <summary>
    /// 发布版把这里改成自己的清单地址（或 GitHub Releases API），用户就不需要配置。
    /// 例：https://example.com/musictoppin/update.json
    /// </summary>
    public const string DefaultManifestUrl = "https://api.github.com/repos/EonloveL/D-music_toppin/releases/latest";

    private static readonly Regex KeyString = new Regex(
        "\"([A-Za-z0-9_]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version ?? new Version(1, 0, 0, 0);
            }
            catch
            {
                return new Version(1, 0, 0, 0);
            }
        }
    }

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Build >= 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor;
        }
    }

    /// <summary>取清单并解析；没有更新返回 null。异常向上抛，由调用方决定怎么提示。</summary>
    public static UpdateInfo Check(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;

        var json = HttpGet(manifestUrl, 12000);
        if (string.IsNullOrWhiteSpace(json)) return null;

        var info = ParseManifest(json);
        if (info == null || string.IsNullOrEmpty(info.Url)) return null;

        if (info.Version == null) return null;
        if (info.Version <= CurrentVersion) return null;
        if (string.IsNullOrWhiteSpace(info.VersionText)) info.VersionText = info.Version.ToString();
        return info;
    }

    /// <summary>兼容两种清单：自定义 {version,url,sha256,notes} 与 GitHub Releases API。</summary>
    private static UpdateInfo ParseManifest(string json)
    {
        // GitHub Releases API 有 assets 数组：必须按 asset 名匹配 Setup.exe，
        // 不能把所有键拉平后取第一个（否则可能选中 SHA256SUMS.txt 或 digest 与 URL 不匹配）
        var github = ParseGitHubManifest(json);
        if (github != null) return github;

        // 有 assets 数组但没匹配到 Setup.exe：拒绝，避免把 SHA256SUMS.txt 之类当安装包
        if (json.IndexOf("\"assets\"", StringComparison.Ordinal) >= 0) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in KeyString.Matches(json))
        {
            var key = match.Groups[1].Value;
            if (!map.ContainsKey(key)) map[key] = Unescape(match.Groups[2].Value);
        }

        string versionText;
        if (!map.TryGetValue("tag_name", out versionText)) map.TryGetValue("version", out versionText);
        if (string.IsNullOrWhiteSpace(versionText)) return null;

        versionText = versionText.Trim();
        if (versionText.StartsWith("v", StringComparison.OrdinalIgnoreCase)) versionText = versionText.Substring(1);

        Version version;
        if (!Version.TryParse(NormalizeVersion(versionText), out version)) return null;

        string url;
        // GitHub 的 assets 里也有 "url"（API 地址），优先取下载直链
        if (!map.TryGetValue("browser_download_url", out url)) map.TryGetValue("url", out url);

        string sha256;
        if (!map.TryGetValue("sha256", out sha256) && map.TryGetValue("digest", out sha256))
        {
            // GitHub Releases API 的 asset digest 形如 "sha256:abcd…"
            if (!string.IsNullOrWhiteSpace(sha256) &&
                sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = sha256.Substring("sha256:".Length);
            }
        }
        string notes;
        if (!map.TryGetValue("notes", out notes)) map.TryGetValue("body", out notes);

        return new UpdateInfo
        {
            Version = version,
            VersionText = versionText,
            Url = url,
            Sha256 = sha256,
            Notes = notes
        };
    }

    private static UpdateInfo ParseGitHubManifest(string json)
    {
        try
        {
            var root = Json.Parse(json);
            if (root == null) return null;

            var assets = root["assets"];
            if (assets == null || assets.Kind != JsonKind.Array || assets.Count == 0) return null;

            var versionText = root["tag_name"] != null ? (root["tag_name"].AsString ?? "") : "";
            if (string.IsNullOrWhiteSpace(versionText)) return null;
            versionText = versionText.Trim();
            if (versionText.StartsWith("v", StringComparison.OrdinalIgnoreCase)) versionText = versionText.Substring(1);

            Version version;
            if (!Version.TryParse(NormalizeVersion(versionText), out version)) return null;

            JsonValue chosen = null;
            for (var i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                if (asset == null) continue;
                var name = asset["name"] != null ? (asset["name"].AsString ?? "") : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    name.IndexOf("MusicToppin-Setup", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chosen = asset;
                    break;
                }
            }

            if (chosen == null) return null;

            var url = chosen["browser_download_url"] != null ? (chosen["browser_download_url"].AsString ?? "") : "";
            if (string.IsNullOrWhiteSpace(url)) return null;

            var sha256 = chosen["digest"] != null ? (chosen["digest"].AsString ?? "") : "";
            if (!string.IsNullOrWhiteSpace(sha256) &&
                sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = sha256.Substring("sha256:".Length);
            }

            var notes = root["body"] != null ? (root["body"].AsString ?? "") : "";

            return new UpdateInfo
            {
                Version = version,
                VersionText = versionText,
                Url = url,
                Sha256 = sha256,
                Notes = notes
            };
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string text)
    {
        // "1.0.1" -> "1.0.1"；"1.0" -> "1.0.0"
        var parts = text.Split('.');
        if (parts.Length == 2) return text + ".0";
        return text;
    }

    private static string Unescape(string value)
    {
        return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "");
    }

    private static string HttpGet(string url, int timeoutMs)
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "MusicToppin-Updater/" + CurrentVersionText;
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.AllowAutoRedirect = true;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

        using (var response = (HttpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    /// <summary>下载安装包，返回本地路径。</summary>
    public static string Download(UpdateInfo info, IProgress<int> progress)
    {
        Uri uri;
        if (info == null || string.IsNullOrWhiteSpace(info.Url) ||
            !Uri.TryCreate(info.Url, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("更新地址无效");
        }
        var fileName = "MusicToppin-Setup-" + info.VersionText + ".exe";
        var path = Path.Combine(Path.GetTempPath(), fileName);

        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

        var request = (HttpWebRequest)WebRequest.Create(info.Url);
        request.UserAgent = "MusicToppin-Updater/" + CurrentVersionText;
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        request.AllowAutoRedirect = true;

        using (var response = (HttpWebResponse)request.GetResponse())
        using (var remote = response.GetResponseStream())
        using (var local = File.Create(path))
        {
            var total = response.ContentLength;
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = remote.Read(buffer, 0, buffer.Length)) > 0)
            {
                local.Write(buffer, 0, read);
                readTotal += read;
                if (progress != null && total > 0)
                {
                    progress.Report((int)(readTotal * 100 / total));
                }
            }
        }

        return path;
    }

    public static bool VerifySha256(string filePath, string expected)
    {
        // 失败即拒绝：清单没给校验值就不自动安装（GitHub API 的 digest 字段会在 ParseManifest 里补上）
        if (string.IsNullOrWhiteSpace(expected))
        {
            Logger.Write("update: 清单没有 sha256，拒绝自动安装");
            return false;
        }

        try
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2"));
                return string.Equals(builder.ToString(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    // ---- 结束 ----

    /// <summary>
    /// 用一个延迟批处理完成「等本进程退出 → 静默安装 → 重新拉起」。
    /// 安装器会杀掉正在运行的 MusicToppin，所以必须先退出再交给它。
    /// </summary>
    public static void ApplyAndRestart(string setupPath)
    {
        var appPath = AppPaths.ExePath;
        var installDir = Path.GetDirectoryName(appPath) ?? Path.GetTempPath();
        var exePath = Path.Combine(installDir, "MusicToppin.exe");
        var script = Path.Combine(Path.GetTempPath(), "musictoppin-update.cmd");

        // 路径通过环境变量传给 cmd（Unicode，不受批处理 ANSI/8.3 短名限制）
        var content =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "\"%MT_SETUP%\" /S /DIR=\"%MT_DIR%\"\r\n" +
            "if errorlevel 1 (\r\n" +
            "  echo update failed %date% %time% > \"%TEMP%\\musictoppin-update-error.log\"\r\n" +
            "  start \"\" \"%MT_APP%\"\r\n" +
            "  del \"%~f0\"\r\n" +
            "  exit /b\r\n" +
            ")\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            "start \"\" \"%MT_EXE%\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(script, content, Encoding.ASCII);

        var info = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath()
        };
        info.EnvironmentVariables["MT_SETUP"] = setupPath;
        info.EnvironmentVariables["MT_DIR"] = installDir;
        info.EnvironmentVariables["MT_APP"] = appPath;
        info.EnvironmentVariables["MT_EXE"] = exePath;

        Process.Start(info);
    }
}
