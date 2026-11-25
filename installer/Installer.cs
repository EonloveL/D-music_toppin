using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    internal const string AppName = "Music Toppin";
    internal const string AppExe = "MusicToppin.exe";
    internal const string Version = "1.0.0";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicToppin";

    internal static string DefaultInstallDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "MusicToppin");
        }
    }

    /// <summary>升级时沿用上次装的位置，否则用默认目录。</summary>
    internal static string SuggestInstallDir()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var existing = key == null ? null : key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }
        }
        catch { }
        return DefaultInstallDir;
    }

    internal static string ParseDirArgument(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring(5).Trim().Trim('"');
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = Array.Exists(args, a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        var dirArgument = ParseDirArgument(args);
        var targetDir = string.IsNullOrWhiteSpace(dirArgument) ? SuggestInstallDir() : dirArgument;

        if (silent)
        {
            try
            {
                DoInstall(targetDir, null);
                return 0;
            }
            catch (Exception ex)
            {
                Fail(ex, silent: true);
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Fail(e.Exception, silent: false);
        Application.Run(new InstallForm(targetDir));
        return 0;
    }

    internal static void Fail(Exception ex, bool silent = false)
    {
        var log = Path.Combine(Path.GetTempPath(), "musictoppin-install-error.log");
        try { File.WriteAllText(log, ex.ToString()); } catch { }

        if (silent) return;
        MessageBox.Show("安装失败：" + ex.Message + "\n\n日志：" + log, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    internal static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>目录需要管理员权限时，用 runas 重新拉起自己（带上选中的目录）。</summary>
    internal static bool RelaunchElevated(string dir)
    {
        try
        {
            var exe = Application.ExecutablePath;
            var info = new ProcessStartInfo(exe, "/DIR=\"" + dir + "\"")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void DoInstall(string targetDir, IProgress<string> status)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) targetDir = DefaultInstallDir;
        targetDir = Path.GetFullPath(targetDir.Trim().TrimEnd('\\'));

        Report(status, "正在关闭已运行的程序…");
        KillRunning();

        Report(status, "正在解压文件…");
        CleanDirectory(targetDir);
        Directory.CreateDirectory(targetDir);

        var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (payload == null) throw new InvalidOperationException("安装包数据缺失");

        var tempZip = Path.Combine(Path.GetTempPath(), "musictoppin-payload.zip");
        using (var fs = File.Create(tempZip)) payload.CopyTo(fs);
        payload.Dispose();

        try
        {
            ZipFile.ExtractToDirectory(tempZip, targetDir);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }

        // 清掉历史残留（例如被占用后改名遗留的 {guid}_MusicToppin.exe）
        try
        {
            var keep = new[] { AppExe, "Uninstall.exe" };
            foreach (var file in Directory.GetFiles(targetDir))
            {
                var name = Path.GetFileName(file);
                if (Array.IndexOf(keep, name) >= 0) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch { }

        Report(status, "正在创建快捷方式…");
        var exe = Path.Combine(targetDir, AppExe);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exe);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"), exe);

        Report(status, "正在注册卸载信息…");
        WriteUninstallRegistry(exe, targetDir);

        Report(status, "安装完成");
    }

    private static void Report(IProgress<string> progress, string text)
    {
        if (progress != null) progress.Report(text);
    }

    private static void CleanDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            return;
        }
        catch { }

        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            try { Directory.Delete(sub, true); } catch { }
        }
    }

    internal static void KillRunning()
    {
        foreach (var p in Process.GetProcessesByName("MusicToppin"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    internal static void CreateShortcut(string lnkPath, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type);
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = AppName;
            lnk.Save();
        }
        catch { }
    }

    internal static void WriteUninstallRegistry(string exe, string installDir)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            if (key == null) return;
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", AppName);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "Uninstall.exe") + "\"");
            key.SetValue("QuietUninstallString", "\"" + Path.Combine(installDir, "Uninstall.exe") + "\" /S");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try
            {
                long size = 0;
                foreach (var f in new DirectoryInfo(installDir).GetFiles("*", SearchOption.AllDirectories))
                    size += f.Length;
                key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}

internal sealed class InstallForm : Form
{
    private readonly TextBox _path;
    private readonly Button _action;
    private readonly Button _close;
    private readonly Button _browse;
    private readonly ProgressBar _bar;
    private readonly Label _status;
    private readonly CheckBox _run;
    private bool _done;
    private volatile bool _installing;

    public InstallForm(string suggestedDir)
    {
        Text = Program.AppName + " 安装程序";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 320);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);

        var title = new Label
        {
            Text = Program.AppName,
            Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(28, 22)
        };

        var subtitle = new Label
        {
            Text = "在游戏和直播画面上，用击杀播报的方式显示系统正在播放的歌。",
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = true,
            Location = new Point(30, 64)
        };

        var pathLabel = new Label
        {
            Text = "安装到（可以改成 D 盘等其他位置）",
            ForeColor = Color.FromArgb(80, 86, 98),
            AutoSize = true,
            Location = new Point(30, 100)
        };

        _path = new TextBox
        {
            Text = suggestedDir,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 122),
            Width = 400,
            BackColor = Color.White
        };

        _browse = new Button
        {
            Text = "浏览…",
            Location = new Point(438, 120),
            Size = new Size(92, 26),
            FlatStyle = FlatStyle.System
        };
        _browse.Click += (_, _) => BrowseFolder();

        var hint = new Label
        {
            Text = "装到 Program Files 等受保护目录时，会提示以管理员身份重新运行。",
            ForeColor = Color.FromArgb(150, 156, 168),
            AutoSize = true,
            Location = new Point(30, 152)
        };

        _bar = new ProgressBar
        {
            Location = new Point(30, 190),
            Width = 500,
            Height = 8,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        _status = new Label
        {
            Text = "点击「开始安装」，默认装到当前用户目录，不需要管理员权限。",
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = false,
            Location = new Point(30, 204),
            Width = 500,
            Height = 22
        };

        _run = new CheckBox
        {
            Text = "安装完成后立即启动",
            Checked = true,
            AutoSize = true,
            Location = new Point(30, 234),
            Visible = false
        };

        _action = new Button
        {
            Text = "开始安装",
            Location = new Point(398, 264),
            Size = new Size(128, 34),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _action.FlatAppearance.BorderSize = 0;
        _action.Click += (_, _) => OnAction();

        _close = new Button
        {
            Text = "取消",
            Location = new Point(312, 264),
            Size = new Size(78, 34),
            FlatStyle = FlatStyle.System
        };
        _close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, subtitle, pathLabel, _path, _browse, hint, _bar, _status, _run, _action, _close });

        FormClosing += (_, e) =>
        {
            if (_installing && !_done) e.Cancel = true;
        };
    }

    private void BrowseFolder()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "选择 " + Program.AppName + " 的安装位置";
            dialog.ShowNewFolderButton = true;
            try
            {
                var current = _path.Text.Trim();
                if (current.Length > 0)
                {
                    var parent = Path.GetDirectoryName(current);
                    dialog.SelectedPath = Directory.Exists(current) ? current : (parent ?? current);
                }
            }
            catch { }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                // 选中的目录下再建一层应用名，避免直接装进用户选中的文件夹根
                _path.Text = Path.Combine(dialog.SelectedPath, Program.AppName);
            }
        }
    }

    private void OnAction()
    {
        if (_done)
        {
            if (_run.Checked) StartApp(_path.Text.Trim());
            Close();
            return;
        }

        string target;
        try
        {
            target = Path.GetFullPath(_path.Text.Trim().TrimEnd('\\'));
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装路径无效：" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (target.Length < 4 || target == Path.GetPathRoot(target))
        {
            MessageBox.Show("请不要直接装到磁盘根目录，选一个文件夹。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Program.IsDirectoryWritable(target))
        {
            var answer = MessageBox.Show(
                "这个目录需要管理员权限才能写入：\n" + target + "\n\n是否以管理员身份重新运行安装程序？\n（也可以换一个普通目录，例如 D:\\MusicToppin）",
                Program.AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
            {
                if (!Program.RelaunchElevated(target))
                {
                    MessageBox.Show("提权失败，请右键安装包选择「以管理员身份运行」。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Close();
            }
            return;
        }

        _installing = true;
        _path.ReadOnly = true;
        _browse.Enabled = false;
        _action.Enabled = false;
        _close.Enabled = false;
        _bar.Visible = true;
        _bar.Style = ProgressBarStyle.Marquee;

        var progress = new Progress<string>(text => _status.Text = text);

        var thread = new Thread(() =>
        {
            try
            {
                Program.DoInstall(target, progress);
                Post(Finish);
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    _installing = false;
                    Program.Fail(ex);
                    _path.ReadOnly = false;
                    _browse.Enabled = true;
                    _action.Enabled = true;
                    _close.Enabled = true;
                    _bar.Visible = false;
                    _status.Text = "安装失败，详情见日志。";
                });
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>窗体可能已被关闭，回调前必须确认句柄还在。</summary>
    private void Post(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch { }
    }

    private void Finish()
    {
        _done = true;
        _installing = false;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 100;
        _status.Text = "安装完成：" + _path.Text.Trim();
        _run.Visible = true;
        _action.Text = "完成";
        _action.Enabled = true;
        _close.Enabled = true;
        _close.Text = "关闭";
        if (_run.Checked) StartApp(_path.Text.Trim());
    }

    private static void StartApp(string dir)
    {
        try
        {
            var exe = Path.Combine(dir, Program.AppExe);
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = dir
            });
        }
        catch { }
    }
}
