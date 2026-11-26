using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class UninstallProgram
{
    private const string AppName = "Music Toppin";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicToppin";

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = Array.Exists(args, a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        bool elevated = Array.Exists(args, a => a.Equals("/ELEVATED", StringComparison.OrdinalIgnoreCase));

        if (!silent)
        {
            var answer = MessageBox.Show(
                "确定要卸载 " + AppName + " 吗？\n\n（%HKCU%\\Software\\MusicToppin 里的颜色/位置等设置也会一并清除）",
                AppName + " 卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return 0;
        }

        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (!CanWriteDirectory(dir))
            {
                if (!elevated && RelaunchElevated(silent)) return 0;

                const string permissionMessage = "没有权限删除安装目录，请右键卸载程序选择「以管理员身份运行」。";
                WriteLog(permissionMessage);
                if (!silent) MessageBox.Show(permissionMessage, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Run(silent);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            // 装在 Program Files 这类受保护目录时，普通权限删不掉
            if (!elevated && RelaunchElevated(silent))
            {
                return 0;
            }

            const string message = "没有权限删除安装目录，请右键卸载程序选择「以管理员身份运行」。";
            WriteLog(message);
            if (!silent) MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
            if (!silent) MessageBox.Show("卸载失败：" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Run(bool silent)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        // 只允许卸载真正的安装目录：装有本产品 MusicToppin.exe，或注册表登记的 InstallLocation。
        // 卸载器被复制/挪到别处运行时直接退出，避免误删任意目录。
        if (!IsOurInstallDir(dir))
        {
            WriteLog("拒绝卸载：不是 Music Toppin 安装目录：" + dir);
            if (!silent)
            {
                MessageBox.Show("这个目录不是 Music Toppin 的安装目录，已停止卸载。", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        foreach (var p in Process.GetProcessesByName("MusicToppin"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));

        // 先确认安装目录里的文件能删掉，再删卸载注册项；否则用户会在「设置→应用」里失去重试入口
        if (!TryDeleteInstallFiles(dir))
        {
            throw new UnauthorizedAccessException("安装目录里有文件删不掉：" + dir);
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        catch { }

        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null) key.DeleteValue("MusicToppin", false);
            }
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\MusicToppin", false);
        }
        catch { }

        // 旧版本留下的数据目录（历史遗留）
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicToppin");
            if (Directory.Exists(appData)) Directory.Delete(appData, true);
        }
        catch { }

        // WebView2 播放器/登录窗口的数据目录（通常几十 MB）
        try
        {
            var localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicToppin");
            if (Directory.Exists(localData)) Directory.Delete(localData, true);
        }
        catch { }

        if (!silent)
        {
            MessageBox.Show("已卸载。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 自删除：cmd 等本进程退出后清目录（提权运行时 cmd 也继承管理员权限）
        try
        {
            var root = Path.GetPathRoot(dir);
            var isRoot = !string.IsNullOrEmpty(root) &&
                         string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (isRoot)
            {
                WriteLog("跳过删除目录（磁盘根）：" + dir);
                return;
            }

            var command = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
        }
        catch { }
    }

    /// <summary>安装目录判定：必须有本产品 exe，或与卸载注册项登记的 InstallLocation 一致。</summary>
    private static bool IsOurInstallDir(string dir)
    {
        try
        {
            var exe = Path.Combine(dir, "MusicToppin.exe");
            if (File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (string.Equals((info.ProductName ?? "").Trim(), AppName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var location = key == null ? null : key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(location))
                {
                    var a = Path.GetFullPath(location).TrimEnd('\\');
                    var b = Path.GetFullPath(dir).TrimEnd('\\');
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool CanWriteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            var probe = Path.Combine(dir, ".uninstall-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RelaunchElevated(bool silent)
    {
        try
        {
            var exe = Application.ExecutablePath;
            var arguments = "/ELEVATED" + (silent ? " /S" : "");
            Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "musictoppin-uninstall-error.log"),
                DateTime.Now + "  " + message);
        }
        catch { }
    }

    private static bool TryDeleteInstallFiles(string dir)
    {
        if (!Directory.Exists(dir)) return true;

        var ok = true;
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (string.Equals(Path.GetFileName(file), "Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch { ok = false; }
            }

            foreach (var sub in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { ok = false; }
            }
        }
        catch
        {
            ok = false;
        }

        return ok;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
