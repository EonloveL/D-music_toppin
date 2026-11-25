using System;

namespace MusicToppin.Tests
{
    /// <summary>ini 解析（QQ 音乐 / 酷狗登录态靠它）。纯离线。</summary>
    internal static class IniFileTests
    {
        public static void Run()
        {
            TestRun.Section("ini 解析");

            var comData =
                "[SectQMAPI]\r\n" +
                "uin=0\r\n" +
                "path=C:\\Program Files (x86)\\Tencent\\QQMusic\\QQMusic.exe\r\n" +
                "\r\n" +
                "[Other]\r\n" +
                "uin=999\r\n";

            TestRun.Check(IniFile.Parse(comData, "SectQMAPI", "uin") == "0", "QQ 音乐：uin=0（未登录）");
            TestRun.Check(IniFile.Parse(comData.Replace("uin=0", "uin=123456"), "SectQMAPI", "uin") == "123456", "QQ 音乐：uin=123456（已登录）");
            TestRun.Check(IniFile.Parse(comData, "Other", "uin") == "999", "分段不串值");
            TestRun.Check(IniFile.Parse(comData, "SectQMAPI", "path").EndsWith("QQMusic.exe"), "路径值");
            TestRun.Check(IniFile.Parse(comData, "SectQMAPI", "missing") == "", "缺键返回空");
            TestRun.Check(IniFile.Parse(comData, "NoSuchSection", "uin") == "", "缺段落返回空");
            TestRun.Check(IniFile.Parse(comData, "sectqmap i", "uin") == "", "段落名要完全匹配");

            var lower = "[SectQMAPI]\r\nuIn = 777\r\n";
            TestRun.Check(IniFile.Parse(lower, "SECTQMAPI", "UIN") == "777", "段落/键名大小写不敏感");
            TestRun.Check(IniFile.Parse(lower, "SectQMAPI", "uin") == "777", "值前后空格被裁掉");

            var comments = "; 注释\n# 也是注释\n[Sec]\n; uin=1\nuin=42\n";
            TestRun.Check(IniFile.Parse(comments, "Sec", "uin") == "42", "注释行被忽略");

            TestRun.Check(IniFile.Parse("uin=5\n", "", "uin") == "5", "不限段落");
            TestRun.Check(IniFile.Parse("", "Sec", "uin") == "", "空内容");
            TestRun.Check(IniFile.Parse(comments, "Sec", "") == "", "空键");
            TestRun.Check(IniFile.Read(@"Z:\definitely\not\here.ini", "Sec", "uin") == "", "文件不存在返回空");
        }
    }
}
