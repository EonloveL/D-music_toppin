using System;

namespace MusicToppin.Tests
{
    /// <summary>「点歌 歌名」自动选平台：只登录一个就用它，都登录网易云优先。</summary>
    internal static class ResolverTests
    {
        public static void Run()
        {
            TestRun.Section("自动选平台");

            var none = SongResolvers.AutoOrder(neteaseLoggedIn: false, qqLoggedIn: false);
            TestRun.Check(none.Count == 2 && none[0] == SongSource.Netease && none[1] == SongSource.QQ, "都没登录：网易云优先（游客也能放免费曲）");

            var onlyNetease = SongResolvers.AutoOrder(neteaseLoggedIn: true, qqLoggedIn: false);
            TestRun.Check(onlyNetease[0] == SongSource.Netease, "只登录网易云：走网易云");

            var onlyQQ = SongResolvers.AutoOrder(neteaseLoggedIn: false, qqLoggedIn: true);
            TestRun.Check(onlyQQ[0] == SongSource.QQ, "只登录 QQ：点歌走 QQ");

            var both = SongResolvers.AutoOrder(neteaseLoggedIn: true, qqLoggedIn: true);
            TestRun.Check(both[0] == SongSource.Netease && both[1] == SongSource.QQ, "都登录：网易云优先、QQ 兜底");

            // 兜底顺序里不能有重复平台
            TestRun.Check(onlyQQ[0] != onlyQQ[1], "兜底平台不重复");
        }
    }
}
