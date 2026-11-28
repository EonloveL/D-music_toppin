using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace MusicToppin;

/// <summary>
/// 击杀提示像素识别：用户框选屏幕区域 + 抓一张击杀提示模板，之后用归一化互相关匹配（不 OCR）。
/// 识别到击杀瞬间触发一次宠物互动；带冷却，避免同一次击杀重复触发。
/// </summary>
internal sealed class KillDetector : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _templatePath;
    private Rectangle _region;
    private byte[] _template;
    private DateTime _lastHitUtc = DateTime.MinValue;
    private bool _armed = true;
    private bool _enabled;

    public int ThresholdPercent = 80;
    public int CooldownMs = 2500;
    public event Action Hit;

    public KillDetector()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicToppin");
        Directory.CreateDirectory(dir);
        _templatePath = Path.Combine(dir, "kill-template.gray");

        _timer = new System.Windows.Forms.Timer { Interval = 220 };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled
    {
        get { return _enabled; }
        set
        {
            _enabled = value;
            if (value) _timer.Start(); else _timer.Stop();
        }
    }

    public Rectangle Region
    {
        get { return _region; }
        set { _region = value; }
    }

    public void ClearTemplate()
    {
        _template = null;
        try { if (File.Exists(_templatePath)) File.Delete(_templatePath); } catch { }
    }

    public bool HasTemplate
    {
        get { return _template != null; }
    }

    /// <summary>从磁盘加载上次抓的模板；没有返回 false。</summary>
    public bool LoadTemplate()
    {
        try
        {
            if (!File.Exists(_templatePath)) return false;
            var bytes = File.ReadAllBytes(_templatePath);
            if (bytes.Length != Width * Height) return false;
            _template = bytes;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Write("kill template load: " + ex.Message);
            return false;
        }
    }

    /// <summary>把当前框选区域抓成模板（请在击杀提示显示时调用）。</summary>
    public bool CaptureTemplate()
    {
        using (var frame = CaptureRegion())
        {
            if (frame == null) return false;
            var gray = ToGray(frame, Width, Height);
            double mean = 0;
            for (var i = 0; i < gray.Length; i++) mean += gray[i];
            mean /= gray.Length;
            double variance = 0;
            for (var i = 0; i < gray.Length; i++) { var d = gray[i] - mean; variance += d * d; }
            if (variance / gray.Length < 40) return false; // 纯色/黑屏不能当模板
            _template = gray;
            _armed = false;
            _lastHitUtc = DateTime.UtcNow;
            try { File.WriteAllBytes(_templatePath, gray); }
            catch (Exception ex) { Logger.Write("kill template save: " + ex.Message); }
            return true;
        }
    }

    public void Test()
    {
        var handler = Hit;
        if (handler != null)
        {
            _lastHitUtc = DateTime.UtcNow;
            try { handler(); } catch (Exception ex) { Logger.Write("kill hit: " + ex.Message); }
        }
    }

    private void Tick()
    {
        if (!_enabled || _template == null) return;
        if (_region.Width < 8 || _region.Height < 8) return;
        if ((long)_region.Width * _region.Height > 640 * 480)
        {
            Logger.Write("kill detect: region too large, disabled");
            _enabled = false;
            _timer.Stop();
            return;
        }
        if ((DateTime.UtcNow - _lastHitUtc).TotalMilliseconds < CooldownMs) return;
        try
        {
            using (var frame = CaptureRegion())
            {
                if (frame == null) return;
                var gray = ToGray(frame, Width, Height);
                var score = KillMatch.Ncc(_template, gray);
                if (score * 100 >= ThresholdPercent)
                {
                    if (!_armed) return;
                    _armed = false;
                    _lastHitUtc = DateTime.UtcNow;
                    Logger.Write("kill detected: score=" + (score * 100).ToString("F1") + "%");
                    var handler = Hit;
                    if (handler != null) handler();
                }
                else if (score * 100 < ThresholdPercent * 0.7)
                {
                    // 模板消失后再武装，避免同一条击杀提示停留期间重复触发
                    _armed = true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Write("kill detect: " + ex.Message);
        }
    }

    private Bitmap CaptureRegion()
    {
        if (_region.Width <= 0 || _region.Height <= 0) return null;
        Bitmap bmp;
        try
        {
            bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public const int Width = 64;
    public const int Height = 36;

    /// <summary>缩小到固定尺寸并转灰度（模板与实时帧同一尺度）。</summary>
    internal static byte[] ToGray(Bitmap source, int width, int height)
    {
        var gray = new byte[width * height];
        using (var small = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var c = small.GetPixel(x, y);
                    gray[y * width + x] = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000);
                }
            }
        }
        return gray;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>全屏拖拽矩形选择器（用于框选击杀提示区域）。</summary>
internal static class ScreenRegionSelector
{
    public static Rectangle? Select()
    {
        using (var form = new RegionForm())
        {
            form.ShowDialog();
            return form.Result;
        }
    }

    private sealed class RegionForm : Form
    {
        private Point _start;
        private bool _dragging;
        private Rectangle _rect;
        public Rectangle? Result;

        public RegionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.30;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Result = null; Close(); } };
            MouseDown += (_, e) => { _start = e.Location; _dragging = true; _rect = new Rectangle(_start, Size.Empty); };
            MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                _rect = Normalize(_start, e.Location);
                Invalidate();
            };
            MouseUp += (_, e) =>
            {
                if (!_dragging) return;
                _dragging = false;
                var local = Normalize(_start, e.Location);
                if (local.Width >= 8 && local.Height >= 8)
                {
                    var screen = SystemInformation.VirtualScreen;
                    Result = new Rectangle(screen.Left + local.Left, screen.Top + local.Top, local.Width, local.Height);
                }
                Close();
            };
            Paint += (_, e) =>
            {
                if (_rect.Width <= 0) return;
                using (var pen = new Pen(Color.FromArgb(255, 90, 90), 2))
                    e.Graphics.DrawRectangle(pen, _rect);
                using (var brush = new SolidBrush(Color.FromArgb(60, 255, 90, 90)))
                    e.Graphics.FillRectangle(brush, _rect);
            };
        }

        private static Rectangle Normalize(Point a, Point b)
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }
}
