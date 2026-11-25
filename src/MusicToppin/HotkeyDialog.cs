namespace MusicToppin;

/// <summary>锁定/解锁快捷键设置：按下即录制，至少需要一个修饰键。</summary>
internal sealed class HotkeyDialog : Form
{
    private readonly Label _value;
    private readonly Button _ok;
    private readonly Label _hint;

    private uint _modifiers;
    private Keys _key = Keys.None;

    /// <summary>确定后的组合（例如 "Ctrl+Alt+F9"）；取消时为 null；禁用时为空字符串。</summary>
    public string Result { get; private set; }

    public HotkeyDialog(string current)
    {
        Text = "锁定快捷键 · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 210);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        TopMost = true;
        KeyPreview = true;

        var title = new Label
        {
            Text = "按下你想用的快捷键",
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 24, 30),
            AutoSize = true,
            Location = new Point(20, 16)
        };

        var currentLabel = new Label
        {
            Text = string.IsNullOrEmpty(current) ? "当前：自动选择" : "当前：" + current,
            ForeColor = Color.FromArgb(120, 126, 138),
            AutoSize = true,
            Location = new Point(22, 50)
        };

        _value = new Label
        {
            Text = "等待按键…",
            Font = new Font("Consolas", 16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(60, 66, 78),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(22, 78),
            Size = new Size(376, 50)
        };

        _hint = new Label
        {
            Text = "需要至少一个 Ctrl / Alt / Shift；Esc 取消",
            ForeColor = Color.FromArgb(150, 156, 168),
            AutoSize = true,
            Location = new Point(22, 136)
        };

        _ok = new Button
        {
            Text = "确定",
            Location = new Point(288, 164),
            Size = new Size(110, 32),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _ok.FlatAppearance.BorderSize = 0;
        _ok.Click += (_, _) => Accept();

        var clear = new Button
        {
            Text = "不用快捷键",
            Location = new Point(168, 164),
            Size = new Size(110, 32),
            FlatStyle = FlatStyle.System
        };
        clear.Click += (_, _) =>
        {
            Result = "";
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(100, 164),
            Size = new Size(60, 32),
            FlatStyle = FlatStyle.System
        };
        cancel.Click += (_, _) =>
        {
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[] { title, currentLabel, _value, _hint, cancel, clear, _ok });

        KeyDown += OnCaptureKeyDown;
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        // 单独的修饰键不算，等主键
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu ||
            e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
        {
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        uint modifiers = 0;
        var parts = new List<string>();
        if ((e.Modifiers & Keys.Control) != 0) { modifiers |= 0x0002; parts.Add("Ctrl"); }
        if ((e.Modifiers & Keys.Alt) != 0) { modifiers |= 0x0001; parts.Add("Alt"); }
        if ((e.Modifiers & Keys.Shift) != 0) { modifiers |= 0x0004; parts.Add("Shift"); }

        if (modifiers == 0)
        {
            _value.Text = "需要加一个修饰键";
            _value.ForeColor = Color.FromArgb(200, 90, 80);
            _ok.Enabled = false;
            _modifiers = 0;
            _key = Keys.None;
            e.SuppressKeyPress = true;
            return;
        }

        _modifiers = modifiers;
        _key = e.KeyCode;

        parts.Add(e.KeyCode.ToString());
        _value.Text = string.Join("+", parts);
        _value.ForeColor = Color.FromArgb(60, 66, 78);
        _ok.Enabled = true;
        e.SuppressKeyPress = true;
    }

    private void Accept()
    {
        if (_modifiers == 0 || _key == Keys.None) return;
        Result = _value.Text;
        DialogResult = DialogResult.OK;
        Close();
    }
}
