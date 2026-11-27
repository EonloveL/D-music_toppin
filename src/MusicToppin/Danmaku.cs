using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicToppin;

/// <summary>礼物 / 上舰事件（喂给叠加层宠物互动用）。</summary>
internal sealed class DanmakuGift
{
    public string Kind = "";      // gift / up
    public string UserName = "";
    public string GiftName = "";
    public int Count;
    public int GuardLevel;
}

/// <summary>一条直播间弹幕（只保留点歌用得上的字段）。</summary>
internal sealed class DanmakuMessage
{
    public string Text = "";
    public string UserName = "";
    public long Uid;
    public bool IsAdmin;

    /// <summary>粉丝牌等级（0 = 没挂粉丝牌）。</summary>
    public int MedalLevel;
    public string MedalName = "";
    public string MedalAnchorName = "";

    /// <summary>粉丝牌所属主播的房间号，用来判断是不是「本直播间的灯牌」。</summary>
    public long MedalRoomId;

    /// <summary>大航海等级：0 = 无，1 = 总督，2 = 提督，3 = 舰长。</summary>
    public int GuardLevel;

    /// <summary>本地调试/「测试点歌」注入的假弹幕，不受点歌/切歌权限限制。</summary>
    public bool IsLocalTest;

    public DateTime ReceivedUtc = DateTime.UtcNow;

    /// <summary>是否挂着粉丝团灯牌（有牌子等级和名称）。</summary>
    public bool HasMedal
    {
        get { return MedalLevel > 0 && !string.IsNullOrWhiteSpace(MedalName); }
    }

    /// <summary>是否大航海（舰长 / 提督 / 总督都算）。</summary>
    public bool IsGuard
    {
        get { return GuardLevel > 0; }
    }
}

/// <summary>
/// 弹幕点歌 / 切歌的权限判定（纯函数，方便单元测试）。
/// 点歌：所有人 / 粉丝团灯牌或舰长 / 仅舰长 / 关闭。
/// 切歌：所有人 / 仅点歌人（房管可切全部）/ 仅房管 / 关闭。
/// </summary>
internal static class DanmakuPermissions
{
    public const int RequestAll = 0;
    public const int RequestFansOrGuard = 1;
    public const int RequestGuardOnly = 2;
    public const int RequestOff = 3;

    public const int SkipAll = 0;
    public const int SkipRequester = 1;
    public const int SkipAdmin = 2;
    public const int SkipOff = 3;

    public static bool CanRequest(int policy, bool hasMedal, bool isGuard)
    {
        switch (policy)
        {
            case RequestAll: return true;
            case RequestFansOrGuard: return hasMedal || isGuard;
            case RequestGuardOnly: return isGuard;
            default: return false;
        }
    }

    public static string RequestDeniedMessage(int policy)
    {
        switch (policy)
        {
            case RequestFansOrGuard: return "点歌需要粉丝团灯牌或舰长";
            case RequestGuardOnly: return "点歌仅限舰长 / 大航海";
            case RequestOff: return "点歌功能已关闭";
            default: return "";
        }
    }

    public static bool CanSkip(int policy, bool isAdmin, bool isRequester)
    {
        switch (policy)
        {
            case SkipAll: return true;
            case SkipRequester: return isAdmin || isRequester;
            case SkipAdmin: return isAdmin;
            default: return false;
        }
    }

    public static string SkipDeniedMessage(int policy)
    {
        switch (policy)
        {
            case SkipRequester: return "普通观众只能切自己点的歌；房管可以切全部";
            case SkipAdmin: return "现在只有房管可以切歌";
            case SkipOff: return "弹幕切歌已关闭";
            default: return "";
        }
    }
}

/// <summary>拆出来的一个协议包。</summary>
internal sealed class DanmakuPacket
{
    public int Version;
    public int Operation;
    public byte[] Body = Array.Empty<byte>();
}

internal sealed class LiveRoomInfo
{
    public long RoomId;
    public long ShortId;
    public int LiveStatus;
}

internal sealed class DanmakuEndpoint
{
    public string Token = "";
    public readonly List<string> WssHosts = new List<string>();
}

/// <summary>
/// B 站直播弹幕协议：16 字节头（长度 / 头长 / 协议版本 / 操作码 / 序号）+ body。
/// 这里只做纯函数部分（封包、拆包、解压、解析），方便离线跑单元测试。
///
/// 头布局（大端）：
///   0..4  总长度
///   4..6  头长度（固定 16）
///   6..8  协议版本：0=普通 JSON，1=心跳/人气，2=zlib 压缩，3=brotli 压缩
///   8..12 操作码：2=心跳，3=心跳回复(人气)，5=通知，7=鉴权，8=鉴权回复
///   12..16 序号
/// </summary>
internal static class DanmakuProtocol
{
    public const int HeaderSize = 16;
    public const int OpHeartbeat = 2;
    public const int OpHeartbeatReply = 3;
    public const int OpMessage = 5;
    public const int OpAuth = 7;
    public const int OpAuthReply = 8;

    private static readonly byte[] HeartbeatBody = Encoding.UTF8.GetBytes("[object Object]");

    public static byte[] BuildPacket(int operation, byte[] body, int version = 1)
    {
        body = body ?? Array.Empty<byte>();

        var packet = new byte[HeaderSize + body.Length];
        WriteInt32(packet, 0, packet.Length);
        WriteInt16(packet, 4, HeaderSize);
        WriteInt16(packet, 6, version);
        WriteInt32(packet, 8, operation);
        WriteInt32(packet, 12, 1);
        if (body.Length > 0) Buffer.BlockCopy(body, 0, packet, HeaderSize, body.Length);
        return packet;
    }

    /// <summary>鉴权包：游客 uid=0 即可；protover=3 让服务器回 brotli（客户端内置解码）。</summary>
    public static byte[] BuildAuthPacket(long roomId, string token, long uid = 0, int protoVersion = 3)
    {
        var buvid = BilibiliLive.ExtractCookieValue("buvid3");

        var body =
            "{\"uid\":" + uid.ToString() + ",\"roomid\":" + roomId.ToString() +
            ",\"protover\":" + protoVersion.ToString() +
            ",\"platform\":\"web\",\"type\":2,\"key\":" + Json.Escape(token ?? "") +
            (string.IsNullOrEmpty(buvid) ? "" : ",\"buvid\":\"" + buvid + "\"") + "}";

        return BuildPacket(OpAuth, Encoding.UTF8.GetBytes(body));
    }

    public static byte[] BuildHeartbeatPacket()
    {
        return BuildPacket(OpHeartbeat, HeartbeatBody);
    }

    /// <summary>从累积缓冲区里取出一个完整包；不够一个包时返回 false。</summary>
    public static bool TryTakePacket(List<byte> buffer, out DanmakuPacket packet)
    {
        packet = null;
        if (buffer == null || buffer.Count < HeaderSize) return false;

        var total = ReadInt32(buffer, 0);
        if (total < HeaderSize || total > 16 * 1024 * 1024)
        {
            // 长度明显不对：丢掉整个缓冲区，让上层重连，比死循环安全
            buffer.Clear();
            throw new InvalidDataException("弹幕包长度异常：" + total);
        }
        if (buffer.Count < total) return false;

        var headerLength = (buffer[4] << 8) | buffer[5];
        if (headerLength < HeaderSize || headerLength > total) headerLength = HeaderSize;

        var version = (buffer[6] << 8) | buffer[7];
        var operation = ReadInt32(buffer, 8);

        var body = new byte[total - headerLength];
        for (var i = 0; i < body.Length; i++) body[i] = buffer[headerLength + i];

        buffer.RemoveRange(0, total);
        packet = new DanmakuPacket { Version = version, Operation = operation, Body = body };
        return true;
    }

    /// <summary>解 zlib（B 站的压缩包带 2 字节头和 4 字节 Adler32，DeflateStream 只吃裸 deflate）。</summary>
    public static byte[] Inflate(byte[] data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();

        // zlib 头：低 4 位必须是 8（deflate），且 (CMF<<8|FLG) % 31 == 0
        if (data.Length > 6 && (data[0] & 0x0F) == 8 && (((data[0] << 8) | data[1]) % 31) == 0)
        {
            var raw = new byte[data.Length - 6];
            Buffer.BlockCopy(data, 2, raw, 0, raw.Length);
            try { return InflateRaw(raw); }
            catch (Exception ex) { Logger.Write("danmaku inflate(zlib): " + ex.Message); }
        }

        return InflateRaw(data);
    }

    private static byte[] InflateRaw(byte[] payload)
    {
        using (var input = new MemoryStream(payload))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    /// <summary>把一条 DANMU_MSG 的 JSON 解析成消息；不是弹幕返回 null。</summary>
    /// <summary>解析 SEND_GIFT / GUARD_BUY；不是礼物返回 null。</summary>
    public static DanmakuGift ParseGift(JsonValue root)
    {
        if (root == null) return null;

        var cmd = root["cmd"] != null ? root["cmd"].AsString : null;
        if (string.IsNullOrEmpty(cmd)) return null;

        var isGift = cmd.IndexOf("SEND_GIFT", StringComparison.Ordinal) >= 0;
        var isGuard = cmd.IndexOf("GUARD_BUY", StringComparison.Ordinal) >= 0;
        if (!isGift && !isGuard) return null;

        var data = root["data"];
        if (data == null || data.Kind != JsonKind.Object) return null;

        if (isGuard)
        {
            return new DanmakuGift
            {
                Kind = "up",
                UserName = data["username"] != null ? data["username"].AsString : "",
                GuardLevel = data["guard_level"] != null ? data["guard_level"].AsInt : 0
            };
        }

        return new DanmakuGift
        {
            Kind = "gift",
            UserName = data["uname"] != null ? data["uname"].AsString : "",
            GiftName = data["giftName"] != null ? data["giftName"].AsString : "",
            Count = data["num"] != null ? data["num"].AsInt : 1
        };
    }

    public static DanmakuMessage ParseDanmaku(JsonValue root)
    {
        if (root == null) return null;

        var cmd = root["cmd"] != null ? root["cmd"].AsString : null;
        if (string.IsNullOrEmpty(cmd) || cmd.IndexOf("DANMU_MSG", StringComparison.Ordinal) < 0) return null;

        var info = root["info"];
        if (info == null || info.Kind != JsonKind.Array || info.Count < 3) return null;

        var text = info[1] != null ? info[1].AsString : null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var user = info[2];
        var message = new DanmakuMessage
        {
            Text = text.Trim(),
            Uid = user != null ? user[0].AsLong : 0,
            UserName = user != null && user[1] != null ? user[1].AsString : "",
            IsAdmin = user != null && user[2] != null && user[2].AsBool
        };

        // 粉丝牌在 info[3]：[等级, 牌子名, 主播名, 房间号, 颜色..., 大航海类型(10)]
        var medal = info[3];
        if (medal != null && medal.Kind == JsonKind.Array && medal.Count >= 2)
        {
            message.MedalLevel = medal[0] != null ? medal[0].AsInt : 0;
            message.MedalName = medal[1] != null ? (medal[1].AsString ?? "") : "";
            message.MedalAnchorName = medal[2] != null ? (medal[2].AsString ?? "") : "";
            message.MedalRoomId = medal[3] != null ? medal[3].AsLong : 0;
            if (medal.Count > 10 && medal[10] != null) message.GuardLevel = medal[10].AsInt;
        }

        // 兼容旧协议/旧测试数据：有些房间把粉丝牌放在 info[7] 的数组里
        var legacyMedal = info[7];
        if (legacyMedal != null && legacyMedal.Kind == JsonKind.Array && legacyMedal.Count >= 2)
        {
            if (message.MedalLevel <= 0)
            {
                message.MedalLevel = legacyMedal[0] != null ? legacyMedal[0].AsInt : 0;
                message.MedalName = legacyMedal[1] != null ? (legacyMedal[1].AsString ?? "") : "";
                message.MedalAnchorName = legacyMedal[2] != null ? (legacyMedal[2].AsString ?? "") : "";
                message.MedalRoomId = legacyMedal[3] != null ? legacyMedal[3].AsLong : 0;
            }

            if (message.GuardLevel == 0 && legacyMedal.Count > 10 && legacyMedal[10] != null)
            {
                message.GuardLevel = legacyMedal[10].AsInt;
            }
        }

        // 大航海等级在 info[7]：0 = 无，1 = 总督，2 = 提督，3 = 舰长
        if (legacyMedal != null && (legacyMedal.Kind == JsonKind.Number || legacyMedal.Kind == JsonKind.String))
        {
            message.GuardLevel = legacyMedal.AsInt;
        }

        if (string.IsNullOrEmpty(message.UserName)) message.UserName = "观众" + message.Uid;
        return message;
    }

    private static int ReadInt32(List<byte> buffer, int offset)
    {
        return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }
}

/// <summary>直播间解析与弹幕服务器地址（走公开的 live 接口，游客身份即可）。</summary>
internal static class BilibiliLive
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>B 站登录 cookie（SESSDATA 等），空 = 游客。</summary>
    public static string Cookie = "";

    /// <summary>调试：取 token 用 cookie，但 WebSocket 握手不带 cookie。</summary>
    public static bool WsNoCookie;

    /// <summary>从登录 cookie 里取某个键（如 SESSDATA / buvid3）。</summary>
    public static string ExtractCookieValue(string name)
    {
        if (string.IsNullOrWhiteSpace(Cookie)) return "";

        var parts = Cookie.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            if (string.Equals(trimmed.Substring(0, eq).Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(eq + 1).Trim();
            }
        }

        return "";
    }

    private static readonly Regex RoomIdPattern = new Regex(@"\d{1,12}", RegexOptions.Compiled);

    /// <summary>抖音直播间链接（目前只做识别，弹幕协议还没接）。</summary>
    public static bool IsDouyinLink(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        return lower.Contains("douyin.com") || lower.Contains("iesdouyin.com");
    }

    /// <summary>支持「6」「https://live.bilibili.com/7734200?x=1」这类输入（抖音链接不当作 B 站房间号）。</summary>
    public static long ParseRoomId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (IsDouyinLink(text)) return 0;

        var match = RoomIdPattern.Match(text.Trim());
        long roomId;
        return match.Success && long.TryParse(match.Value, out roomId) ? roomId : 0;
    }

    /// <summary>短号 → 真实房间号（房号 6 其实是 7734200）。</summary>
    public static LiveRoomInfo ResolveRoom(long roomInput)
    {
        var json = Get("https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomInput,
            "https://live.bilibili.com/" + roomInput);
        if (json == null) return null;

        var root = ParseOrNull(json);
        if (root == null || root["code"] == null || root["code"].AsInt != 0) return null;

        var data = root["data"];
        if (data == null) return null;

        return new LiveRoomInfo
        {
            RoomId = data["room_id"] != null ? data["room_id"].AsLong : 0,
            ShortId = data["short_id"] != null ? data["short_id"].AsLong : 0,
            LiveStatus = data["live_status"] != null ? data["live_status"].AsInt : 0
        };
    }

    /// <summary>
    /// 拿鉴权 token 和弹幕服务器列表。
    /// 注意：新的 getDanmuInfo 需要 wbi 签名，未签名会返回 -352；老接口 getConf 目前仍然可用。
    /// </summary>
    private static string _wbiKey;
    private static DateTime _wbiKeyFetchedUtc;

    private static string GetWbiKey()
    {
        if (!string.IsNullOrEmpty(_wbiKey) && (DateTime.UtcNow - _wbiKeyFetchedUtc).TotalHours < 11)
        {
            return _wbiKey;
        }

        var json = Get("https://api.bilibili.com/x/web-interface/nav", "https://www.bilibili.com/");
        var root = ParseOrNull(json);
        var data = root != null ? root["data"] : null;
        var wbi = data != null ? data["wbi_img"] : null;
        var img = wbi != null && wbi["img_url"] != null ? wbi["img_url"].AsString : "";
        var sub = wbi != null && wbi["sub_url"] != null ? wbi["sub_url"].AsString : "";
        if (string.IsNullOrEmpty(img) || string.IsNullOrEmpty(sub)) return null;

        var shuffled = FileKey(img) + FileKey(sub);
        if (shuffled.Length < 64) return null;

        var table = new[] { 46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13 };
        var builder = new StringBuilder(32);
        foreach (var index in table) builder.Append(shuffled[index]);

        _wbiKey = builder.ToString();
        _wbiKeyFetchedUtc = DateTime.UtcNow;
        return _wbiKey;
    }

    private static string FileKey(string url)
    {
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url.Substring(slash + 1) : url;
        var dot = name.IndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    private static string Md5Hex(string text)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }

    /// <summary>
    /// 新版弹幕服务器接口（需要 wbi 签名）：返回的 host_list 使用 wss_port=2245，
    /// 旧 getConf 的 443 对部分房间只推人气、不推弹幕。
    /// </summary>
    private static DanmakuEndpoint GetDanmuInfoEndpoint(long roomId, bool retried = false)
    {
        try
        {
            var wbiKey = GetWbiKey();
            if (string.IsNullOrEmpty(wbiKey)) return null;

            var wts = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();
            var query = "id=" + roomId + "&type=0&web_location=444.8&wts=" + wts;
            var wrid = Md5Hex(query + wbiKey);

            var url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query + "&w_rid=" + wrid;
            var json = Get(url, "https://live.bilibili.com/" + roomId);
            var root = ParseOrNull(json);
            if (root == null || root["code"] == null) return null;

            var code = root["code"].AsInt;
            if (code != 0)
            {
                // wbi key 过期/轮换：清缓存重试一次，别让旧 key 拖 11 小时
                if (!retried && (code == -352 || code == -403))
                {
                    _wbiKey = null;
                    _wbiKeyFetchedUtc = DateTime.MinValue;
                    Logger.Write("danmaku getDanmuInfo: code " + code + "，刷新 wbi 重试");
                    return GetDanmuInfoEndpoint(roomId, true);
                }

                Logger.Write("danmaku getDanmuInfo: code " + code);
                return null;
            }

            var data = root["data"];
            if (data == null) return null;

            var endpoint = new DanmakuEndpoint { Token = data["token"] != null ? (data["token"].AsString ?? "") : "" };

            var hosts = data["host_list"];
            if (hosts != null && hosts.Kind == JsonKind.Array)
            {
                foreach (var host in hosts.Items)
                {
                    if (host == null) continue;

                    var name = host["host"] != null ? host["host"].AsString : null;
                    if (string.IsNullOrEmpty(name)) continue;

                    var port = host["wss_port"] != null ? host["wss_port"].AsInt : 443;
                    var wsUrl = "wss://" + name + ":" + port + "/sub";
                    if (!endpoint.WssHosts.Contains(wsUrl)) endpoint.WssHosts.Add(wsUrl);
                }
            }

            return endpoint;
        }
        catch (Exception ex)
        {
            Logger.Write("danmaku getDanmuInfo: " + ex.Message);
            return null;
        }
    }

    public static DanmakuEndpoint GetEndpoint(long realRoomId)
    {
        var modern = GetDanmuInfoEndpoint(realRoomId);
        if (modern != null && !string.IsNullOrEmpty(modern.Token) && modern.WssHosts.Count > 0)
        {
            Logger.Write("danmaku: getDanmuInfo ok, hosts=" + modern.WssHosts.Count + " first=" + modern.WssHosts[0]);
            return modern;
        }

        var json = Get("https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id=" + realRoomId,
            "https://live.bilibili.com/" + realRoomId);
        if (json == null) return null;

        var root = ParseOrNull(json);
        if (root == null || root["code"] == null || root["code"].AsInt != 0) return null;

        var data = root["data"];
        if (data == null) return null;

        var endpoint = new DanmakuEndpoint { Token = data["token"] != null ? (data["token"].AsString ?? "") : "" };

        var hosts = data["host_server_list"];
        if (hosts != null && hosts.Kind == JsonKind.Array)
        {
            foreach (var host in hosts.Items)
            {
                if (host == null) continue;
                var name = host["host"] != null ? host["host"].AsString : null;
                if (string.IsNullOrEmpty(name)) continue;

                var port = host["wss_port"] != null ? host["wss_port"].AsInt : 443;
                var url = "wss://" + name + ":" + port + "/sub";
                if (!endpoint.WssHosts.Contains(url)) endpoint.WssHosts.Add(url);
            }
        }

        if (endpoint.WssHosts.Count == 0)
        {
            endpoint.WssHosts.Add("wss://broadcastlv.chat.bilibili.com:443/sub");
        }

        return endpoint;
    }

    private static JsonValue ParseOrNull(string json)
    {
        try { return Json.Parse(json); }
        catch (Exception ex)
        {
            Logger.Write("bilibili live json: " + ex.Message);
            return null;
        }
    }

    private static string Get(string url, string referer)
    {
        try
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Referer = referer;
            request.Accept = "application/json, text/plain, */*";
            if (!string.IsNullOrWhiteSpace(Cookie)) request.Headers["Cookie"] = Cookie;
            request.Timeout = 12000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Logger.Write("bilibili live api: " + ex.Message);
            return null;
        }
    }
}

/// <summary>
/// 弹幕客户端：房间解析 → 取 token → WebSocket 鉴权 → 30 秒心跳 → 收包解压解析。
/// 断线自动重连（2s 起指数退避，最长 30s）。事件在后台线程触发，UI 侧自己 Post 回去。
/// </summary>
internal sealed class DanmakuClient : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private CancellationTokenSource _cts;
    private Task _loop;
    private int _messageCount;
    private volatile bool _connected;
    private volatile bool _brotliWarned;

    /// <summary>收到一条弹幕（后台线程）。</summary>
    public event Action<DanmakuMessage> Received;

    /// <summary>礼物 / 上舰事件。</summary>
    public event Action<DanmakuGift> GiftReceived;

    /// <summary>人气值（心跳回复）。</summary>
    public event Action<int> PopularityChanged;

    /// <summary>连接状态变化（true=已连上并鉴权成功）。</summary>
    public event Action<bool> ConnectionChanged;

    /// <summary>给 UI 看的状态文案：正在连接 / 已连接 / X 秒后重连 / 已断开。</summary>
    public event Action<string> StatusChanged;

    /// <summary>诊断用：每拆出一个协议包回调一次（操作码, 协议版本, body 长度）。</summary>
    public event Action<int, int, int> PacketTraced;

    public long RoomId { get; private set; }
    public long ShortId { get; private set; }

    /// <summary>直播状态：1 = 直播中，0 = 未开播（未开播时通常收不到弹幕）。</summary>
    public int LiveStatus { get; private set; }

    /// <summary>最后一条弹幕的时间（UTC），没收到过就是 MinValue。</summary>
    public DateTime LastMessageUtc { get; private set; }

    /// <summary>最后收到任意协议包的时间（用来判断半开连接）。</summary>
    public DateTime LastPacketUtc { get; private set; }

    /// <summary>收到的协议包总数（心跳/popularity 也算）。</summary>
    public int PacketCount { get { return Volatile.Read(ref _packetCount); } }

    private int _packetCount;
    private volatile bool _reconnectRequested;

    /// <summary>会话刷新间隔（秒，0 = 关闭）。B 站弹幕 token 长时间使用后会静默失效，定期换新连接更稳。</summary>
    public int SessionRefreshSeconds { get; set; } = 300;

    /// <summary>调试用：强制优先使用某个弹幕服务器（host 名或完整 wss 地址）。</summary>
    public string HostOverride { get; set; }

    /// <summary>登录用户的 uid（0 = 游客）。B 站部分房间只给登录用户下发弹幕。</summary>
    public long Uid { get; set; }

    /// <summary>鉴权用的 protover（2 = zlib，3 = brotli）。</summary>
    public int ProtoVersion { get; set; } = 3;
    private readonly HashSet<string> _seenCommands = new HashSet<string>(StringComparer.Ordinal);
    public bool IsConnected { get { return _connected; } }
    public int MessageCount { get { return Volatile.Read(ref _messageCount); } }
    private int _generation;

    public int ReconnectCount { get; private set; }
    public string LastError { get; private set; } = "";
    public bool IsRunning { get { return _loop != null && !_loop.IsCompleted; } }

    public void Start(long roomInput)
    {
        Stop();

        ShortId = roomInput;
        LiveStatus = -1;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var generation = ++_generation;
        _loop = Task.Run(() => RunAsync(roomInput, token, generation));
    }

    /// <summary>重新拉一次房间信息（用于「先连上、后开播」的情况，或接口抖动后修正 LiveStatus）。</summary>
    public async Task RefreshLiveStatusAsync()
    {
        var room = RoomId;
        if (room <= 0) return;

        try
        {
            var info = await Task.Run(() => BilibiliLive.ResolveRoom(room));
            if (info == null || info.RoomId <= 0) return;

            LiveStatus = info.LiveStatus;
            ReportStatus("房间 " + RoomId + (info.LiveStatus == 1 ? "（直播中）" : "（未开播，通常收不到弹幕）"));
        }
        catch (Exception ex)
        {
            Logger.Write("live status refresh: " + ex.Message);
        }
    }

    public void Stop()
    {
        _generation++;
        var cts = _cts;
        _cts = null;

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        // 给旧循环一点时间收尾，避免它把新会话的状态覆盖掉
        var loop = _loop;
        if (loop != null && !loop.IsCompleted)
        {
            try { loop.Wait(1500); } catch { }
        }

        SetConnected(false);
    }

    private async Task RunAsync(long roomInput, CancellationToken ct, int generation)
    {
        LiveRoomInfo room = null;
        try
        {
            room = await Task.Run(() => BilibiliLive.ResolveRoom(roomInput), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { LastError = ex.Message; }

        if (generation != _generation) return;

        if (room != null && room.RoomId > 0)
        {
            RoomId = room.RoomId;
            ShortId = room.ShortId > 0 ? room.ShortId : roomInput;
            LiveStatus = room.LiveStatus;
            ReportStatus("房间 " + RoomId + (room.LiveStatus == 1 ? "（直播中）" : "（未开播，通常收不到弹幕）"));
        }
        else
        {
            // 接口失败也允许直接当真实房号用，免得一次接口抖动就完全连不上
            RoomId = roomInput;
            ReportStatus("房间号解析失败，按 " + RoomId + " 直连");
        }

        var backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            DanmakuEndpoint endpoint = null;
            try
            {
                endpoint = await Task.Run(() => BilibiliLive.GetEndpoint(RoomId), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LastError = ex.Message; }

            if (endpoint == null || string.IsNullOrEmpty(endpoint.Token))
            {
                LastError = "拿不到弹幕服务器或 token";
                ReportStatus("获取弹幕服务器失败，" + backoff + " 秒后重试");
                Logger.Write("danmaku: no token for room " + RoomId);
            }
            else
            {
                var connectedAt = DateTime.UtcNow;
                try
                {
                    await PumpAsync(endpoint, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Logger.Write("danmaku pump: " + ex.GetType().Name + " " + ex.Message);
                    ReportStatus("弹幕连接断开：" + ex.Message);
                }
                finally
                {
                    SetConnected(false);
                }

                if (ct.IsCancellationRequested) break;

                // 连上过足够久就重置退避，避免长期运行后被一次偶发断线拖成 30 秒
                var lifetime = DateTime.UtcNow - connectedAt;
                if (lifetime > TimeSpan.FromSeconds(60))
                {
                    backoff = 2;
                    _quickCloses = 0;
                }
                else if (lifetime < TimeSpan.FromSeconds(5))
                {
                    // 秒断：多半是登录态/token 不匹配（或被限流），退避久一点
                    backoff = Math.Max(backoff, 30);

                    if (!string.IsNullOrEmpty(BilibiliLive.Cookie) && ++_quickCloses >= 3)
                    {
                        Logger.Write("danmaku: 登录态连续秒断，临时改用游客连接");
                        BilibiliLive.Cookie = "";
                        Uid = 0;
                        _quickCloses = 0;
                        backoff = 2;
                    }
                }
            }

            ReconnectCount++;
            ReportStatus(backoff + " 秒后重连（第 " + ReconnectCount + " 次）");
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { break; }

            backoff = Math.Min(backoff * 2, 30);
        }

        if (generation == _generation)
        {
            SetConnected(false);
            ReportStatus("已断开");
        }
    }

    private async Task PumpAsync(DanmakuEndpoint endpoint, CancellationToken ct)
    {
        _reconnectRequested = false;

        using (var session = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var socket = await ConnectAnyAsync(endpoint, ct))
        {
            await socket.SendAsync(
                new ArraySegment<byte>(DanmakuProtocol.BuildAuthPacket(RoomId, endpoint.Token, Uid, ProtoVersion)),
                WebSocketMessageType.Binary, true, ct);

            var heartbeat = Task.Run(() => HeartbeatLoopAsync(socket, session.Token));

            try
            {
                var buffer = new byte[32 * 1024];
                var pending = new List<byte>(64 * 1024);
                var sessionStarted = DateTime.UtcNow;

                while (!session.IsCancellationRequested)
                {
                    if (!_reconnectRequested && SessionRefreshSeconds > 0 &&
                        (DateTime.UtcNow - sessionStarted).TotalSeconds >= SessionRefreshSeconds)
                    {
                        Logger.Write("danmaku: 会话刷新（" + SessionRefreshSeconds + "s），换一条新连接");
                        _reconnectRequested = true;
                        break;
                    }

                    WebSocketReceiveResult result;
                    try
                    {
                        // 服务端 60 秒左右至少会有一次心跳/popularity；超过 120 秒没包就是半开连接，主动重连
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(120));
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!session.IsCancellationRequested)
                        {
                            Logger.Write("danmaku receive: 120 秒没有收到任何数据，按断线重连");
                        }
                        break;
                    }
                    catch (WebSocketException ex)
                    {
                        Logger.Write("danmaku receive: " + ex.Message);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count <= 0) continue;

                    LastPacketUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _packetCount);
                    for (var i = 0; i < result.Count; i++) pending.Add(buffer[i]);

                    DanmakuPacket packet;
                    while (DanmakuProtocol.TryTakePacket(pending, out packet))
                    {
                        HandlePacket(packet, 0);
                        if (_reconnectRequested) break;
                    }

                    if (_reconnectRequested) break;
                }
            }
            finally
            {
                try { session.Cancel(); } catch { }
                try { await heartbeat; } catch { }
                try { socket.Abort(); } catch { }
            }
        }
    }

    private int _hostCursor;
    private int _quickCloses;

    private async Task<ClientWebSocket> ConnectAnyAsync(DanmakuEndpoint endpoint, CancellationToken ct)
    {
        Exception last = null;

        var hosts = new List<string>(endpoint.WssHosts);

        // 每次重连换一个节点开局，避免一直连到不推弹幕的节点
        if (hosts.Count > 1)
        {
            var cursor = _hostCursor++ % hosts.Count;
            if (cursor > 0)
            {
                var rotated = hosts.GetRange(cursor, hosts.Count - cursor);
                rotated.AddRange(hosts.GetRange(0, cursor));
                hosts = rotated;
            }
        }
        if (!string.IsNullOrWhiteSpace(HostOverride))
        {
            var match = hosts.Find(h => h.IndexOf(HostOverride, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null)
            {
                hosts.Remove(match);
                hosts.Insert(0, match);
            }
            else
            {
                hosts.Insert(0, HostOverride.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                    ? HostOverride
                    : "wss://" + HostOverride + ":443/sub");
            }
        }

        foreach (var url in hosts)
        {
            // 每个 host 用新 socket：ConnectAsync 失败后同一个实例不能复用
            var socket = new ClientWebSocket();
            try { socket.Options.SetRequestHeader("User-Agent", BilibiliLive.UserAgent); } catch { }
            try { socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com"); } catch { }
            if (!string.IsNullOrWhiteSpace(BilibiliLive.Cookie) && !BilibiliLive.WsNoCookie) { try { socket.Options.SetRequestHeader("Cookie", BilibiliLive.Cookie); } catch { } }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(new Uri(url), timeout.Token);
                    Logger.Write("danmaku connected: " + url);
                    return socket;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Logger.Write("danmaku connect " + url + ": " + ex.Message);
                }
            }

            socket.Dispose();
        }

        throw new InvalidOperationException("所有弹幕服务器都连不上：" + (last != null ? last.Message : "无可用地址"), last);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(HeartbeatInterval, ct);
                if (ct.IsCancellationRequested || socket.State != WebSocketState.Open) break;
                await socket.SendAsync(new ArraySegment<byte>(DanmakuProtocol.BuildHeartbeatPacket()),
                    WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Write("danmaku heartbeat: " + ex.Message); }
    }

    /// <summary>
    /// 处理 PREPARING / LIVE / STOP_LIVE_ROOM_LIST：
    /// 主播重开直播时，旧 WebSocket 不会断，但弹幕订阅会静默失效（表现为“第一条之后再也收不到”），
    /// 所以收到本房间的开播/下播通知就立刻换一条新连接。
    /// </summary>
    private bool HandleRoomLifecycle(JsonValue root)
    {
        if (root == null) return false;

        var cmd = root["cmd"] != null ? root["cmd"].AsString : "";
        if (string.IsNullOrEmpty(cmd)) return false;

        if (cmd == "STOP_LIVE_ROOM_LIST")
        {
            if (RoomListContains(root, RoomId))
            {
                Logger.Write("danmaku lifecycle: 本房间出现在下播列表，重连");
                RequestReconnect();
            }
            return true;
        }

        if (cmd != "PREPARING" && cmd != "LIVE") return false;

        long roomId = 0;
        if (root["roomid"] != null) roomId = root["roomid"].AsInt;
        else if (root["data"] != null && root["data"]["roomid"] != null) roomId = root["data"]["roomid"].AsInt;

        Logger.Write("danmaku lifecycle: " + cmd + " room=" + roomId);

        // 有些房间的 LIVE/PREPARING 不带 roomid，只能当作本房间处理
        if (roomId > 0 && roomId != RoomId) return true;

        LiveStatus = cmd == "LIVE" ? 1 : 0;
        ReportStatus("房间 " + RoomId + (cmd == "LIVE" ? "（直播中）" : "（准备中 / 已下播）"));
        RequestReconnect();
        return true;
    }

    private static bool RoomListContains(JsonValue root, long roomId)
    {
        try
        {
            var list = root["data"] != null ? root["data"]["room_list"] : null;
            if (list == null || list.Kind != JsonKind.Array) return false;

            foreach (var item in list.Items)
            {
                if (item == null) continue;

                var id = item.Kind == JsonKind.Object && item["roomid"] != null ? item["roomid"].AsInt : item.AsInt;
                if (id == roomId) return true;
            }
        }
        catch { }
        return false;
    }

    private void RequestReconnect()
    {
        _reconnectRequested = true;
    }

    private void HandlePacket(DanmakuPacket packet, int depth)
    {
        if (packet == null || depth > 4) return;

        var traced = PacketTraced;
        if (traced != null) try { traced(packet.Operation, packet.Version, packet.Body.Length); } catch { }

        switch (packet.Operation)
        {
            case DanmakuProtocol.OpAuthReply:
                HandleAuthReply(packet.Body);
                break;

            case DanmakuProtocol.OpHeartbeatReply:
                HandlePopularity(packet.Body);
                break;

            case DanmakuProtocol.OpMessage:
                if (packet.Version == 2)
                {
                    byte[] inflated;
                    try
                    {
                        inflated = DanmakuProtocol.Inflate(packet.Body);
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("danmaku inflate: " + ex.Message);
                        return;
                    }

                    var nested = new List<byte>(inflated);
                    DanmakuPacket inner;
                    while (DanmakuProtocol.TryTakePacket(nested, out inner)) HandlePacket(inner, depth + 1);
                }
                else if (packet.Version == 3)
                {
                    byte[] inflated;
                    try
                    {
                        inflated = BrotliSharpLib.Brotli.DecompressBuffer(packet.Body, 0, packet.Body.Length, null);
                    }
                    catch (Exception ex)
                    {
                        if (!_brotliWarned)
                        {
                            _brotliWarned = true;
                            LastError = "brotli 解压失败：" + ex.Message;
                            Logger.Write("danmaku brotli: " + ex.Message);
                        }
                        return;
                    }

                    var nested = new List<byte>(inflated);
                    DanmakuPacket inner;
                    while (DanmakuProtocol.TryTakePacket(nested, out inner)) HandlePacket(inner, depth + 1);
                }
                else
                {
                    HandleBody(packet.Body);
                }
                break;
        }
    }

    private void HandleAuthReply(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body ?? Array.Empty<byte>());
        var code = 0;
        try
        {
            var root = Json.Parse(text);
            if (root != null && root["code"] != null) code = root["code"].AsInt;
        }
        catch (Exception ex)
        {
            Logger.Write("danmaku auth reply: " + ex.Message + " " + text);
        }

        if (code != 0)
        {
            LastError = "鉴权失败 code=" + code;
            Logger.Write("danmaku auth failed: " + text);
            throw new InvalidOperationException(LastError);
        }

        Logger.Write("danmaku auth ok: room " + RoomId);
        SetConnected(true);
    }

    private void HandlePopularity(byte[] body)
    {
        if (body == null || body.Length == 0) return;

        int value;
        if (body.Length == 4)
        {
            value = (body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3];
        }
        else if (!int.TryParse(Encoding.UTF8.GetString(body), out value))
        {
            return;
        }

        var handler = PopularityChanged;
        if (handler != null) try { handler(value); } catch { }
    }

    private void HandleBody(byte[] body)
    {
        if (body == null || body.Length == 0) return;

        var text = Encoding.UTF8.GetString(body);

        JsonValue root;
        try
        {
            root = Json.Parse(text);
        }
        catch (Exception ex)
        {
            Logger.Write("danmaku json: " + ex.Message);
            return;
        }

        // 房间生命周期：重开直播后旧连接还在、但弹幕订阅会失效，主动换新连接
        if (HandleRoomLifecycle(root)) return;

        // 礼物 / 上舰：转成宠物互动事件
        var gift = DanmakuProtocol.ParseGift(root);
        if (gift != null)
        {
            var giftHandler = GiftReceived;
            if (giftHandler != null)
            {
                try { giftHandler(gift); }
                catch (Exception ex) { Logger.Write("danmaku gift handler: " + ex.Message); }
            }
            return;
        }

        var message = DanmakuProtocol.ParseDanmaku(root);
        if (message == null)
        {
            // 记录出现过的命令类型，排查「连上了但收不到弹幕」
            var cmd = root != null && root["cmd"] != null ? root["cmd"].AsString : "";
            if (!string.IsNullOrEmpty(cmd) && _seenCommands.Add(cmd))
            {
                Logger.Write("danmaku cmd: " + cmd);
            }
            return;
        }

        LastMessageUtc = DateTime.UtcNow;
        Interlocked.Increment(ref _messageCount);

        var handler = Received;
        if (handler != null)
        {
            try { handler(message); }
            catch (Exception ex) { Logger.Write("danmaku handler: " + ex.Message); }
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;

        var handler = ConnectionChanged;
        if (handler != null) try { handler(value); } catch { }

        if (value) this.ReportStatus("已连接直播间 " + RoomId + "（弹幕通道）");
    }

    private void ReportStatus(string status)
    {
        var handler = StatusChanged;
        if (handler != null) try { handler(status); } catch { }
    }

    public void Dispose()
    {
        Stop();
    }
}
