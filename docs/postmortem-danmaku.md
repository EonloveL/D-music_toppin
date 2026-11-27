# 复盘：部分直播间「连上了但收不到弹幕」 (2026-11)

> 本文记录的是 v1.2.5 的修复过程；当前 v1.4.7 沿用同一套弹幕链路（getDanmuInfo + brotli + 登录 uid），并在此基础上增加了点歌/切歌权限与上屏向导。
>
> 记录一次从 v1.2.0 折腾到 v1.2.5 的排查。**结论先写在最前面**，细节和以后怎么避免放后面。

## 一句话结论

B 站直播弹幕不是「连上 WebSocket 就一定有弹幕」：

1. **压缩协议**：房间可能只下发 `protover=3`（brotli）；只支持 zlib 的客户端会**鉴权成功、心跳正常，但一条 `DANMU_MSG` 都收不到**。
2. **服务器接口**：旧 `room/v1/Danmu/getConf`（443）对部分房间只推人气包；要用新版 `xlive/web-room/v1/index/getDanmuInfo`（wbi 签名，端口 **2245**）。
3. **登录态**：`getDanmuInfo` 带登录 cookie 取到的 token 是**绑定 uid** 的；鉴权包 `uid` 必须一致，否则服务端**秒断**。而 `nav` 接口返回的字段叫 `mid`，不是 `uid`。

## 症状时间线

| 版本 | 改动 | 结果 |
|---|---|---|
| v1.2.0 | 弹幕基础接入（getConf + protover 2） | 热门房间正常，用户自己的房间 0 条 |
| v1.2.1 | 120 秒无包看门狗 | 不解决（人气包一直有，看门狗不触发） |
| v1.2.2 | 状态窗口加「最后收包/协议包数」、`danmaku cmd:` 日志 | 能证明「连接活着但没弹幕」，仍收不到 |
| v1.2.3 | `LIVE`/`PREPARING` 主动重连 | 解决「重开播后订阅失效」，但用户房间仍然 0 条 |
| v1.2.4 | 60 秒会话刷新 | 热门房间更稳，用户房间仍然 0 条 |
| **v1.2.5** | **brotli + getDanmuInfo/2245 + 登录 uid** | **用户房间正常收到** |

## 根因详解

### 1. brotli（最关键）

抓包看到：`protover=3` 时服务端下发 `op=5, ver=3`（brotli）。我们之前只处理 `ver=0`（明文）和 `ver=2`（zlib），`ver=3` 直接丢弃，只在日志写一句 `protover 3 (brotli) not supported`。

.NET Framework 4.8 没有 `BrotliStream`，所以被忽略了很久。最终方案：内置托管解码库 `BrotliSharpLib`（`src/MusicToppin/lib/`），依赖 `System.Runtime.CompilerServices.Unsafe`。

### 2. getDanmuInfo / 2245

旧 `getConf` 返回 `wss_port=443`；新 `getDanmuInfo` 返回 `wss_port=2245`。后者需要 wbi 签名：

```
GET /xlive/web-room/v1/index/getDanmuInfo?id=<room>&type=0&web_location=444.8&wts=<秒>&w_rid=<md5(参数+wbiKey)>
```

wbiKey 从 `api.bilibili.com/x/web-interface/nav` 的 `wbi_img` 算出来。现在实现为「新接口优先、旧接口兜底」。

### 3. 登录 uid（最隐蔽）

- 游客连：`uid=0` + 游客 token → 正常。
- 登录后：`getDanmuInfo` 带 cookie 取到的是**登录态 token**，鉴权包仍发 `uid=0` → 服务端**立刻断开**（日志：`远程方在没有完成关闭握手的情况下关闭了 WebSocket 连接`）。
- `FetchBilibiliUid` 读的是 `nav.data.uid`，而实际字段是 `nav.data.mid`，所以 uid 一直是 0。

修复后：`uid=450794957` 与 token 匹配，连接稳定。

## 为什么走了这么多弯路

1. **用「鉴权成功」代替「收到弹幕」**：`auth ok` 只说明协议层通了，不代表订阅到了房间的 `DANMU_MSG`。
2. **只在热门房间测**：热门房间用旧链路/游客也能收到，掩盖了「冷门房间 + protover3 + 登录态」的组合问题。
3. **没有记录包版本**：如果早点打 `op5 ver=?` 的日志，brotli 问题 10 分钟就能发现。
4. **忽略参考实现**：`BxNcm-DJ` 用 protover 3 + brotli，`blivedm` 用 getDanmuInfo + wbi，都是现成答案；先自己猜了很久。
5. **反复重连被限流**：同一 IP 短时间大量建连会被 B 站限速（表现为所有节点秒断），一度误判成代码问题。

## 排查方法（可复用）

1. **状态窗口三元组**：`开播状态` + `收到弹幕` + `最后收包`。
   - 最后收包在 30 秒内 → TCP/心跳正常
   - 收到弹幕长期 0 → 订阅层问题（压缩/登录/token）
2. **日志锚点**：`danmaku first`、`danmaku cmd:`、`danmaku tick … lastPacket=Ns`、`getDanmuInfo ok …:2245/sub`、`会话刷新`、`秒后重连`。
3. **多节点并行探测**：同一 token 同时连多个 host，看哪个节点能收到 `DANMU_MSG`（`%TEMP%\toppin-hosts\multihost-probe.exe`）。
4. **对照实验**：游客 vs 登录、223/443 vs 2245、protover 2 vs 3，一次只改一个变量。
5. **全新配置**（清空 `HKCU\Software\MusicToppin`）验证「别人第一次装能不能用」。

## 以后必须遵守的规则

- **不要把 `auth ok` 当成成功**；验收标准是「收到至少一条 `DANMU_MSG`」。
- **op=5 三种版本都要处理**：0 明文 / 2 zlib / 3 brotli；收到未知版本先记日志，不要静默丢。
- **新接口优先**：getDanmuInfo(wbi) + `wss_port`；getConf 只做兜底。
- **登录 token 必须配登录 uid**；`nav` 用 `mid`。
- **重连要克制**：会话刷新默认 300 秒；秒断用 30 秒退避；连续秒断超过 3 次回退游客，避免被限流。
- **重连轮换 host**；不要永远只连列表第一个。
- **测试矩阵**：热门房间 / 冷门房间 / 主播自己的房间 / 游客 / 登录 / 首次安装。
- **改弹幕相关代码必须跑一次真实房间**（不能只跑单测和假弹幕）。

## 关键代码位置

| 位置 | 作用 |
|---|---|
| `BilibiliLive.GetDanmuInfoEndpoint` | wbi 签名 + 新接口 + host_list |
| `BilibiliLive.GetEndpoint` | 新接口优先、旧接口兜底 |
| `DanmakuProtocol.BuildAuthPacket` | uid/roomid/protover/platform/type/key/buvid |
| `DanmakuClient.PumpAsync` | WS 连接、鉴权、心跳、收包、会话刷新、半开检测 |
| `DanmakuClient.HandlePacket` | op5 / v0 / v2(zlib) / v3(brotli) 解包 |
| `DanmakuClient.HandleBody` | DANMU_MSG 解析、命令类型日志 |
| `TrayApp.HandleDanmaku` | 切歌/撤回/点歌解析 → 队列 |
| `LiveStatusForm` | 开播/连接/收到条数/最后收包/最近弹幕/最近点歌 |

## 附：版本对照

- v1.2.5：brotli + getDanmuInfo/2245 + 登录 uid + 节点轮换 + 会话刷新 300 秒
- v1.2.4：60 秒会话刷新
- v1.2.3：LIVE/PREPARING 重连
- v1.2.2：最后收包 / 协议包计数 / 命令日志
- v1.2.1：120 秒无包看门狗
- v1.2.0：初版弹幕接入
