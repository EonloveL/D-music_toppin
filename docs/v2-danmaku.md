# B 站直播弹幕接入笔记（V2 · S2，按 v1.4.9 更新）

> 记录协议事实、为什么这么做、以及再踩坑时先看哪里。代码在 `src/MusicToppin/Danmaku.cs`。
>
> 本文以 **v1.4.9** 为准。更早的 v1.2.0 只支持 getConf + zlib，已被 v1.2.5 的 brotli / getDanmuInfo / 登录 uid 方案取代。

## 1. 链路

```
房间号（可短号）
  → GET api.live.bilibili.com/room/v1/Room/room_init?id=…        拿真实 room_id（房号 6 → 7734200）
  → GET xlive/web-room/v1/index/getDanmuInfo（wbi 签名）          拿 token + host_list（wss_port 常见 2245）
  → wss://<host>:<wss_port>/sub                                  ClientWebSocket
  → 鉴权包(op=7) → 30 秒心跳(op=2) → 收包(op=5, 明文/zlib/brotli) → DANMU_MSG
```

`getDanmuInfo` 失败时回退旧接口：

```
GET api.live.bilibili.com/room/v1/Danmu/getConf?room_id=…
```

旧接口常见 `wss_port=443`，对部分房间只推人气包；新接口优先。

## 2. 为什么新接口需要 wbi

`getDanmuInfo` 未签名会返回 `{"code":-352}`。wbi 签名步骤：

```
从 api.bilibili.com/x/web-interface/nav 的 data.wbi_img 取 img_url / sub_url
  → 取文件名（去掉扩展名）拼成 64 位字符串
  → 按固定表重排成 32 位 wbiKey
  → w_rid = md5(query + wbiKey)，query 里带 wts
```

wbiKey 缓存 11 小时；失败会回退 `getConf`。

## 3. 包格式

16 字节头（大端）+ body：

| 偏移 | 长度 | 含义 |
|---|---|---|
| 0 | 4 | 总长度（含头） |
| 4 | 2 | 头长度（16） |
| 6 | 2 | 协议版本：0 明文 JSON / 1 心跳·人气 / 2 zlib / 3 brotli |
| 8 | 4 | 操作码：2 心跳、3 人气、5 通知、7 鉴权、8 鉴权回复 |
| 12 | 4 | 序号 |

鉴权体（`protover=3`，服务器会下发 brotli；客户端内置 BrotliSharpLib 解码）：

```json
{"uid":0,"roomid":<真实房号>,"protover":3,"platform":"web","type":2,"key":"<token>"}
```

登录后：

- `getDanmuInfo` 用登录 cookie 取到的 token **绑定 uid**；
- 鉴权包的 `uid` 必须与 token 的 uid 一致，否则服务端会秒断；
- `nav` 接口返回的字段是 `mid`，不是 `uid`。

连续秒断超过 3 次时，客户端会临时回退游客连接，避免被限流。

## 4. 解压

- **zlib**（`ver=2`）：标准 zlib，`78 9C` 开头 + 4 字节 Adler32；`DeflateStream` 只吃裸 deflate，所以先去掉前 2 / 后 4 字节，失败再退回整段裸 deflate。
- **brotli**（`ver=3`）：用 `BrotliSharpLib.Brotli.DecompressBuffer`，依赖 `System.Runtime.CompilerServices.Unsafe`。
- 解出来的内容仍是若干**完整包**，要递归拆。

## 5. DANMU_MSG 字段

```
info[1]      弹幕文本
info[2][0]   uid
info[2][1]   昵称
info[2][2]   是否房管
info[3][0]   粉丝牌等级
info[3][1]   粉丝牌名称
info[3][2]   粉丝牌主播名
info[3][3]   粉丝牌所属房间号（判断是不是本直播间的灯牌）
info[3][10]  大航海类型：0 无 / 1 总督 / 2 提督 / 3 舰长
info[7]      大航海等级（数字，0 无 / 1 总督 / 2 提督 / 3 舰长）
```

`cmd` 可能带后缀（`DANMU_MSG:4:0:2:2:2:0`），用前缀匹配，不要用相等。
旧版/少数房间可能把粉丝牌放在 `info[7]` 的数组里，解析时做了兼容；正常以 `info[3]` 为准。

## 6. 权限判定

- 点歌：`所有人 / 粉丝团灯牌或舰长 / 仅舰长 / 关闭`；粉丝牌必须匹配本直播间房间号。
- 切歌：`所有人 / 仅点歌人（房管可切全部）/ 仅房管 / 关闭`；`info[2][2]` 为房管标记。
- 纯逻辑在 `DanmakuPermissions`（`src/MusicToppin/Danmaku.cs`），托盘「弹幕权限」和上屏向导都能随时修改，修改立即生效。

## 7. 实测与限制

- 2026-11 实测：直播间 5050（1.8M 人气）75 秒收 56 条弹幕、2 次心跳回复，连接不掉
- 冷场房间（如 6，赛事重播）能鉴权成功但收不到 `DANMU_MSG`，不是 bug
- 部分房间只下发 `protover=3`（brotli）；v1.2.5 起已支持，不会静默丢弃
- 游客 `uid=0` 即可鉴权；部分房间只给登录用户下发弹幕
- 无效房号（如 999999999）服务端照样回鉴权成功，但不会有弹幕

## 8. 排错

| 现象 | 先查 |
|---|---|
| 一直「获取弹幕服务器失败」 | `getDanmuInfo` 是否 -352 / 网络 / DNS；看是否回退 `getConf` |
| 鉴权失败 `code=-101` | 房间号不对，或 token 过期（每次重连都会重取） |
| 登录后秒断 | 鉴权 `uid` 与 token 绑定的 uid 不一致；`nav` 读的是 `mid` |
| 连上但一条弹幕都没有 | 房间是否冷场；是否只收到人气包；`danmaku cmd:` / `lastPacket` |
| 有 `op5/v3` 但没有内容 | brotli 解码失败；检查 BrotliSharpLib / Unsafe DLL 是否在安装目录 |
| 弹幕乱码 | 包体一律按 UTF-8 解，不要跟着控制台代码页走 |

日志：`%TEMP%\musictoppin.log`（UTF-8；PowerShell 5.1 读要 `Get-Content -Encoding UTF8`）。
调试开关：`MusicToppin.exe --room=5050` 启动即连；`--fake-danmaku="点歌 BV…"` 注入一条假弹幕走全链路。
