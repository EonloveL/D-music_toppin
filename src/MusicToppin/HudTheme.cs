namespace MusicToppin;

/// <summary>浮窗配色。用户可以分别改主题色、歌名、歌手、卡片背景。</summary>
internal sealed class HudTheme
{
    public Color Accent = Color.FromArgb(255, 255, 59, 48);
    public Color Title = Color.FromArgb(255, 245, 248, 252);
    public Color Artist = Color.FromArgb(255, 159, 176, 198);
    public Color Card = Color.FromArgb(246, 14, 17, 23);

    /// <summary>背景不透明度 0~1（由托盘「背景透明度」调节）。</summary>
    public float CardOpacity = 0.96f;

    public static HudTheme Default
    {
        get { return new HudTheme(); }
    }

    /// <summary>把 fg 按 amount（0~1）叠到 bg 上，用来派生次级文字颜色。</summary>
    public static Color Blend(Color fg, Color bg, float amount)
    {
        if (amount < 0f) amount = 0f;
        if (amount > 1f) amount = 1f;
        var r = (int)(bg.R + (fg.R - bg.R) * amount);
        var g = (int)(bg.G + (fg.G - bg.G) * amount);
        var b = (int)(bg.B + (fg.B - bg.B) * amount);
        return Color.FromArgb(255, Clamp(r), Clamp(g), Clamp(b));
    }

    private static int Clamp(int v)
    {
        if (v < 0) return 0;
        if (v > 255) return 255;
        return v;
    }

    public static Color WithAlpha(Color color, int alpha)
    {
        if (alpha < 0) alpha = 0;
        if (alpha > 255) alpha = 255;
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
