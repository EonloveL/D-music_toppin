using System;

namespace MusicToppin.Tests
{
    /// <summary>击杀识别的纯逻辑：NCC 匹配、区域解析。</summary>
    internal static class KillTests
    {
        public static void Run()
        {
            TestRun.Section("击杀匹配");

            var a = new byte[144];
            for (var i = 0; i < a.Length; i++) a[i] = (byte)((i * 7) % 256);
            TestRun.Check(Math.Abs(KillMatch.Ncc(a, a) - 1.0) < 1e-9, "自匹配 = 1");

            var inverted = new byte[a.Length];
            for (var i = 0; i < a.Length; i++) inverted[i] = (byte)(255 - a[i]);
            TestRun.Check(Math.Abs(KillMatch.Ncc(a, inverted) + 1.0) < 1e-9, "取反 = -1");

            var flat = new byte[a.Length];
            TestRun.Check(KillMatch.Ncc(a, flat) == 0, "零方差 = 0");
            TestRun.Check(KillMatch.Ncc(a, new byte[10]) == 0, "长度不一致 = 0");
            TestRun.Check(KillMatch.Ncc(null, a) == 0, "null = 0");

            int x, y, w, h;
            TestRun.Check(KillMatch.TryParseRegion("100,200,320,80", out x, out y, out w, out h) &&
                          x == 100 && y == 200 && w == 320 && h == 80, "区域解析");
            TestRun.Check(KillMatch.TryParseRegion("-50,10,300,80", out x, out y, out w, out h) && x == -50, "负坐标区域");
            TestRun.Check(!KillMatch.TryParseRegion("100,200,4,80", out x, out y, out w, out h), "区域过窄拒绝");
            TestRun.Check(!KillMatch.TryParseRegion("100,200,320", out x, out y, out w, out h), "字段不足拒绝");
            TestRun.Check(!KillMatch.TryParseRegion("a,b,c,d", out x, out y, out w, out h), "非数字拒绝");
            TestRun.Check(!KillMatch.TryParseRegion("", out x, out y, out w, out h), "空区域拒绝");
        }
    }
}
