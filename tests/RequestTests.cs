using System;

namespace MusicToppin.Tests
{
    /// <summary>点歌解析 + 队列核心（S1）的回归测试。</summary>
    internal static class RequestTests
    {
        private static SongRequest Parse(string text, string user = "观众A")
        {
            SongRequest request;
            SongRequestParser.TryParse(text, user, 1001, out request);
            return request;
        }

        public static void Run()
        {
            TestRun.Section("解析");

            var request = Parse("点歌 起风了");
            TestRun.Check(request != null && request.Source == SongSource.Auto && request.Query == "起风了" && !request.IsVideo, "默认前缀");

            request = Parse("点歌 wy 起风了");
            TestRun.Check(request != null && request.Source == SongSource.Netease && request.Query == "起风了", "wy 前缀");

            request = Parse("点歌 qq 夜に駆ける");
            TestRun.Check(request != null && request.Source == SongSource.QQ && request.Query == "夜に駆ける", "qq 前缀");

            request = Parse("点歌 am Lemon");
            TestRun.Check(request != null && request.Source == SongSource.AppleMusic && request.Query == "Lemon", "am 前缀");

            request = Parse("點歌 起风了");
            TestRun.Check(request != null && request.Query == "起风了", "繁体點歌");

            TestRun.Check(Parse("点歌BV1xx411c7mD") == null, "缺少空格不解析");
            TestRun.Check(Parse("点歌姬 你好") == null, "点歌姬不误判");
            TestRun.Check(Parse("点歌   ") == null, "空内容");

            request = Parse("点歌 BV1xx411c7mD");
            TestRun.Check(request != null && request.Source == SongSource.Bilibili && request.IsVideo && request.Query == "BV1xx411c7mD", "BV 号");

            request = Parse("点歌 bilibili BV1xx411c7mD");
            TestRun.Check(request != null && request.IsVideo && request.Query == "BV1xx411c7mD", "前缀 + BV");

            request = Parse("点歌 id:123456");
            TestRun.Check(request != null && request.IsId && request.Source == SongSource.Netease && request.Query == "123456", "id 点歌");

            request = Parse("点歌 qq id:999");
            TestRun.Check(request != null && request.IsId && request.Source == SongSource.QQ && request.Query == "999", "qq + id");

            request = Parse("点歌 https://b23.tv/abcd");
            TestRun.Check(request != null && request.IsVideo && request.Source == SongSource.Bilibili, "b23 短链");

            request = Parse("qq I don't care Ed Sheeran");
            TestRun.Check(request != null && request.Source == SongSource.QQ && request.Query == "I don't care Ed Sheeran", "平台前缀直接开头（qq 歌名）");

            request = Parse("wy 起风了");
            TestRun.Check(request != null && request.Source == SongSource.Netease && request.Query == "起风了", "平台前缀直接开头（wy 歌名）");

            request = Parse("bv BV1xx411c7mD");
            TestRun.Check(request != null && request.IsVideo && request.Source == SongSource.Bilibili, "平台前缀直接开头（bv）");

            request = Parse("点歌：起风了");
            TestRun.Check(request != null && request.Query == "起风了", "点歌后带冒号");

            request = Parse("点歌qq 起风了");
            TestRun.Check(request != null && request.Source == SongSource.QQ && request.Query == "起风了", "点歌紧跟平台前缀");

            TestRun.Check(Parse("qq") == null, "只有平台前缀不解析");

            TestRun.Section("队列");

            var queue = new RequestQueue { MaxSize = 3, UserCooldown = TimeSpan.FromSeconds(30), SongCooldown = TimeSpan.FromMinutes(5) };

            var first = queue.Enqueue(Parse("点歌 起风了", "观众A"));
            TestRun.Check(first.Accepted && first.Position == 1, "第一首入队");

            var duplicate = queue.Enqueue(Parse("点歌 起风了", "观众B"));
            TestRun.Check(!duplicate.Accepted && duplicate.Reason == QueueRejectReason.SongCooldown, "同曲冷却拦截");

            var userCooldown = queue.Enqueue(Parse("点歌 群青", "观众A"));
            TestRun.Check(!userCooldown.Accepted && userCooldown.Reason == QueueRejectReason.UserCooldown, "用户冷却拦截");

            var second = queue.Enqueue(Parse("点歌 群青", "观众B"));
            TestRun.Check(second.Accepted && second.Position == 2, "第二首入队");

            var third = queue.Enqueue(Parse("点歌 Lemon", "观众C"));
            TestRun.Check(third.Accepted && third.Position == 3, "第三首入队");

            var full = queue.Enqueue(Parse("点歌 群青2", "观众D"));
            TestRun.Check(!full.Accepted && full.Reason == QueueRejectReason.Full, "队列上限");

            var current = queue.TakeNext();
            TestRun.Check(current != null && current.Request.Query == "起风了", "FIFO 出队");
            TestRun.Check(queue.Current == current && queue.Count == 2, "当前项与剩余数");

            TestRun.Check(queue.Promote(queue.Items[1].Id), "顶歌");
            TestRun.Check(queue.Items[0].Request.Query == "Lemon", "顶歌后排第一");

            TestRun.Check(queue.RemoveByRequester("观众B"), "撤回自己的点歌");

            queue.ClearCurrent();
            TestRun.Check(queue.Current == null, "清空当前项");

            var loose = new RequestQueue { MaxSize = 5, UserCooldown = TimeSpan.Zero, SongCooldown = TimeSpan.Zero };
            TestRun.Check(loose.Enqueue(Parse("点歌 起风了", "观众A")).Accepted, "冷却为 0 时可重复点");
            TestRun.Check(loose.Enqueue(Parse("点歌 起风了", "观众A")).Accepted, "冷却为 0 时同用户可连点");

            // 弹幕撤回：按序号 / 撤回自己的 / 按歌名
            var removeQueue = new RequestQueue { MaxSize = 5, UserCooldown = TimeSpan.Zero, SongCooldown = TimeSpan.Zero };
            var r1 = Parse("点歌 起风了", "甲");
            var r2 = Parse("点歌 群青", "乙");
            var r3 = Parse("点歌 Lemon", "丙");
            r1.RequesterUid = 2001;
            r2.RequesterUid = 2002;
            r3.RequesterUid = 2003;
            removeQueue.Enqueue(r1);
            removeQueue.Enqueue(r2);
            removeQueue.Enqueue(r3);

            var removed = removeQueue.RemoveAt(2);
            TestRun.Check(removed != null && removed.Request.Query == "群青" && removeQueue.Count == 2, "按序号撤回待播歌曲");

            removed = removeQueue.RemoveByRequesterUid(2003);
            TestRun.Check(removed != null && removed.Request.Query == "Lemon" && removeQueue.Count == 1, "撤回自己的待播歌曲");

            TestRun.Check(removeQueue.RemoveAt(99) == null, "序号越界不删");

            var keywordQueue = new RequestQueue { MaxSize = 5, UserCooldown = TimeSpan.Zero, SongCooldown = TimeSpan.Zero };
            keywordQueue.Enqueue(Parse("点歌 起风了", "甲"));
            keywordQueue.Enqueue(Parse("点歌 群青", "乙"));
            int matches;
            removed = keywordQueue.RemoveByKeyword("起风", out matches);
            TestRun.Check(removed != null && matches == 1 && keywordQueue.Count == 1, "按歌名撤回待播歌曲");

            removed = keywordQueue.RemoveByKeyword("群青", out matches);
            TestRun.Check(removed != null && keywordQueue.Count == 0, "按歌名撤回最后一首");
        }
    }
}
