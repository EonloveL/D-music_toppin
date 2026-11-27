using System;

namespace MusicToppin.Tests
{
    /// <summary>叠加层 HTTP Range / BV 号校验测试。</summary>
    internal static class OverlayTests
    {
        public static void Run()
        {
            TestRun.Section("叠加层 Range / BV");

            long start;
            long end;

            TestRun.Check(OverlayServer.TryParseRange("bytes=0-99", 1000, out start, out end) && start == 0 && end == 99, "普通 Range");
            TestRun.Check(OverlayServer.TryParseRange("bytes=500-", 1000, out start, out end) && start == 500 && end == 999, "开放 Range");
            TestRun.Check(OverlayServer.TryParseRange("bytes=-100", 1000, out start, out end) && start == 900 && end == 999, "尾部 Range");
            TestRun.Check(!OverlayServer.TryParseRange("bytes=1000-1100", 1000, out start, out end), "越界 Range");
            TestRun.Check(!OverlayServer.TryParseRange("bytes=900-800", 1000, out start, out end), "倒置 Range");
            TestRun.Check(!OverlayServer.TryParseRange("items=0-1", 1000, out start, out end), "非 bytes");
            TestRun.Check(!OverlayServer.TryParseRange("", 1000, out start, out end), "空 Range");

            long rangeStart;
            TestRun.Check(OverlayServer.TryGetRangeStart("bytes=123-456", out rangeStart) && rangeStart == 123, "Range 起点解析");
            TestRun.Check(!OverlayServer.TryGetRangeStart("bytes=-99", out rangeStart), "尾部 Range 无起点");

            TestRun.Check(OverlayServer.IsValidBvid("BV1xx411c7mD"), "合法 BV");
            TestRun.Check(!OverlayServer.IsValidBvid("bv1xx411c7m"), "长度不足");
            TestRun.Check(!OverlayServer.IsValidBvid("AV1234567890"), "非 BV 前缀");
            TestRun.Check(!OverlayServer.IsValidBvid("BV1xx411c7m!"), "非法字符");
            TestRun.Check(!OverlayServer.IsValidBvid(""), "空 BV");
        }
    }
}
