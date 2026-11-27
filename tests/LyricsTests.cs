using System;

namespace MusicToppin.Tests
{
    /// <summary>LRC 歌词解析测试。</summary>
    internal static class LyricsTests
    {
        public static void Run()
        {
            TestRun.Section("歌词解析");

            var lines = LyricsParser.Parse("[00:01.00]第一句\n[00:03.00][00:04.00]第二句\n[ti:歌名]\n[00:02.50] 第三句 ");
            TestRun.Check(lines.Count == 4, "多时间戳解析");
            TestRun.Check(lines[0].Text == "第一句" && Math.Abs(lines[0].Time.TotalSeconds - 1) < 0.001, "时间排序/首句");
            TestRun.Check(lines[1].Text == "第三句" && Math.Abs(lines[1].Time.TotalSeconds - 2.5) < 0.001, "第三句时间");
            TestRun.Check(lines[2].Text == "第二句" && Math.Abs(lines[2].Time.TotalSeconds - 3) < 0.001, "多时间戳第一条");
            TestRun.Check(lines[3].Text == "第二句" && Math.Abs(lines[3].Time.TotalSeconds - 4) < 0.001, "多时间戳第二条");

            TestRun.Check(LyricsParser.Parse("[ti:歌名]\n\n").Count == 0, "纯元数据为空");
            TestRun.Check(LyricsParser.Parse("[00:0a.00]无效").Count == 0, "非法时间");
            TestRun.Check(LyricsParser.Parse("[01:02.345]毫秒").Count == 1, "毫秒时间");
            TestRun.Check(LyricsParser.Parse(" [00:01.00] 前导空格 ").Count == 0, "行前空格不解析");
        }
    }
}
