using System;

namespace MusicToppin.Tests
{
    /// <summary>极简断言器：两个测试文件共用一份计数，最后统一汇总。
    /// 项目不引测试框架（否则构建要拉依赖），跑 exe 看输出即可。</summary>
    internal static class TestRun
    {
        public static int Pass;
        public static int Fail;

        public static void Check(bool condition, string name)
        {
            if (condition)
            {
                Pass++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                Fail++;
                Console.WriteLine("  FAIL  " + name);
            }
        }

        public static void Section(string title)
        {
            Console.WriteLine("== " + title + " ==");
        }

        public static int Summarize()
        {
            Console.WriteLine();
            Console.WriteLine("通过 " + Pass + " 项，失败 " + Fail + " 项");
            return Fail == 0 ? 0 : 1;
        }
    }
}
