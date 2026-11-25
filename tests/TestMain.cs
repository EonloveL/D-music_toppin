using System;

namespace MusicToppin.Tests
{
    /// <summary>入口：跑完所有测试，用退出码表示结果（0 = 全过）。</summary>
    internal static class TestMain
    {
        private static int Main()
        {
            RequestTests.Run();
            DanmakuTests.Run();
            NeteaseTests.Run();
            return TestRun.Summarize();
        }
    }
}
