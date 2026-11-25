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
