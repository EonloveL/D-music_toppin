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

        foreach (var p in Process.GetProcessesByName("MusicToppin"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));

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

        if (!silent)
        {
            MessageBox.Show("已卸载。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 自删除：cmd 等本进程退出后清目录（提权运行时 cmd 也继承管理员权限）
        try
        {
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
