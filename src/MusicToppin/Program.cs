namespace MusicToppin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using (var mutex = new Mutex(true, @"Local\MusicToppin.SingleInstance", out var created))
        {
            if (!created) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 全局异常兜底：直播中宁可记日志继续跑，也不要弹崩溃对话框抢焦点
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Logger.Write("ui exception: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Write("fatal: " + e.ExceptionObject);

            var demo = false;
            var demoFast = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)) demo = true;
                if (string.Equals(a, "--demo-fast", StringComparison.OrdinalIgnoreCase)) { demo = true; demoFast = true; }
            }

            Application.Run(new TrayApp(demo, demoFast));
        }
    }
}
