using System;
using System.Text;

namespace MusicToppin.Tests
{
    /// <summary>叠加层 HTTP Range / BV 号校验测试。</summary>
    internal static class OverlayTests
    {
        public static void Run()
        {
            TestRun.Section("叠加层 Range / BV");

            // OW 页面结构回归：只允许一张宠物图，禁止旧的 base+tail 分层（会导致重复猫）
            var page = OverlayServer.PageHtml;
            TestRun.Check(page.Contains("id=\"" + "owPetImg" + "\""), "OW 单图宠物");
            TestRun.Check(page.Split(new[] { "id=\"" + "owPetImg" + "\"" }, StringSplitOptions.None).Length == 2, "OW 只有一张宠物图");
            TestRun.Check(!page.Contains("clipPath") && !page.Contains("ow-pet-tail") && !page.Contains("ow-pet-base"), "OW 无尾巴分层");
            TestRun.Check(page.Contains("st.pageVersion"), "OW 页面版本握手");

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

            // 回归：歌词 JSON 曾输出 ,, 导致浏览器解析失败、守望先锋模式歌词/主题全不显示
            var lyricsJson = new StringBuilder();
            lyricsJson.Append('{');
            OverlayState.AppendString(lyricsJson, "style", "3");
            lyricsJson.Append(',');
            OverlayState.AppendLyricLines(lyricsJson, new[]
            {
                new LyricLine { Time = TimeSpan.Zero, Text = "第一行" },
                new LyricLine { Time = TimeSpan.FromSeconds(1.5), Text = "a \"quote\" \\ 行" }
            });
            lyricsJson.Append('}');
            var parsed = Json.Parse(lyricsJson.ToString());
            TestRun.Check(parsed["lines"].Count == 2, "歌词 JSON 合法（无 ,,）");
            TestRun.Check(parsed["lines"][1]["text"].AsString == "a \"quote\" \\ 行", "歌词文本转义");

            var emptyJson = new StringBuilder();
            emptyJson.Append('{');
            OverlayState.AppendLyricLines(emptyJson, new LyricLine[0]);
            emptyJson.Append('}');
            TestRun.Check(Json.Parse(emptyJson.ToString())["lines"].Count == 0, "空歌词数组合法");
        }
    }
}
