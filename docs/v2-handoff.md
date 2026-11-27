# V2 交接说明（给新会话）

> 本文是「下一次继续开发 V2」的上下文快照。读完这一篇即可接手，不需要翻历史对话。

## 0. 一句话现状

**V1.1.1 已发布（叠加层 / 离屏采集 / 遮挡模式，已合进 main）；V2 已完成 S1（点歌指令 + 队列）、S2（B 站弹幕接入 + 点歌自动播放）、S3（网易云 + QQ 音乐）、S4（BV 播放链路）、S5（HUD / 叠加层「下一首」+ 托盘队列列表 + 观众端提示 + 视频小窗）。**

## 1. 仓库与构建

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/EonloveL/D-music_toppin |
| 分支 | `main` 是唯一开发线（V2 + v1.1.1 已合并）；`fix/v1` 保留为 v1.1.1 发布点 |
| 最新提交 | 见 `git log -1` |
| 构建 | `powershell -ExecutionPolicy Bypass -File .\build.ps1`（**不需要 .NET SDK**，首次自动下载工具链到 `.cache\nuget`） |
| 测试 | `powershell -ExecutionPolicy Bypass -File .\run-tests.ps1`（214 项，退出码 0 = 全过） |
| 产物 | `build\MusicToppin-Setup.exe`（约 932 KB）、`build\app\`（主程序约 482 KB） |
| 运行 | 装好后从托盘启动；调试直接跑 `build\app\MusicToppin.exe` |

**重要**：项目是 `.NET Framework 4.8` + 直接调 Roslyn `csc`（**不是** `dotnet build`，仓库里的 `MusicToppin.csproj` 是历史遗留，不要用它构建）。

## 2. V2 已完成的部分

### S1 点歌解析 + 队列（纯逻辑，有测试）

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/SongRequest.cs` | `点歌/點歌` 解析：`wy` / `qq` / `am` 前缀、`id:123`、`BV 号`、`b23.tv` 短链、繁体；也支持 **`qq 歌名` 这类不带「点歌」的平台前缀**，以及 `点歌：歌名` / `点歌qq 歌名` 容错 |
| `src/MusicToppin/RequestQueue.cs` | 队列：去重、用户冷却、歌曲冷却、上限、FIFO、顶歌、按人撤回、冷却清理 |

### S2 弹幕接入（已实测直播间）

```
房间号 → room_init（短号→真实房号）→ getDanmuInfo（wbi 签名，token + wss 列表；失败回退 getConf）
      → ClientWebSocket + 鉴权（游客 uid=0，或登录 uid；protover=3）
      → 30 秒心跳 → op=5 收包 → zlib / brotli 解包 → DANMU_MSG
      → SongRequestParser.TryParse → RequestQueue.Enqueue → 自动播放
```

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/Danmaku.cs` | `DanmakuClient`（连接/鉴权/心跳/重连/会话刷新）+ `DanmakuProtocol`（封包、拆包、zlib/brotli、`DANMU_MSG` 解析）+ `BilibiliLive`（房号解析、wbi 签名、取 token）+ `DanmakuPermissions`（点歌/切歌权限） |
| `src/MusicToppin/Json.cs` | 零依赖只读 JSON（对象→Dictionary / 数组→List） |
| `src/MusicToppin/TrayApp.cs` | 「弹幕点歌」菜单：连接/断开、队列状态、自动播放、放不了就跳下一首 |
| `src/MusicToppin/Settings.cs` | `LiveRoomId`、`LiveAutoConnect`（记住直播间，启动自动重连） |
| `tests/` | 214 项单元测试（点歌解析、队列、弹幕协议、JSON、网易云/QQ 解析） |

行为要点：

- 只认「点歌」开头的弹幕；`点歌姬` 不会误判
- 第一首自动开播，播完自动下一首；不支持/解析失败的那首提示并跳过
- 断线自动重连：2 秒起翻倍，最长 30 秒；连上超过 60 秒会重置退避
- 会话刷新默认 300 秒；120 秒收不到任何协议包按半开连接重连
- 弹幕默认走游客身份；部分房间要求登录，登录后 token 与 uid 自动匹配
- 点歌 / 切歌权限可在托盘「弹幕权限」随时修改，默认：点歌需粉丝团灯牌或舰长，切歌仅点歌人且房管可切全部

协议细节见 **[docs/v2-danmaku.md](v2-danmaku.md)**（含实测数据与排错表）。

### S4 BV 点播（已验证可播）

```
BV 号 → api.bilibili.com/x/web-interface/view（cid/标题/时长，实测无需 cookie）
      → api.bilibili.com/x/player/playurl?fnval=16（DASH，AAC）
      → 挑最高码率 audio.baseUrl（带 backupUrl）
      → 隐藏的 WebView2 <audio>，通过 WebResourceRequested 注入 Referer
      → 正常播放，状态回传给 HUD
```

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/Bilibili.cs` | BV 解析、view + playurl、多码率选择、`ResolvedSong` |
| `src/MusicToppin/WebView2Player.cs` | 隐藏 WebView2 宿主（屏幕外 2×2 窗口）、Referer 注入、播放/停止/seek、500 ms 状态轮询、备用地址重试、失败上报 |
| `src/MusicToppin/PromptDialog.cs` | 通用单行输入框 |
| `src/MusicToppin/NowPlayingService.cs` | `SetOwnPlayback(np)`：点歌播放时自有状态压过 SMTC/窗口通道 |

托盘菜单已有：**「弹幕点歌」**（连接直播间/断开/队列/气泡开关）、**「点歌测试（播放 BV）…」**、**「停止播放」**、**「直播叠加层（OBS 浏览器源）」**、**「离屏采集」**、**「遮挡模式」**。

### S3 曲源：网易云 + QQ 音乐（已验证可播）

```
网易云：点歌 起风了 / id:1330348068
  → /api/search/get/web?s=…（或 /api/song/detail?ids=[id]）
  → /api/song/enhance/player/url?br=320000（320k）→ 失败走 /song/media/outer/url（128k）
QQ 音乐：点歌 qq 起风了
  → c.y.qq.com/soso/fcgi-bin/client_search_cp（搜索）
  → u.y.qq.com/cgi-bin/musicu.fcg → vkey.GetVkeyServer → sip + purl
两者都 → 隐藏 WebView2 <audio> 直接播
```

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/Netease.cs` | 搜索 / 详情 / 320k 取流 / outer 兜底 / 解析（纯函数可测） |
| `src/MusicToppin/QQMusic.cs` | QQ 搜索 / vkey / uin 归一化 / 解析（纯函数可测） |
| `src/MusicToppin/NeteaseClient.cs` | 从官方客户端读登录 cookie（winsqlite3 + DPAPI + AES-GCM） |
| `src/MusicToppin/WebLoginForm.cs` | 应用内登录窗口（官方登录页 + 自动抓 cookie） |
| `src/MusicToppin/SourceCheck.cs` | 播放器检查：已安装 / 运行中 / 登录（网易云、QQ 音乐的本地信号；网易云会联网校验昵称） |
| `src/MusicToppin/PlayerCheckForm.cs` | 播放器检查对话框（再次检查 / 启动客户端） |
| `src/MusicToppin/IniFile.cs` | 极简 ini 读取（QQ 音乐登录账号） |
| `src/MusicToppin/SongResolvers.cs` | 按 `SongRequest.Source` 分派：`Auto`/`Netease` → 网易云，`QQ` → QQ 音乐，`Bilibili` → BV |
| `src/MusicToppin/ResolvedSong.cs` | 各平台统一的「可播放条目」（从 `Bilibili.cs` 拆出来） |

行为要点：

- **网易云不需要 weapi 加密**：2026-11 实测 `/api/*` 带 UA + `Referer` + `os=pc` Cookie 就能用
- **网易云登录优先「同步官方客户端」**：读 `webapp*xx*\LocalPrefs.json` 的 `os_crypt.encrypted_key`
  → DPAPI 得到 AES 密钥 → `Cookies`（SQLite）里 AES-GCM 解出 `MUSIC_U`；读不到再走应用内扫码
- 关键词取搜索前 3 个候选逐个试（搜索第一名常是翻唱）；`id:` 直接查详情
- 无版权 / 会员歌：`player/url` 返回 `code=404`、`outer/url` 302 到 `music.163.com/404` → null → 队列跳过
- **QQ 音乐匿名大概率拿不到 purl**（`req_0.code=1000`），少数免费歌可以；登录后正常
- **直播间状态诊断**（`LiveStatusForm`）：房间号 / 开播状态 / 弹幕连接 / 收到条数 / 最后一条时间 / 最近错误 /
  **最近 12 条弹幕原文** / **最近一次点歌解析结果**；「测试点歌」按钮本地模拟一条点歌，用来隔离「弹幕没收到」和「播放有问题」；
  未开播时明确提示「收不到弹幕」（实测房号 1820294367 就是 `live_status=0`，鉴权成功但 0 条弹幕）；
  日志加 `danmaku first`（第一条）与 `danmaku tick`（每分钟计数），排查「发点歌没反应」先看这两个
- **播放器检查**（对齐参考工具的 3.1.4）：网易云读 CEF cookie（MUSIC_U）；QQ 音乐读内嵌 chromium 的
  `QQMusicCache\WebkitCache\*\Cookies` 里的 `pt2gguin`（值形如 `o1305835491`，也认 `uin` / `qm_keyst`），
  `ComData\qmcomdata.ini` 的 `[SectQMAPI] uin` 只作辅助（实测登录后它仍是 0，不能单靠它）；
  没开客户端可直接「启动」，状态可「再次检查」
- **自动选平台**：`点歌 歌名` 只看「应用内可播」的登录态 —— 只登录一个就用它，都登录网易云优先、QQ 兜底；
  `wy` / `qq` 前缀强制指定；`BV` 永远走 B 站（`SongResolvers.AutoOrder` 有单元测试）
- **单队列播放**：同一时间只放一首（`_playingFromQueue` + 手动点播先 `StopOwnPlayback`）
- 弹幕切歌/撤回权限：`SkipPolicy`（0 所有人、1 仅点歌人且房管可切全部、2 仅房管、3 关闭）/ `RemovePolicy`（0 所有人、1 仅本人且房管可撤全部（默认）、2 关闭），见 `DanmakuPermissions`，菜单在「弹幕点歌」下；
  托盘根菜单另有「弹幕权限（点歌 / 切歌）」快捷入口，可随时切回「所有人」
- **点歌权限**：`RequestPolicy`（0 所有人、1 粉丝团灯牌或舰长（默认）、2 仅舰长、3 关闭）；粉丝牌必须是本直播间的（`info[3][3]`）；
  `DanmakuMessage` 已解析 `MedalLevel / MedalName / MedalRoomId / GuardLevel`（`info[3]` / `info[7]`）
- **v1.3 上屏向导**：`WizardForm` + `WizardContext`，按软件推荐叠加层 / 绿幕 / 离屏，一键应用；含弹幕点歌、权限、功能说明、FAQ
- **v1.4 环状毛玻璃**：`HudRenderer.MeasureRing/DrawRing` + `HudForm.SetRing/SetRingSide`；按位置锚点自动选择上/下/左/右，圆心在屏幕外，下一首/上一首沿圆周排列并随切歌滚动
- **v1.4 默认歌单**：`Playlist.cs` + `Netease.LoadPlaylist/ResolveById` + `QQMusic.LoadPlaylist/ResolveByMid`；设置里只存平台/ID/名称，播放时逐首取流；点歌队列优先，空了自动续默认歌单
- **反作弊保守模式**：`AppSettings.DisableMediaKeys`；关闭时 `WindowPlayers.MediaKeysEnabled=false`，不再调用 `keybd_event` 发送媒体键，只影响外部播放器切歌
- **登录凭据**：`Secrets` 用 DPAPI（CurrentUser）加密 `NeteaseCookie` / `QQMusicCookie` 再写注册表，旧明文读取时兼容
- **点歌音量**：`AppSettings.PlayerVolume`（默认 60），菜单「播放控制 → 点歌音量」预设/滑块；
  `WebView2Player.Volume` + JS `setVolume()`，拖动即时生效（只改内置播放器，不动系统音量）
- 完整接口与候选平台（酷狗 / 汽水 / YouTube / Spotify）见 **[docs/v2-sources.md](v2-sources.md)**

> 直播中**默认不弹任何通知**：点歌/连接/播放只写日志 + 更新菜单状态行，避免右下角弹窗干扰键鼠输入判定。
> 需要气泡时打开「弹幕点歌 → 点歌/播放时弹气泡提示」（`HKCU\Software\MusicToppin\LiveToast=1`）。

### S5 HUD 队列预览（已完成第一部分）

```
NowPlaying.NextTitle / NextArtist / QueueCount
  → HudRenderer.Measure 多算一行（navNext），队列空了自动收起
  → HudRenderer.Draw 画「下一首 ↓ 歌名 — 歌手（队列 N）」
  → TrayApp.PushOwnState 每 500ms 从 RequestQueue.PeekNext() 刷新
```

- 演示模式（`--demo`）也会带一行假的「下一首」，方便预览上屏效果
- 截图：`docs/screenshots/hud-next.png`
- 托盘「点歌队列（N 首）」展开列表（前 10 首：歌名 · 点歌人），带「下一首 / 清空」
- 叠加层（OBS 浏览器源）同步「下一首 + 队列数」；点歌成功/放不了在叠加层底部弹 4.5 秒提示（`OverlayState.Toast / ToastId`）
- **视频小窗**：`OverlayState.VideoBvid/VideoSound` → `/video` 页用 `<video>` 播本地代理的 MP4（`/video/stream?bvid=`，带 Referer/Range），无 B 站 UI；默认 `videoSound=false`
  （默认 `VideoAudio=0` 走应用播放器音量；OBS 允许带声自动播放时可切 `VideoAudio=1` 跟随视频）。
  播放 B 站视频时 `TrayApp._videoBvid` 置位，停止/下一首自动清空
- 叠加层两个源共用一个端口：`OverlayServer.Start(preferredPort)` 优先上次端口、占用则 4681-4750 往后找，
  成功端口写回 `OverlayPort`；托盘可分别「复制叠加层地址 / 复制视频小窗地址」
- 至此 S5 完成；剩余是 S3 的 Apple Music（酷狗暂缓）
> **弹幕排查教训**：见 [postmortem-danmaku.md](postmortem-danmaku.md)（brotli / getDanmuInfo(2245) / 登录 uid=mid 三个坑）。

## 3. 关键决策与代价

| 决策 | 原因 | 代价 |
|---|---|---|
| 播放用 **WebView2** 而不是 mpv/ffmpeg | Chromium 原生支持 B 站 DASH(fMP4)，且能注入 Referer 绕过热链；mpv 要多 30–70 MB | 安装包 → **约 932 KB**；播放时多 7 个 `msedgewebview2` 进程，约 **+160 MB** 内存 |
| **双模式**：纯显示 / 点歌 | 保住 V1「轻量」卖点 | 首次播放时才创建 WebView2（纯显示模式 25–33 MB） |
| 弹幕优先 **getDanmuInfo（wbi）+ 2245**，旧 getConf 兜底 | 旧接口对部分房间只推人气包；新接口能正常订阅弹幕 | 需要 wbi 签名；旧接口随时可能下线 |
| 鉴权 **protover=3**，支持 **brotli + zlib** | 部分房间只下发 brotli；只支持 zlib 会「鉴权成功但 0 条弹幕」 | 内置 BrotliSharpLib + Unsafe，安装包约 +1.3 MB |
| 登录 token 必须配登录 **uid** | 登录后 token 绑定 uid，鉴权包仍发 uid=0 会秒断；`nav` 字段是 `mid` | 登录失败时自动回退游客连接 |
| 点歌自动播放做成「队列驱动」 | S2 先闭环：弹幕 → 入队 → 出声 | 支持网易云 + QQ 音乐 + B 站 BV；Apple 入队后会被跳过并提示 |
| 网易云走 **公开 `/api/*`** 而不是 weapi 加密 | 2026-11 实测游客可用，省掉 AES/RSA 一大段代码 | 游客曲库有限；登录后更多（客户端同步 / 扫码） |
| 登录优先「**同步官方客户端**」 | 参考工具的文档就是「登录交给官方客户端」，用户不想复制 cookie | 读客户端私有 cookie（仅本机、不上传）；换号后要重新同步；依赖 Win10+ 的 `winsqlite3.dll` |

## 4. 待办（建议顺序）

### S3 剩余：Apple Music（酷狗按需求先移除）

- 分派已经做好：`SongResolvers.Resolve()` 里加一个 `case`，照 `Netease.cs` 的形状写（搜索 → 取流 → 兜底 → 纯函数解析 + 测试）
- ~~酷狗~~：按用户要求先移除，后续要加时再实现（tracker 要 `md5(hash + "kgcloud")`）
- **酷我（已放弃）**：`anti.s` 匿名只给 ~11 秒试听（免费歌只有移动端能听），实测后撤掉，别再重复踩
- **Apple Music**：网页端无直链，只能试听或 MusicKit（要开发者 token），保持「只入队提示」
- 候选平台评估见 [docs/v2-sources.md](v2-sources.md)

### S5（已完成）

- HUD / 叠加层「下一首 + 队列数」、托盘队列列表、观众端点歌提示都已完成
- 后续可选：点歌人昵称进 HUD、队列内拖动排序

## 5. 已知坑（别再踩）

1. **安装器会替换整个安装目录**：只允许替换真正的 MusicToppin 安装目录（`MusicToppin.exe` 产品名/注册表匹配）；加新依赖 DLL 时必须进 payload。
2. **WebView2 必须在可见窗口里**：宿主窗口是屏幕外 2×2（`-32000,-32000`），不能用隐藏窗口，否则不渲染/不出声。
3. **B 站音频直链有热链保护**：所有 CDN 请求必须带 `Referer: https://www.bilibili.com`，否则 403。备用地址（`backupUrl`）要自动重试。
4. **playurl 目前不需要 wbi 签名**（2026-11 实测），但 B 站随时可能改；如果突然返回 `-403`，先怀疑这一点。
5. **弹幕包有 zlib 和 brotli 两种**：zlib（`ver=2`）不是裸 deflate，`DeflateStream` 要先剥 2 字节头 + 4 字节 Adler32；brotli（`ver=3`）用 BrotliSharpLib 解。解出来的内容还是若干完整包，要递归拆。
6. **`DANMU_MSG` 的 `cmd` 可能带后缀**（`DANMU_MSG:4:0:2:2:2:0`），必须前缀匹配。
7. **冷场房间**（如房号 6 的赛事重播）能鉴权成功但收不到弹幕，别当成连接失败。
8. **csc 编译要带 `/nowarn:8632`**，且所有 `.cs` 里中文字符串用 UTF-8；`toolchain.ps1` 已处理。
9. **`git` 现在可直接用**；仓库在 `E:\D-music_toppin`。早期文档提到的便携版路径 `D:\tools\MinGit\cmd\git.exe` 只作历史参考。
10. **中文日志乱码**：`%TEMP%\musictoppin.log` 是 UTF-8，PowerShell 5.1 直接 `Get-Content` 会乱码，用 `-Encoding UTF8`。
11. **执行策略**：交互式 pwsh 里 `. script.ps1` 会被挡，统一用 `powershell -NoProfile -ExecutionPolicy Bypass -File xxx.ps1`。
12. **别改成弹气泡「通知」**：点歌/连接/播放这类自动事件走 `LiveNotify`（默认只写日志），
    直播时 Windows 气泡会干扰键鼠输入判定；`ShowBalloonTip` 只留给用户主动点的操作。
13. **网易云的「无版权」不是网络错误**：`player/url` 会回 `code=404` + `url=null`，`outer/url` 会 302 到
    `music.163.com/404` 的 HTML —— 必须校验 `Content-Type` 与落地域名，否则会把 404 页面当音频播。
14. **网易云搜索第一名常是翻唱/直播版**：`Netease.Resolve` 会依次试前 3 个候选，不要只取第一个。
15. **客户端 cookie 是 CEF 方案**：`webapp*xx*\LocalPrefs.json` 的 `os_crypt.encrypted_key` 用 DPAPI 解出
    AES-256-GCM 密钥；cookie 值是 `v10 + nonce(12) + 密文 + tag(16)`。BCrypt 的 P/Invoke 必须
    `CharSet.Unicode`，否则 `BCryptOpenAlgorithmProvider` 返回 `0xC0000225`（找不到算法）。
16. **`winsqlite3.dll` 只有 Win10/11 自带**；没有就回退「应用内登录」。客户端锁着 Cookies 文件，
    必须先复制到 `%TEMP%` 再读。
17. **QQ 音乐「搜索成功 ≠ 能播」**：匿名时 vkey 返回 `req_0.code=1000` + 空 `purl`，不要只看搜索有结果。
18. **读客户端 cookie 是敏感操作**：只在本机做、只用于网易云官方请求；README/文档里写明，不要外传或落盘到别处。
19. **QQ 音乐的登录态不在 `qmcomdata.ini` 里**：`[SectQMAPI] uin` 实测登录后仍是 0（它是 QMAPI 插件的字段）。
    真正的账号在 `QQMusicCache\WebkitCache\*\Cookies`（SQLite）的 `pt2gguin=o<QQ号>`；文件被 qmbrowser 锁着，
    要复制到 temp 再用 winsqlite3 读。
20. **酷狗已按需求移除**；如果以后再加，登录标记在 `HKCU\Software\KuGoo3`（`LastLoginUserID`/`UserID`）或 `kugou.ini`。

## 6. 调试入口

```
MusicToppin.exe --demo                      演示数据（不读系统播放）
MusicToppin.exe --demo-fast                 演示数据，12 秒切一首
MusicToppin.exe --play-bv=BV1xx411c7mD      启动即播放指定 BV（自动化验证）
MusicToppin.exe --room=5050                 启动即连接直播间弹幕
MusicToppin.exe --fake-danmaku="点歌 BV…"   注入假弹幕（可重复传多个，依次触发），走「解析→入队→播放」全链路
MusicToppin.exe --fake-danmaku="点歌 起风了" 同上，但走网易云取流（S3 验证用）
MusicToppin.exe --fake-danmaku="点歌 qq 起风了" 走 QQ 音乐取流（需要登录才有地址）
MusicToppin.exe --login-test=netease      打开网易云登录窗口（netease|qq）
```

- 日志：`%TEMP%\musictoppin.log`（`sources:`、`netease client:`、`netease account:`、`danmaku:`、`song request:`、`resolved:`、`queue play:`、`queue skip:`、`qq:`、`notify:`、`player failed:`、`overlay server started at` 是排错关键词）
- 设置：`HKCU\Software\MusicToppin`（`Hotkey`、`UpdateUrl`、`LiveRoomId`、`LiveAutoConnect`、`LiveToast`、`NeteaseCookie`、`QQMusicCookie`、`QQMusicUin`、`Overlay`、`CaptureOffscreen`、`CaptureOccluded`、`DefaultPlaylistSource`、`NeteasePlaylistId`、`QQPlaylistId` 等）
- 安装目录：默认 `%LocalAppData%\Programs\MusicToppin`，可自定义
- 单元测试：`run-tests.ps1`（214 项，不必联网；已引用 `src\MusicToppin\lib` 下的 brotli 依赖）
- 叠加层：托盘开启后日志里有 `overlay server started at http://127.0.0.1:<port>/`，浏览器可直接打开验证

## 7. 新会话建议的第一次动作

1. `git checkout main && git pull`
2. `run-tests.ps1` + `build.ps1` 确认环境（应全绿、约 932 KB）
3. `MusicToppin.exe --room=5050` 跑 30 秒，看日志里有没有 `danmaku auth ok` 与 `song request`
4. `MusicToppin.exe --fake-danmaku="点歌 起风了"` 验证网易云链路（日志应有 `resolved:` + `queue play:`）
5. 接着做 **S3 剩余**：Apple Music（只有试听），或按用户要求再评估酷狗/酷我

## 8. 分支与版本（重要）

- `main`：唯一开发线，已用 merge 提交把 **v1.1.1**（叠加层 / 离屏采集 / 遮挡模式）合进来，冲突点都在
  `TrayApp.cs`（菜单项/字段）、`Settings.cs`（注册表项）、`CHANGELOG.md`，处理原则是**两边都保留**。
- `fix/v1`：停在 v1.1.1 发布点，只做 V1 修复；不要在这里加 V2 功能。
- **版本号 1.4.6**（`VersionInfo.cs` + `installer/Installer.cs`）。
  发版时确认 GitHub Release 的 tag 与之一致，否则更新器会比对失败。
