using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MusicToppin.Tests
{
    /// <summary>B 站弹幕协议（S2）：封包、拆包、zlib 解包、DANMU_MSG 解析、房间号解析。
    /// 全部离线，不联网。</summary>
    internal static class DanmakuTests
    {
        public static void Run()
        {
            JsonTests();
            PacketTests();
            InflateTests();
            ParseTests();
            RoomIdTests();
        }

        private static void JsonTests()
        {
            TestRun.Section("JSON");

            var root = Json.Parse("{\"code\":0,\"data\":{\"room_id\":7734200,\"live_status\":1},\"list\":[1,2,3],\"ok\":true,\"nil\":null}");
            TestRun.Check(root["code"].AsInt == 0, "整数");
            TestRun.Check(root["data"]["room_id"].AsLong == 7734200, "嵌套对象");
            TestRun.Check(root["list"].Count == 3 && root["list"][2].AsInt == 3, "数组");
            TestRun.Check(root["ok"].AsBool, "布尔");
            TestRun.Check(root["nil"].IsNull, "null");
            TestRun.Check(root["missing"] == null, "缺失键返回 null");
            TestRun.Check(root["list"][99] == null, "数组越界返回 null");

            var escaped = Json.Parse("{\"s\":\"a\\\"b\\\\c\\nd\\u4e2d\\u6587\"}");
            TestRun.Check(escaped["s"].AsString == "a\"b\\c\nd中文", "转义与 unicode");

            var emoji = Json.Parse("{\"s\":\"\\ud83c\\udfa7\"}");
            TestRun.Check(emoji["s"].AsString == "🎧", "代理对（emoji）");

            var number = Json.Parse("{\"n\":-1.5e3}");
            TestRun.Check(Math.Abs(number["n"].AsDouble + 1500) < 0.001, "浮点/科学计数法");

            TestRun.Check(Json.Escape("a\"b\n中") == "\"a\\\"b\\n中\"", "Escape");

            var threw = false;
            try { Json.Parse("{\"a\":}"); }
            catch (FormatException) { threw = true; }
            TestRun.Check(threw, "坏 JSON 抛 FormatException");
        }

        private static void PacketTests()
        {
            TestRun.Section("封包 / 拆包");

            var auth = DanmakuProtocol.BuildAuthPacket(7734200, "tok en\"");
            TestRun.Check(auth.Length > DanmakuProtocol.HeaderSize, "鉴权包有内容");
            TestRun.Check(ReadInt32(auth, 0) == auth.Length, "总长度写在头里");
            TestRun.Check(((auth[4] << 8) | auth[5]) == 16, "头长度 16");
            TestRun.Check(((auth[6] << 8) | auth[7]) == 1, "协议版本 1");
            TestRun.Check(ReadInt32(auth, 8) == 7, "操作码 7（鉴权）");
            TestRun.Check(ReadInt32(auth, 12) == 1, "序号 1");

            var authBody = Encoding.UTF8.GetString(auth, 16, auth.Length - 16);
            var authJson = Json.Parse(authBody);
            TestRun.Check(authJson["roomid"].AsLong == 7734200, "鉴权体 roomid");
            TestRun.Check(authJson["protover"].AsInt == 2, "鉴权体 protover=2（要 zlib，不要 brotli）");
            TestRun.Check(authJson["uid"].AsLong == 0, "鉴权体游客 uid");
            TestRun.Check(authJson["key"].AsString == "tok en\"", "鉴权体 token 转义正确");

            var heartbeat = DanmakuProtocol.BuildHeartbeatPacket();
            TestRun.Check(ReadInt32(heartbeat, 8) == 2, "心跳操作码 2");
            TestRun.Check(Encoding.UTF8.GetString(heartbeat, 16, heartbeat.Length - 16) == "[object Object]", "心跳体");

            var first = DanmakuProtocol.BuildPacket(5, Encoding.UTF8.GetBytes("{\"cmd\":\"A\"}"));
            var second = DanmakuProtocol.BuildPacket(8, Encoding.UTF8.GetBytes("{\"code\":0}"), 1);
            var buffer = new List<byte>();
            buffer.AddRange(first);
            buffer.AddRange(second);

            DanmakuPacket packet;
            TestRun.Check(DanmakuProtocol.TryTakePacket(buffer, out packet) && packet.Operation == 5, "拆出第一个包");
            TestRun.Check(Encoding.UTF8.GetString(packet.Body) == "{\"cmd\":\"A\"}", "第一个包内容");
            TestRun.Check(DanmakuProtocol.TryTakePacket(buffer, out packet) && packet.Operation == 8, "拆出第二个包");
            TestRun.Check(buffer.Count == 0, "缓冲区清空");
            TestRun.Check(!DanmakuProtocol.TryTakePacket(buffer, out packet), "空缓冲区返回 false");

            var half = new List<byte>();
            half.AddRange(first);
            half.RemoveAt(half.Count - 1);
            TestRun.Check(!DanmakuProtocol.TryTakePacket(half, out packet), "半包返回 false");
            TestRun.Check(half.Count == first.Length - 1, "半包不丢数据");
        }

        private static void InflateTests()
        {
            TestRun.Section("zlib 解包");

            var payload = Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG\",\"info\":[]}");
            var zlib = ZlibCompress(payload);
            TestRun.Check(zlib[0] == 0x78, "测试数据是 zlib 头");
            var inflated = DanmakuProtocol.Inflate(zlib);
            TestRun.Check(Encoding.UTF8.GetString(inflated) == Encoding.UTF8.GetString(payload), "zlib 解包");

            var raw = DeflateRaw(payload);
            var inflatedRaw = DanmakuProtocol.Inflate(raw);
            TestRun.Check(Encoding.UTF8.GetString(inflatedRaw) == Encoding.UTF8.GetString(payload), "裸 deflate 兜底");

            var empty = DanmakuProtocol.Inflate(new byte[0]);
            TestRun.Check(empty.Length == 0, "空数据不炸");
        }

        private static void ParseTests()
        {
            TestRun.Section("DANMU_MSG 解析");

            var json =
                "{\"cmd\":\"DANMU_MSG\",\"info\":[" +
                "[0,1,25,16777215,1700000000000,0,0,\"\",0,0,0,\"\",0,0,0,0,0,0]," +
                "\"点歌 起风了\"," +
                "[12345678,\"观众A\",0,0,0,10000,1,\"\"]," +
                "[]," +
                "[],[\"\",\"\"],[],[\"12\",\"真爱粉\",\"主播\",\"9527\",0,0,0]]}";

            var message = DanmakuProtocol.ParseDanmaku(Json.Parse(json));
            TestRun.Check(message != null, "能解析");
            TestRun.Check(message.Text == "点歌 起风了", "弹幕文本");
            TestRun.Check(message.UserName == "观众A", "用户名");
            TestRun.Check(message.Uid == 12345678, "uid");
            TestRun.Check(!message.IsAdmin, "非房管");
            TestRun.Check(message.MedalLevel == 12 && message.MedalName == "真爱粉", "粉丝牌");

            var admin = DanmakuProtocol.ParseDanmaku(Json.Parse(
                "{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"点歌 BV1xx411c7mD\",[1,\"房管B\",1,0]]}"));
            TestRun.Check(admin != null && admin.IsAdmin, "房管标记");

            var suffix = DanmakuProtocol.ParseDanmaku(Json.Parse(
                "{\"cmd\":\"DANMU_MSG:4:0:2:2:2:0\",\"info\":[[],\"点歌 群青\",[2,\"观众C\",0,0]]}"));
            TestRun.Check(suffix != null && suffix.Text == "点歌 群青", "带后缀的 cmd");

            TestRun.Check(DanmakuProtocol.ParseDanmaku(Json.Parse("{\"cmd\":\"SEND_GIFT\",\"data\":{}}")) == null, "非弹幕返回 null");
            TestRun.Check(DanmakuProtocol.ParseDanmaku(Json.Parse("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"  \",[1,\"x\",0,0]]}")) == null, "空白弹幕返回 null");
            TestRun.Check(DanmakuProtocol.ParseDanmaku(null) == null, "null 返回 null");
        }

        private static void RoomIdTests()
        {
            TestRun.Section("房间号");

            TestRun.Check(BilibiliLive.ParseRoomId("6") == 6, "纯数字");
            TestRun.Check(BilibiliLive.ParseRoomId("https://live.bilibili.com/7734200?x=1") == 7734200, "直播链接");
            TestRun.Check(BilibiliLive.ParseRoomId("live.bilibili.com/123") == 123, "裸域名");
            TestRun.Check(BilibiliLive.ParseRoomId("  9527 ") == 9527, "前后空格");
            TestRun.Check(BilibiliLive.ParseRoomId("") == 0, "空串");
            TestRun.Check(BilibiliLive.ParseRoomId("没有数字") == 0, "没有数字");
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        private static byte[] DeflateRaw(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            var raw = DeflateRaw(data);
            var result = new byte[raw.Length + 6];
            result[0] = 0x78;
            result[1] = 0x9C;
            Buffer.BlockCopy(raw, 0, result, 2, raw.Length);

            var adler = Adler32(data);
            result[result.Length - 4] = (byte)(adler >> 24);
            result[result.Length - 3] = (byte)(adler >> 16);
            result[result.Length - 2] = (byte)(adler >> 8);
            result[result.Length - 1] = (byte)adler;
            return result;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
