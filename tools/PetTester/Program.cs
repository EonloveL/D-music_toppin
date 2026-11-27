using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MusicToppin;

/// <summary>
/// 独立 SVG 宠物测试器：无需启动主程序，直接看三套形象和全部动作。
/// 复用 src/MusicToppin/SvgPet.cs 的页面，编译时一起编入。
/// </summary>
internal static class PetTester
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new Form
        {
            Text = "Music Toppin · SVG Pet Tester",
            Width = 560,
            Height = 640,
            MinimumSize = new Size(460, 520),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(18, 20, 26)
        };

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        form.Shown += async (_, __) =>
        {
            try
            {
                await web.EnsureCoreWebView2Async();
                web.DefaultBackgroundColor = Color.FromArgb(18, 20, 26);
                web.CoreWebView2.NavigateToString(SvgPet.Page);

                // 让演示按钮常显（NavigateToString 没有 query，页面默认隐藏 demo 面板）
                await Task.Delay(600);
                await web.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('demo').classList.remove('hidden');");
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, "WebView2 初始化失败：" + ex.Message, "SVG Pet Tester",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.Close();
            }
        };

        Application.Run(form);
    }
}
