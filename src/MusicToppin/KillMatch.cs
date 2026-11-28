using System;

namespace MusicToppin;

/// <summary>击杀识别的纯逻辑（无 WinForms/Drawing 依赖，方便单元测试）。</summary>
internal static class KillMatch
{
    /// <summary>零均值归一化互相关，返回 -1..1（1 = 完全一致）。</summary>
    internal static double Ncc(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0;
        double ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;

        double num = 0, da = 0, db = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double va = a[i] - ma, vb = b[i] - mb;
            num += va * vb;
            da += va * va;
            db += vb * vb;
        }

        if (da <= 0 || db <= 0) return 0;
        return num / Math.Sqrt(da * db);
    }

    /// <summary>解析设置里保存的 "x,y,w,h" 区域；宽高至少 8。</summary>
    internal static bool TryParseRegion(string text, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y) ||
            !int.TryParse(parts[2], out w) || !int.TryParse(parts[3], out h)) return false;
        if (w < 8 || h < 8) return false;
        return true;
    }
}
