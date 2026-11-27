using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MusicToppin;

/// <summary>
/// 击杀播报风格悬浮窗。
/// 分层窗口 + GDI+ 逐像素 alpha：无 WPF、无 WebView、无 GPU 依赖。
/// 鼠标穿透用 WS_EX_TRANSPARENT（写进 CreateParams + 仅 SetWindowLong 切换，不触发 UpdateStyles）。
/// </summary>
internal sealed class HudForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const int ULW_ALPHA = 0x00000002;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_HOTKEY = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    private readonly Bitmap _measureBmp = new Bitmap(1, 1);
    private readonly Graphics _measureG;
    private readonly System.Windows.Forms.Timer _animTimer;

    private Bitmap _bmp;
    private byte[] _premulBuffer;
    private HudLayout _layout;
    private RingLayout _ringLayout;
    private NowPlaying _state = NowPlaying.Empty;
    private bool _compact;
    private bool _ring;
    private bool _vinyl;
    private int _ringSide = 1;
    private bool _ringReverse;
    private bool _classicLeft;
    private bool _green;
    private bool _locked;
    private bool _editing;
    private bool _rendering;
    private float _anim = 1f;

    private bool _dragging;
    private Point _dragOrigin;
    private Point _cursorStart;
    private string _lastTitle = "";
    private string _signature = "";

    public event Action PrevRequested;
    public event Action NextRequested;
    public event Action ToggleLockRequested;
    public event Action MenuRequested;
    public event Action DragFinished;
    public event Action<int> HotkeyPressed;

    public HudForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Music Toppin";
        AutoScaleMode = AutoScaleMode.None;   // 手工按 DPI 缩放，禁用 WinForms 自动缩放
        Width = (int)HudRenderer.DesignWidth;
        Height = 160;

        _measureG = Graphics.FromImage(_measureBmp);
        _measureG.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // 新曲目完成时，下方灰卡做 200ms 的堆叠下移动画
        _animTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _animTimer.Tick += (_, _) =>
        {
            try
            {
                _anim = Math.Min(1f, _anim + (_ring ? 0.06f : 0.10f));
                Render();
            }
            catch (Exception ex)
            {
                Logger.Write("anim: " + ex.Message);
                _animTimer.Stop();
                _anim = 1f;
            }
            if (_anim >= 1f) _animTimer.Stop();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            // 穿透位必须进 CreateParams：TopMost 等属性变化会触发 UpdateStyles，
            // 否则会被 WinForms 用 CreateParams 覆盖回去。
            if (_locked) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    private float Scale
    {
        get { return DeviceDpi / 96f; }
    }

    public void SetState(NowPlaying np, bool animate)
    {
        var trackChanged = !string.Equals(np.Title, _lastTitle, StringComparison.Ordinal);
        _state = np;

        if (trackChanged)
        {
            _lastTitle = np.Title;
            _ringReverse = np.RingReverse;
            if (animate && np.HasSession && np.Title.Length > 0)
            {
                _anim = 0f;
                _animTimer.Start();
            }
        }

        var sig = BuildSignature(np);
        if (sig == _signature && _bmp != null) return;
        _signature = sig;

        Render();
    }

    /// <summary>内容签名：包含 HasTimeline 与历史内容，避免「该重绘却没重绘」。</summary>
    private static string BuildSignature(NowPlaying np)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(np.HasSession ? "1" : "0").Append('\u0001')
            .Append(np.Title).Append('\u0001').Append(np.Artist)
            .Append('\u0001').Append(np.Playing ? "1" : "0")
            .Append('\u0001').Append(np.HasTimeline ? "1" : "0")
            .Append('\u0001').Append((int)np.Position.TotalSeconds)
            .Append('\u0001').Append((int)np.Duration.TotalSeconds)
            .Append('\u0001').Append(np.PrevCount)
            .Append('\u0001').Append(np.PlayedCount)
            .Append('\u0001').Append(np.NextTitle ?? "")
            .Append('\u0002').Append(np.NextArtist ?? "")
            .Append('\u0001').Append(np.QueueCount);

        for (var i = 0; i < NowPlaying.HistorySlots; i++)
        {
            var title = np.PrevTitles != null && i < np.PrevTitles.Length ? np.PrevTitles[i] : "";
            var artist = np.PrevArtists != null && i < np.PrevArtists.Length ? np.PrevArtists[i] : "";
            builder.Append('\u0001').Append(title).Append('\u0002').Append(artist);
        }

        return builder.ToString();
    }

    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        Render();
    }

    public void SetRing(bool ring)
    {
        if (_ring == ring) return;
        _ring = ring;
        Render();
    }

    public void SetVinyl(bool vinyl)
    {
        if (_vinyl == vinyl) return;
        _vinyl = vinyl;
        Render();
    }

    public bool IsVinyl
    {
        get { return _vinyl; }
    }

    /// <summary>环状样式的方向：0 上、1 下、2 左、3 右。圆心在对应屏幕外侧。</summary>
    public void SetRingSide(int side)
    {
        side = Math.Max(0, Math.Min(3, side));
        if (_ringSide == side) return;
        _ringSide = side;
        if (_ring)
        {
            // 动画进行中换方向/尺寸会瞬移，直接收敛到最终状态
            if (_anim < 1f)
            {
                _animTimer.Stop();
                _anim = 1f;
            }
            Render();
        }
    }

    /// <summary>经典/极简样式贴左侧：灰卡左对齐、强调条在右、动画向左滑入。</summary>
    public void SetClassicLeft(bool left)
    {
        if (_classicLeft == left) return;
        _classicLeft = left;

        if (_ring) return;

        // 动画进行中换边会瞬移，直接收敛到最终状态
        if (_anim < 1f)
        {
            _animTimer.Stop();
            _anim = 1f;
        }

        Render();
    }

    public bool IsRing
    {
        get { return _ring; }
    }

    public void SetGreen(bool green)
    {
        if (_green == green) return;
        _green = green;
        Render();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (!locked)
        {
            // 解锁时刷新时间戳，否则 12 秒自动回锁会在 1 秒内把编辑态收回去
            LastInteractionUtc = DateTime.UtcNow;
        }
        if (locked) CancelDrag();
        ApplyLockStyle();
    }

    public bool IsLocked
    {
        get { return _locked; }
    }

    /// <summary>编辑模式：才允许拖动、点击按钮。默认关闭（鼠标穿透），打游戏不会误触。</summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        Render();
    }

    public bool IsEditing
    {
        get { return _editing; }
    }

    /// <summary>最后一次鼠标交互时间，用来在编辑模式下自动回锁。</summary>
    public DateTime LastInteractionUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>取消进行中的拖动（锁定/隐藏/弹菜单时调用，避免鼠标状态残留）。</summary>
    private Point _offscreenBackup = Point.Empty;

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>是否处于"遮挡模式"（压在其它窗口后面）。</summary>
    public bool IsOccluded { get; private set; }

    /// <summary>
    /// 遮挡模式：取消置顶并把窗口压到 Z 序最底，被游戏/浏览器挡住。
    /// 窗口仍在屏幕内且可见，按窗口采集的软件仍能拿到画面。
    /// </summary>
    public void SetCaptureOccluded(bool occluded)
    {
        if (occluded)
        {
            IsOccluded = true;
            TopMost = false;
            if (Handle != IntPtr.Zero)
            {
                SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            Logger.Write("capture occluded");
            return;
        }

        if (!IsOccluded) return;

        IsOccluded = false;
        TopMost = true;
        Logger.Write("capture occluded: off");
    }

    /// <summary>是否处于"移到屏幕外"的采集模式。</summary>
    public bool IsOffscreen { get; private set; }

    /// <summary>
    /// 把浮窗挪到所有显示器之外：主播看不到，但窗口仍然可见（IsWindowVisible），
    /// 直播姬/直播伴侣这类"窗口采集"仍能选到它。
    /// </summary>
    public void SetCaptureOffscreen(bool offscreen)
    {
        if (offscreen)
        {
            if (!IsOffscreen) _offscreenBackup = Location;

            IsOffscreen = true;

            var screen = SystemInformation.VirtualScreen;
            Location = new Point(screen.Left - Width - 320, screen.Top + 40);
            Logger.Write("capture offscreen: " + Location.X + "," + Location.Y);
            Render();
            return;
        }

        if (!IsOffscreen) return;

        IsOffscreen = false;
        if (_offscreenBackup != Point.Empty)
        {
            Location = _offscreenBackup;
        }
        Logger.Write("capture offscreen: restored");
        Render();
    }
    public void CancelDrag()
    {
        _dragging = false;
        if (Capture) Capture = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLockStyle();
        Render();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            base.WndProc(ref m);
            Render();
            return;
        }

        if (m.Msg == WM_HOTKEY)
        {
            var handler = HotkeyPressed;
            if (handler != null) handler(m.WParam.ToInt32());
            return;
        }

        base.WndProc(ref m);
    }

    public void ApplyLockStyle()
    {
        if (!IsHandleCreated) return;
        var ex = GetWindowLong(Handle, GWL_EXSTYLE);
        ex = _locked ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
    }

    public void Render()
    {
        if (_rendering || !IsHandleCreated) return;
        _rendering = true;
        try
        {
            var s = Scale;
            HudLayout layout = null;
            RingLayout ringLayout = null;
            VinylLayout vinylLayout = null;
            int width;
            int height;

            if (_vinyl)
            {
                vinylLayout = HudRenderer.MeasureVinyl(s);
                width = vinylLayout.Width;
                height = vinylLayout.Height;
                _layout = null;
                _ringLayout = null;
            }
            else if (_ring)
            {
                ringLayout = HudRenderer.MeasureRing(_measureG, s, _state, _ringSide);
                width = ringLayout.Width;
                height = ringLayout.Height;
                _layout = null;
                _ringLayout = ringLayout;
            }
            else
            {
                layout = HudRenderer.Measure(_measureG, s, _state, _compact, _green, _classicLeft);
                width = layout.Width;
                height = layout.Height;
                _layout = layout;
                _ringLayout = null;
            }

            if (_bmp == null || _bmp.Width != width || _bmp.Height != height)
            {
                if (_bmp != null) _bmp.Dispose();
                _bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
            }

            using (var g = Graphics.FromImage(_bmp))
            {
                g.Clear(Color.Transparent);
                if (_vinyl)
                {
                    HudRenderer.DrawVinyl(g, s, _state, vinylLayout, _green);
                }
                else if (_ring)
                {
                    HudRenderer.DrawRing(g, s, _state, ringLayout, _green, _editing, _anim, _ringReverse);
                }
                else
                {
                    HudRenderer.Draw(g, s, _state, layout, _green, _compact, _editing, _anim);
                }
            }

            var dump = Environment.GetEnvironmentVariable("MUSICTOPPIN_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                try { _bmp.Save(dump, ImageFormat.Png); } catch { }
            }

            Premultiply(_bmp);

            if (Width != width || Height != height)
            {
                Width = width;
                Height = height;
            }

            Push();
        }
        catch (Exception ex)
        {
            // 渲染异常（超长标题、GDI+ 资源失败）不能让托盘进程崩掉
            Logger.Write("render: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>把 ARGB 直通 alpha 转成预乘 alpha（分层窗口的要求）。</summary>
    private void Premultiply(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var length = stride * bmp.Height;
            if (_premulBuffer == null || _premulBuffer.Length < length)
                _premulBuffer = new byte[length];

            var buffer = _premulBuffer;
            Marshal.Copy(data.Scan0, buffer, 0, length);

            for (var i = 0; i < length; i += 4)
            {
                var a = buffer[i + 3];
                if (a == 255) continue;
                if (a == 0)
                {
                    buffer[i] = 0;
                    buffer[i + 1] = 0;
                    buffer[i + 2] = 0;
                    continue;
                }
                buffer[i] = (byte)((buffer[i] * a + 127) / 255);
                buffer[i + 1] = (byte)((buffer[i + 1] * a + 127) / 255);
                buffer[i + 2] = (byte)((buffer[i + 2] * a + 127) / 255);
            }

            Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void Push(byte alpha = 255)
    {
        if (_bmp == null || !IsHandleCreated) return;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBmp = _bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = _bmp.Width, cy = _bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var top = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = 1
            };
            if (!UpdateLayeredWindow(Handle, screenDc, ref top, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA))
            {
                Logger.Write("UpdateLayeredWindow failed: " + Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        LastInteractionUtc = DateTime.UtcNow;
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOrigin = Location;
            _cursorStart = Cursor.Position;
            Capture = true;   // 必须捕获，否则鼠标离开窗口就收不到 MouseUp
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        LastInteractionUtc = DateTime.UtcNow;

        // 左键已经松开却还在拖动（MouseUp 丢失）：直接结束，避免窗口粘着光标
        if (_dragging && (Control.MouseButtons & MouseButtons.Left) == 0)
        {
            CancelDrag();
        }

        if (_dragging)
        {
            var cursor = Cursor.Position;
            Location = new Point(
                _dragOrigin.X + (cursor.X - _cursorStart.X),
                _dragOrigin.Y + (cursor.Y - _cursorStart.Y));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            CancelDrag();
            MenuRequested?.Invoke();
            base.OnMouseUp(e);
            return;
        }

        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            if (Capture) Capture = false;

            var cursor = Cursor.Position;
            var moved = Math.Abs(cursor.X - _cursorStart.X) + Math.Abs(cursor.Y - _cursorStart.Y) > 4;
            if (moved)
            {
                var handler = DragFinished;
                if (handler != null) handler();
            }
            else
            {
                HitTestClick(e.Location);
            }
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _dragging = false;
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // 双击导航卡片会先触发一次切歌，这里只对非导航区域响应
            if (!IsNavHit(e.Location))
            {
                ToggleLockRequested?.Invoke();
            }
        }
        base.OnMouseDoubleClick(e);
    }

    private bool IsNavHit(Point point)
    {
        if (_ring)
        {
            var ring = _ringLayout;
            return ring != null &&
                   ((ring.HasPrev && ring.PrevRect.Contains(point)) ||
                    (ring.HasNext && ring.NextRect.Contains(point)));
        }

        var layout = _layout;
        return layout != null && layout.NavPrev.Contains(point);
    }

    private void HitTestClick(Point point)
    {
        if (_ring)
        {
            var ring = _ringLayout;
            if (ring == null) return;
            if (ring.HasPrev && ring.PrevRect.Contains(point)) PrevRequested?.Invoke();
            else if (ring.HasNext && ring.NextRect.Contains(point)) NextRequested?.Invoke();
            return;
        }

        var layout = _layout;
        if (layout == null) return;
        // V1 只有「上一首」可点，「下一首」留到点歌机版本
        if (layout.NavPrev.Contains(point)) PrevRequested?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Dispose();
            _measureG.Dispose();
            _measureBmp.Dispose();
            if (_bmp != null) _bmp.Dispose();
        }
        base.Dispose(disposing);
    }
}
