namespace MusicToppin;

/// <summary>发现新版本时的确认窗：版本对比 + 更新说明 + 下载进度。</summary>
internal sealed class UpdateDialog : Form
{
    private readonly UpdateInfo _info;
    private readonly Action _onInstall;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly Button _primary;
    private readonly Button _secondary;
    private bool _busy;

    public UpdateDialog(UpdateInfo info, Action onInstall)
    {
        _info = info;
        _onInstall = onInstall;

        Text = "发现新版本 · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 300);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        TopMost = true;

        var title = new Label
        {
            Text = "发现新版本 " + info.VersionText,
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(22, 18)
        };

        var subtitle = new Label
        {
            Text = "当前版本 " + Updater.CurrentVersionText + "　→　新版本 " + info.VersionText,
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = true,
            Location = new Point(24, 52)
        };

        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Location = new Point(24, 80),
            Size = new Size(472, 130),
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "（此版本没有提供更新说明）" : info.Notes
        };

        _bar = new ProgressBar
        {
            Location = new Point(24, 220),
            Size = new Size(472, 10),
            Visible = false
        };

        _status = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(110, 116, 128),
            AutoSize = false,
            Location = new Point(24, 236),
            Size = new Size(472, 20)
        };

        _primary = new Button
        {
            Text = "立即更新",
            Location = new Point(366, 258),
            Size = new Size(130, 32),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _primary.FlatAppearance.BorderSize = 0;
        _primary.Click += (_, _) => StartInstall();

        _secondary = new Button
        {
            Text = "稍后",
            Location = new Point(276, 258),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.System
        };
        _secondary.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, subtitle, notes, _bar, _status, _primary, _secondary });

    }

    private void StartInstall()
    {
        if (_busy) return;
        _busy = true;

        _primary.Enabled = false;
        _secondary.Enabled = false;
        _bar.Visible = true;
        _status.Text = "正在下载…";

        var thread = new Thread(() =>
        {
            try
            {
                var progress = new Progress<int>(percent =>
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    try { _bar.Value = Math.Max(0, Math.Min(100, percent)); } catch { }
                });

                var path = Updater.Download(_info, progress);

                Post(() => _status.Text = "正在校验安装包…");
                if (!Updater.VerifySha256(path, _info.Sha256))
                {
                    try { File.Delete(path); } catch { }
                    Post(() =>
                    {
                        _busy = false;
                        _bar.Visible = false;
                        _primary.Enabled = true;
                        _secondary.Enabled = true;
                        _status.Text = "安装包校验失败，已丢弃。请稍后重试或手动下载。";
                    });
                    return;
                }

                Post(() =>
                {
                    _status.Text = "正在安装，程序会自动重启…";
                    _bar.Value = 100;
                });

                // 交给延迟批处理：等本进程退出 → /S 安装 → 重新拉起
                Updater.ApplyAndRestart(path);
                Thread.Sleep(300);

                Post(() =>
                {
                    if (_onInstall != null) _onInstall();
                });
            }
            catch (Exception ex)
            {
                Logger.Write("update download: " + ex.Message);
                Post(() =>
                {
                    _busy = false;
                    _bar.Visible = false;
                    _primary.Enabled = true;
                    _secondary.Enabled = true;
                    _status.Text = "下载失败：" + ex.Message;
                });
            }
        });

        thread.IsBackground = true;
        thread.Start();
    }

    private void Post(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch { }
    }
}
