using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MusicToppin;

/// <summary>下方「已播完」灰卡的一层布局。</summary>
internal sealed class PrevCardLayout
{
    public RectangleF Card;
    public RectangleF Status;
    public RectangleF Title;
    public RectangleF Artist;
    public float Alpha;
    public float Scale;
}

/// <summary>浮窗布局结果：所有矩形在窗口内以设备像素计。</summary>
internal sealed class HudLayout
{
    public int Width;
    public int Height;

    public RectangleF Card;
    public RectangleF Header;
    public RectangleF Title;
    public RectangleF Artist;
    public float DividerY;
    public RectangleF NavPrev;
    public RectangleF Progress;

    public List<PrevCardLayout> PrevCards = new List<PrevCardLayout>();
}

/// <summary>纯 GDI+ 绘制，无 GPU、无额外线程，只在内容变化时重绘。</summary>
internal static class HudRenderer
{
    public const float DesignWidth = 404f;

    /// <summary>静止状态最多显示 2 张：播放完毕 + 更早播放（各一张）。</summary>
    public const int VisiblePrevCards = 2;

    private const float PadX = 15f;
    private const float PadY = 12f;
    private const float AccentW = 3f;
    private const float Radius = 10f;
    private const float GreenPad = 12f;

    /// <summary>.NET Framework 没有 MathF，这里做一层小型浮点工具。</summary>
    private static class MF
    {
        public static float Round(float v) { return (float)Math.Round(v); }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Ceil(float v) { return (float)Math.Ceiling(v); }
        public static float Ceiling(float v) { return (float)Math.Ceiling(v); }
        public static float Clamp(float v, float min, float max) { return v < min ? min : (v > max ? max : v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
    }

    /// <summary>当前配色（由托盘「颜色」菜单驱动）。卡片底色固定，只允许改文字/强调色/背景透明度。</summary>
    public static HudTheme Theme = HudTheme.Default;

    private static Color C_CardOpaque { get { return HudTheme.WithAlpha(Theme.Card, 255); } }
    private static Color C_Accent { get { return Theme.Accent; } }
    private static Color C_Title { get { return Theme.Title; } }
    private static Color C_Artist { get { return Theme.Artist; } }
    private static Color C_Muted { get { return HudTheme.Blend(Theme.Artist, Theme.Card, 0.62f); } }
    private static Color C_NavInk { get { return HudTheme.Blend(Theme.Title, Theme.Card, 0.86f); } }
    private static Color C_DimTitle { get { return HudTheme.Blend(Theme.Title, Theme.Card, 0.55f); } }
    private static Color C_DimArtist { get { return HudTheme.Blend(Theme.Artist, Theme.Card, 0.55f); } }
    private static Color C_Streak { get { return HudTheme.Blend(Theme.Title, Theme.Card, 0.8f); } }
    private static Color C_Border { get { return Color.FromArgb(36, Theme.Title); } }
    private static Color C_BorderOpaque { get { return Color.FromArgb(52, Theme.Title); } }

    private static readonly Dictionary<(string, float, FontStyle), Font> Fonts = new();

    private static Font F(string family, float px, FontStyle style)
    {
        var key = (family, px, style);
        Font font;
        if (!Fonts.TryGetValue(key, out font))
        {
            font = new Font(family, px, style, GraphicsUnit.Pixel);
            Fonts[key] = font;
        }
        return font;
    }

    private static Font TitleFont(float s, bool compact) { return F("Microsoft YaHei UI", (compact ? 16f : 19f) * s, FontStyle.Bold); }
    private static Font ArtistFont(float s, bool compact) { return F("Microsoft YaHei UI", (compact ? 12.5f : 14f) * s, FontStyle.Regular); }
    private static Font TagFont(float s) { return F("Microsoft YaHei UI", 11f * s, FontStyle.Bold); }
    private static Font NavFont(float s) { return F("Microsoft YaHei UI", 12.5f * s, FontStyle.Regular); }
    private static Font NavKeyFont(float s) { return F("Microsoft YaHei UI", 10f * s, FontStyle.Regular); }
    private static Font MonoFont(float s) { return F("Consolas", 11f * s, FontStyle.Regular); }
    private static Font StreakFont(float s) { return F("Microsoft YaHei UI", 10.5f * s, FontStyle.Regular); }
    private static Font PrevTitleFont(float s, float scale) { return F("Microsoft YaHei UI", 16f * s * scale, FontStyle.Bold); }
    private static Font PrevArtistFont(float s, float scale) { return F("Microsoft YaHei UI", 12.5f * s * scale, FontStyle.Regular); }

    private static StringFormat WrapFormat()
    {
        return new StringFormat { Trimming = StringTrimming.None };
    }

    private static StringFormat EllipsisFormat()
    {
        return new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    }

    private static float MeasureWrapped(Graphics g, string text, Font font, float width)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        using (var sf = WrapFormat())
        {
            return g.MeasureString(text, font, new SizeF(width, 10000f), sf).Height;
        }
    }

    /// <summary>每一层的缩放（越往下越小）。</summary>
    private static float PrevScale(int index)
    {
        return 1f - 0.045f * index;
    }

    /// <summary>每一层的静止透明度。</summary>
    private static float PrevAlpha(int index)
    {
        switch (index)
        {
            case 0: return 232f;
            case 1: return 196f;
            default: return 136f;
        }
    }

    public static HudLayout Measure(Graphics g, float s, NowPlaying np, bool compact, bool green)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var layout = new HudLayout();
        var width = MF.Round(DesignWidth * s);
        var innerW = width - (PadX * 2 + AccentW) * s;

        var titleH = MF.Max(TitleFont(s, compact).Height, MeasureWrapped(g, np.Title, TitleFont(s, compact), innerW));
        // 超长标题兜底：最多 10 行，避免窗口高到屏幕外（正常歌曲远达不到）
        var titleLineCap = TitleFont(s, compact).Height * 10f;
        if (titleH > titleLineCap) titleH = titleLineCap;
        var artistH = string.IsNullOrWhiteSpace(np.Artist)
            ? 0f
            : MF.Max(ArtistFont(s, compact).Height, MeasureWrapped(g, np.Artist, ArtistFont(s, compact), innerW));

        var headerH = 14f * s;
        var navRowH = 17f * s;

        var hasTimeline = np.HasTimeline && np.Duration > TimeSpan.Zero;

        // V1 只显示「上一首」，不再显示「下一首」
        var cardH = PadY * s * 2 + headerH + 8f * s + titleH + 5f * s + artistH
                    + (string.IsNullOrWhiteSpace(np.Artist) ? 0 : 4f * s)
                    + 11f * s + navRowH
                    + (hasTimeline ? 5f * s : 0f);

        layout.Width = (int)width;
        layout.Card = new RectangleF(0, 0, width, cardH);

        var y = PadY * s;
        layout.Header = new RectangleF(PadX * s + AccentW * s, y, innerW, headerH);
        y += headerH + 8f * s;
        layout.Title = new RectangleF(PadX * s + AccentW * s, y, innerW, titleH);
        y += titleH + 5f * s;
        layout.Artist = new RectangleF(PadX * s + AccentW * s, y, innerW, artistH);
        if (artistH > 0) y += artistH + 4f * s;
        y += 11f * s;
        layout.DividerY = y;
        y += 2f * s;

        layout.NavPrev = new RectangleF(PadX * s + AccentW * s, y, innerW, navRowH);
        y += navRowH + 3f * s;
        layout.Progress = new RectangleF(0, cardH - 2f * s, width, hasTimeline ? 2f * s : 0f);

        var totalH = cardH;
        var gap = 9f * s;

        // 布局最多 3 个槽位：静止时只画前 2 个，第 3 个仅用于「被挤下去」的动画残影
        var slots = compact ? 0 : Math.Min(np.PrevCount, NowPlaying.HistorySlots);
        for (var i = 0; i < slots; i++)
        {
            var title = np.PrevTitles != null && i < np.PrevTitles.Length ? np.PrevTitles[i] : "";
            if (string.IsNullOrWhiteSpace(title)) break;
            var artist = np.PrevArtists != null && i < np.PrevArtists.Length ? np.PrevArtists[i] : "";
            var scale = PrevScale(i);

            var cardW = MF.Round(width * scale);
            var innerWi = cardW - (PadX * 2 + AccentW) * s * scale;
            var tH = MF.Max(PrevTitleFont(s, scale).Height, MeasureWrapped(g, title, PrevTitleFont(s, scale), innerWi));
            var aH = string.IsNullOrWhiteSpace(artist)
                ? 0f
                : MF.Max(PrevArtistFont(s, scale).Height, MeasureWrapped(g, artist, PrevArtistFont(s, scale), innerWi));

            var h = PadY * s * scale * 2 + 13f * s * scale + 6f * s * scale + tH
                    + (aH > 0 ? aH + 3f * s * scale : 0f);

            var card = new PrevCardLayout
            {
                Card = new RectangleF(width - cardW, totalH + gap, cardW, h),
                Alpha = PrevAlpha(i),
                Scale = scale
            };

            var py = card.Card.Y + PadY * s * scale;
            card.Status = new RectangleF(card.Card.X + (PadX * s + AccentW * s) * scale, py, innerWi, 13f * s * scale);
            py += 13f * s * scale + 6f * s * scale;
            card.Title = new RectangleF(card.Card.X + (PadX * s + AccentW * s) * scale, py, innerWi, tH);
            py += tH;
            if (aH > 0)
            {
                py += 3f * s * scale;
                card.Artist = new RectangleF(card.Card.X + (PadX * s + AccentW * s) * scale, py, innerWi, aH);
            }

            layout.PrevCards.Add(card);

            // 第 3 个槽位只是动画残影，不占静止高度
            if (i < VisiblePrevCards) totalH = card.Card.Bottom + gap;
        }

        if (green)
        {
            var pad = GreenPad * s;
            layout.Card = Shift(layout.Card, pad);
            layout.Header = Shift(layout.Header, pad);
            layout.Title = Shift(layout.Title, pad);
            layout.Artist = Shift(layout.Artist, pad);
            layout.NavPrev = Shift(layout.NavPrev, pad);
            layout.Progress = Shift(layout.Progress, pad);
            foreach (var card in layout.PrevCards)
            {
                card.Card = Shift(card.Card, pad);
                card.Status = Shift(card.Status, pad);
                card.Title = Shift(card.Title, pad);
                card.Artist = Shift(card.Artist, pad);
            }
            layout.DividerY += pad;
            layout.Width = (int)MF.Ceiling(width + pad * 2);
            layout.Height = (int)MF.Ceiling(totalH + pad * 2);
            return layout;
        }

        layout.Height = (int)MF.Ceiling(totalH);
        return layout;
    }

    public static void Draw(Graphics g, float s, NowPlaying np, HudLayout l, bool green, bool compact, bool editing, float anim)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (green)
        {
            using (var bg = new SolidBrush(Color.FromArgb(255, 0, 255, 0)))
                g.FillRectangle(bg, 0, 0, l.Width, l.Height);
        }

        var opacity = MF.Clamp(Theme.CardOpacity, 0.05f, 1f);
        var border = green ? C_BorderOpaque : Color.FromArgb(Math.Max(8, (int)(C_Border.A * opacity)), C_Border.R, C_Border.G, C_Border.B);
        if (editing) border = C_Accent;

        // 灰卡：从下往上画，整体做「往下顺移 + 逐渐变小」的堆叠动画
        for (var i = l.PrevCards.Count - 1; i >= 0; i--)
        {
            var isGhost = i >= VisiblePrevCards;
            if (isGhost && anim >= 1f) continue;
            DrawPrevCard(g, s, np, l, i, isGhost, border, green, anim);
        }

        var cardBg = green
            ? C_CardOpaque
            : HudTheme.WithAlpha(Theme.Card, (int)(255f * MF.Clamp(Theme.CardOpacity, 0.05f, 1f)));
        DrawCard(g, s, l.Card, cardBg, border, true);

        var header = l.Header;
        var accent = np.Playing ? C_Accent : Color.FromArgb(255, 124, 134, 148);
        var dotR = 3.5f * s;
        using (var dot = new SolidBrush(accent))
            g.FillEllipse(dot, header.X, header.Y + header.Height / 2f - dotR, dotR * 2, dotR * 2);

        var tagX = header.X + dotR * 2 + 8f * s;
        using (var tag = new SolidBrush(accent))
        using (var sf = EllipsisFormat())
        {
            var state = editing ? "编辑中" : (np.Playing ? "正在播放" : "已暂停");
            g.DrawString(state, TagFont(s), tag, new RectangleF(tagX, header.Y - 1f * s, 160f * s, header.Height), sf);
        }

        if (np.HasTimeline && np.Duration > TimeSpan.Zero)
        {
            var timeText = Fmt(np.Position) + " / " + Fmt(np.Duration);
            using (var tm = new SolidBrush(C_Muted))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                g.DrawString(timeText, MonoFont(s), tm, header, sf);
        }

        using (var b = new SolidBrush(C_Title))
        using (var sf = WrapFormat())
            g.DrawString(np.Title, TitleFont(s, compact), b, l.Title, sf);

        if (!string.IsNullOrWhiteSpace(np.Artist))
        {
            using (var b = new SolidBrush(C_Artist))
            using (var sf = WrapFormat())
                g.DrawString(np.Artist, ArtistFont(s, compact), b, l.Artist, sf);
        }

        var lineY = l.DividerY + 0.5f;
        var dividerAlpha = green ? 46 : Math.Max(6, (int)(20 * opacity));
        using (var pen = new Pen(Color.FromArgb(dividerAlpha, 255, 255, 255), MF.Max(1f, s)))
            g.DrawLine(pen, l.Card.X + PadX * s, lineY, l.Card.Right - PadX * s, lineY);

        var hasPrev = np.PrevCount > 0 && np.PrevTitles != null && np.PrevTitles.Length > 0 && !string.IsNullOrEmpty(np.PrevTitles[0]);
        var prevTitle = hasPrev ? np.PrevTitles[0] : "暂无历史";
        var prevArtist = hasPrev && np.PrevArtists != null && np.PrevArtists.Length > 0 ? np.PrevArtists[0] : null;
        DrawNavRow(g, s, l.NavPrev, "上一首", "↑", prevTitle, prevArtist);

        if (np.HasTimeline && np.Duration > TimeSpan.Zero)
        {
            var trackAlpha = green ? 40 : Math.Max(6, (int)(22 * opacity));
            using (var track = new SolidBrush(Color.FromArgb(trackAlpha, 255, 255, 255)))
                g.FillRectangle(track, l.Progress);
            var p = MF.Clamp((float)(np.Position.TotalSeconds / np.Duration.TotalSeconds), 0f, 1f);
            using (var path = RoundRect(l.Card, Radius * s))
            {
                var old = g.Clip;
                g.SetClip(path);
                var from = np.Playing ? C_Accent : Color.FromArgb(255, 108, 118, 132);
                var to = np.Playing ? Color.FromArgb(255, 255, 122, 69) : Color.FromArgb(255, 92, 101, 114);
                using (var fill = new LinearGradientBrush(
                    new PointF(l.Progress.X, 0), new PointF(l.Card.Right, 0), from, to))
                {
                    g.FillRectangle(fill, l.Progress.X, l.Progress.Y, l.Progress.Width * p, l.Progress.Height);
                }
                g.Clip = old;
            }
        }
    }

    /// <summary>
    /// 画一层灰卡。动画期间：新卡从上方滑入，旧卡顺移到下一槽位并缩小变淡，
    /// 被挤出的一张（ghost）继续下移并淡出。
    /// </summary>
    private static void DrawPrevCard(Graphics g, float s, NowPlaying np, HudLayout layout, int index,
        bool ghost, Color border, bool green, float anim)
    {
        var card = layout.PrevCards[index];

        // 起点：上一层的位置（第 0 层从上方 16px 处进入）
        RectangleF fromRect;
        float fromScale, fromAlpha;
        if (index == 0)
        {
            fromRect = new RectangleF(card.Card.X, card.Card.Y - 16f * s, card.Card.Width, card.Card.Height);
            fromScale = card.Scale;
            fromAlpha = 0f;
        }
        else
        {
            var upper = layout.PrevCards[index - 1];
            fromRect = upper.Card;
            fromScale = upper.Scale;
            fromAlpha = upper.Alpha;
        }

        var t = MF.Clamp(anim, 0f, 1f);
        var toAlpha = ghost ? 0f : card.Alpha;
        var toScale = card.Scale;

        var toX = card.Card.X;
        var toY = card.Card.Y;
        var toW = card.Card.Width;
        var toH = card.Card.Height;

        if (ghost)
        {
            // 残影层在画布之外，滑出去会被硬裁；改成在原位稍微下移并淡出
            var upper = layout.PrevCards[Math.Max(0, index - 1)];
            toX = upper.Card.X;
            toY = upper.Card.Y + 14f * s;
            toW = upper.Card.Width;
            toH = upper.Card.Height;
            if (toY + toH > layout.Height - 2f * s)
            {
                toY = Math.Max(0f, layout.Height - toH - 2f * s);
            }
        }

        var rect = new RectangleF(
            MF.Lerp(fromRect.X, toX, t),
            MF.Lerp(fromRect.Y, toY, t),
            MF.Lerp(fromRect.Width, toW, t),
            MF.Lerp(fromRect.Height, toH, t));
        var scale = MF.Lerp(fromScale, toScale, t);
        var alpha = (int)MF.Clamp(MF.Lerp(fromAlpha, toAlpha, t), 0f, 255f);

        var fill = green ? C_CardOpaque : HudTheme.WithAlpha(Theme.Card, (int)(alpha * MF.Clamp(Theme.CardOpacity, 0.05f, 1f)));
        var borderColor = Color.FromArgb((int)(border.A * alpha / 255f), border.R, border.G, border.B);
        DrawCard(g, s, rect, fill, borderColor, false);

        var title = np.PrevTitles != null && index < np.PrevTitles.Length ? np.PrevTitles[index] : "";
        var artist = np.PrevArtists != null && index < np.PrevArtists.Length ? np.PrevArtists[index] : "";

        var innerX = rect.X + (PadX * s + AccentW * s) * scale;
        var innerW = rect.Width - (PadX * 2 + AccentW) * s * scale;
        var py = rect.Y + PadY * s * scale;

        var status = new RectangleF(innerX, py, innerW, 13f * s * scale);
        using (var b = new SolidBrush(Color.FromArgb((int)(alpha * 0.88f), C_Muted.R, C_Muted.G, C_Muted.B)))
        using (var sf = EllipsisFormat())
            g.DrawString(index == 0 ? "播放完毕" : "更早播放", StreakFont(s), b, status, sf);

        if (index == 0 && np.PlayedCount > 1)
        {
            using (var b = new SolidBrush(Color.FromArgb(alpha, C_Streak.R, C_Streak.G, C_Streak.B)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                g.DrawString("已播 " + np.PlayedCount + " 首", StreakFont(s), b, status, sf);
        }

        py += 13f * s * scale + 6f * s * scale;
        var titleRect = new RectangleF(innerX, py, innerW, rect.Height);
        var titleAlpha = ghost ? (int)(alpha * 0.82f) : Math.Max((int)(alpha * 0.86f), 168);
        using (var b = new SolidBrush(Color.FromArgb(titleAlpha, C_DimTitle.R, C_DimTitle.G, C_DimTitle.B)))
        using (var sf = WrapFormat())
            g.DrawString(title, PrevTitleFont(s, scale), b, titleRect, sf);

        if (!string.IsNullOrWhiteSpace(artist))
        {
            var titleH = MF.Max(PrevTitleFont(s, scale).Height, MeasureWrapped(g, title, PrevTitleFont(s, scale), innerW));
            var artistRect = new RectangleF(innerX, py + titleH + 3f * s * scale, innerW, rect.Height);
            var artistAlpha = ghost ? (int)(alpha * 0.75f) : Math.Max((int)(alpha * 0.80f), 150);
            using (var b = new SolidBrush(Color.FromArgb(artistAlpha, C_DimArtist.R, C_DimArtist.G, C_DimArtist.B)))
            using (var sf = WrapFormat())
                g.DrawString(artist, PrevArtistFont(s, scale), b, artistRect, sf);
        }
    }

    private static void DrawNavRow(Graphics g, float s, RectangleF rect, string key, string arrow, string prevTitle, string prevArtist)
    {
        using (var keyBrush = new SolidBrush(C_Muted))
        using (var arrowBrush = new SolidBrush(Color.FromArgb(255, 143, 164, 189)))
        using (var valBrush = new SolidBrush(C_NavInk))
        using (var keyFmt = new StringFormat { LineAlignment = StringAlignment.Center })
        using (var valFmt = EllipsisFormat())
        {
            var keyW = 40f * s;
            g.DrawString(key, NavKeyFont(s), keyBrush, new RectangleF(rect.X, rect.Y, keyW, rect.Height), keyFmt);

            var arrowX = rect.X + keyW;
            g.DrawString(arrow, NavKeyFont(s), arrowBrush, new RectangleF(arrowX, rect.Y, 14f * s, rect.Height), keyFmt);

            var valX = arrowX + 14f * s;
            var text = string.IsNullOrEmpty(prevArtist) ? prevTitle : prevTitle + " — " + prevArtist;
            g.DrawString(text, NavFont(s), valBrush, new RectangleF(valX, rect.Y, rect.Right - valX, rect.Height), valFmt);
        }
    }

    private static void DrawCard(Graphics g, float s, RectangleF r, Color fill, Color border, bool accent)
    {
        using (var path = RoundRect(r, Radius * s))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(border, MF.Max(1f, s))) g.DrawPath(p, path);

            if (!accent) return;

            var old = g.Clip;
            g.SetClip(path);
            using (var b = new SolidBrush(C_Accent))
                g.FillRectangle(b, r.X, r.Y, AccentW * s, r.Height);
            g.Clip = old;
        }
    }

    private static RectangleF Shift(RectangleF r, float pad)
    {
        return new RectangleF(r.X + pad, r.Y + pad, r.Width, r.Height);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2f;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Fmt(TimeSpan t)
    {
        return ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
    }
}
