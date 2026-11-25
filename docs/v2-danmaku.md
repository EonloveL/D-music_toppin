# B 站直播弹幕接入笔记（V2 · S2）

> 记录协议事实、为什么这么做、以及再踩坑时先看哪里。代码在 `src/MusicToppin/Danmaku.cs`。

## 1. 链路

```
房间号（可短号）
  → GET api.live.bilibili.com/room/v1/Room/room_init?id=…        拿真实 room_id（房号 6 → 7734200）
  → GET api.live.bilibili.com/room/v1/Danmu/getConf?room_id=…    拿 token + wss 服务器列表
  → wss://<host>:443/sub                                         ClientWebSocket
  → 鉴权包(op=7) → 30 秒心跳(op=2) → 收包(op=5, zlib/明文) → DANMU_MSG
```

## 2. 为什么不用 getDanmuInfo

新接口 `xlive/web-room/v1/index/getDanmuInfo` 需要 wbi 签名 + 完整 cookie，未签名直接 `{"code":-352}`（风控）。
老接口 `room/v1/Danmu/getConf` 仍返回同样的 `token` 与 `host_server_list`，游客可用 —— 2026-11 实测。

## 3. 包格式

16 字节头（大端）+ body：

| 偏移 | 长度 | 含义 |
|---|---|---|
| 0 | 4 | 总长度（含头） |
| 4 | 2 | 头长度（16） |
| 6 | 2 | 协议版本：0 明文 JSON / 1 心跳·人气 / 2 zlib / 3 brotli |
| 8 | 4 | 操作码：2 心跳、3 人气、5 通知、7 鉴权、8 鉴权回复 |
| 12 | 4 | 序号 |

鉴权体：

```json
{"uid":0,"roomid":<真实房号>,"protover":2,"platform":"web","type":2,"key":"<token>"}
```

`protover=2` 是关键：.NET Framework 4.8 没有 Brotli 解压器，要显式只要 zlib。

## 4. 解压

B 站的 zlib 是标准 zlib（`78 9C` 开头 + 4 字节 Adler32），而 `DeflateStream` 只吃裸 deflate：
先去掉前 2 / 后 4 字节再解，失败再退回整段裸 deflate。解出来的内容仍是若干**完整包**，要递归拆。

## 5. DANMU_MSG 字段

```
info[1]      弹幕文本
info[2][0]   uid
info[2][1]   昵称
info[2][2]   是否房管
info[7][0]   粉丝牌等级
info[7][1]   粉丝牌名称
```

`cmd` 可能带后缀（`DANMU_MSG:4:0:2:2:2:0`），用前缀匹配，不要用相等。

## 6. 实测与限制

- 2026-11-25 实测：直播间 5050（1.8M 人气）75 秒收 56 条弹幕、2 次心跳回复，连接不掉
- 冷场房间（如 6，赛事重播）能鉴权成功但收不到 `DANMU_MSG`，不是 bug
- 只解 zlib；服务器若坚持 brotli（`protover=3`）会记录一次警告并保持连接（但收不到内容）
- 游客 `uid=0` 即可鉴权；拿不到完整用户信息，只够点歌用
- 无效房号（如 999999999）服务端照样回鉴权成功，但不会有弹幕

## 7. 排错

| 现象 | 先查 |
|---|---|
| 一直「获取弹幕服务器失败」 | `getConf` 是否返回 token；网络 / DNS |
| 鉴权失败 `code=-101` | 房间号不对，或 token 过期（每次重连都会重取） |
| 连上但一条弹幕都没有 | 房间是否冷场；看日志里的 `danmaku:` 行 |
| 有 `op5/v3` 但没有内容 | 服务器强制 brotli |
| 弹幕乱码 | 包体一律按 UTF-8 解，不要跟着控制台代码页走 |

日志：`%TEMP%\musictoppin.log`（UTF-8；PowerShell 5.1 读要 `Get-Content -Encoding UTF8`）。
调试开关：`MusicToppin.exe --room=5050` 启动即连；`--fake-danmaku="点歌 BV…"` 注入一条假弹幕走全链路。
