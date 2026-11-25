namespace MusicToppin;

/// <summary>通用单行输入框（点歌测试等场景）。</summary>
internal sealed class PromptDialog : Form
{
    private readonly TextBox _input;
    public string Value { get; private set; }

    public PromptDialog(string title, string hint, string initial = null)
    {
        Text = title + " · Music Toppin";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 170);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9f);
        TopMost = true;

        var label = new Label
        {
            Text = hint,
            ForeColor = Color.FromArgb(90, 96, 108),
            AutoSize = false,
            Location = new Point(20, 20),
            Size = new Size(420, 22)
        };

        _input = new TextBox
        {
            Location = new Point(20, 50),
            Width = 420,
            BorderStyle = BorderStyle.FixedSingle,
            Text = initial ?? ""
        };

        var ok = new Button
        {
            Text = "确定",
            Location = new Point(330, 118),
            Size = new Size(110, 32),
            BackColor = Color.FromArgb(22, 24, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            Value = _input.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(240, 118),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.System
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { label, _input, ok, cancel });
    }
}
