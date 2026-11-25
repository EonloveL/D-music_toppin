# V2 交接说明（给新会话）

> 本文是「下一次继续开发 V2」的上下文快照。读完这一篇即可接手，不需要翻历史对话。

## 0. 一句话现状

**V1.1.1 已发布（叠加层 / 离屏采集 / 遮挡模式，已合进 main）；V2 已完成 S1（点歌指令 + 队列）、S2（B 站弹幕接入 + 点歌自动播放）、S3-网易云、S4（BV 播放链路）。**
S3 已接入网易云 + QQ 音乐（含「同步官方客户端登录」与「播放器检查」），S5 的 HUD「下一首 / 队列预览」也已完成。剩下 Apple Music、酷我/酷狗候选，以及 S5 的托盘队列列表 / 弹幕提示。版本号 **1.2.0**（未发布）。

## 1. 仓库与构建

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/EonloveL/D-music_toppin |
| 分支 | `main` 是唯一开发线（V2 + v1.1.1 已合并）；`fix/v1` 保留为 v1.1.1 发布点 |
| 最新提交 | 见 `git log -1` |
| 构建 | `powershell -ExecutionPolicy Bypass -File .\build.ps1`（**不需要 .NET SDK**，首次自动下载工具链到 `.cache\nuget`） |
| 测试 | `powershell -ExecutionPolicy Bypass -File .\run-tests.ps1`（124 项，退出码 0 = 全过） |
| 产物 | `build\MusicToppin-Setup.exe`（约 367 KB）、`build\app\`（主程序 182 KB） |
| 运行 | 装好后从托盘启动；调试直接跑 `build\app\MusicToppin.exe` |

**重要**：项目是 `.NET Framework 4.8` + 直接调 Roslyn `csc`（**不是** `dotnet build`，仓库里的 `MusicToppin.csproj` 是历史遗留，不要用它构建）。

## 2. V2 已完成的部分

### S1 点歌解析 + 队列（纯逻辑，有测试）

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/SongRequest.cs` | `点歌/點歌` 解析：`wy` / `qq` / `am` 前缀、`id:123`、`BV 号`、`b23.tv` 短链、繁体；必须「点歌 + 空格」开头 |
| `src/MusicToppin/RequestQueue.cs` | 队列：去重、用户冷却、歌曲冷却、上限、FIFO、顶歌、按人撤回、冷却清理 |

### S2 弹幕接入（已实测直播间）

```
房间号 → room_init（短号→真实房号）→ getConf（token + wss 列表）
      → ClientWebSocket + 游客鉴权（uid=0, protover=2）
      → 30 秒心跳 → op=5 收包 → zlib 解包 → DANMU_MSG
      → SongRequestParser.TryParse → RequestQueue.Enqueue → 自动播放
```

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/Danmaku.cs` | `DanmakuClient`（连接/鉴权/心跳/重连）+ `DanmakuProtocol`（封包、拆包、zlib、`DANMU_MSG` 解析）+ `BilibiliLive`（房号解析、取 token） |
| `src/MusicToppin/Json.cs` | 零依赖只读 JSON（对象→Dictionary / 数组→List） |
| `src/MusicToppin/TrayApp.cs` | 「弹幕点歌」菜单：连接/断开、队列状态、自动播放、放不了就跳下一首 |
| `src/MusicToppin/Settings.cs` | `LiveRoomId`、`LiveAutoConnect`（记住直播间，启动自动重连） |
| `tests/` | 124 项单元测试（点歌解析、队列、弹幕协议、JSON、网易云/QQ 解析） |

行为要点：

- 只认「点歌」开头的弹幕；`点歌姬` 不会误判
- 第一首自动开播，播完自动下一首；不支持/解析失败的那首提示并跳过
- 断线自动重连：2 秒起翻倍，最长 30 秒；连上超过 60 秒会重置退避
- 弹幕走游客身份，不需要账号

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
| `src/MusicToppin/SourceCheck.cs` | 播放器检查：已安装 / 运行中 / 登录（三平台各自的本地信号） |
| `src/MusicToppin/PlayerCheckForm.cs` | 播放器检查对话框（再次检查 / 启动客户端） |
| `src/MusicToppin/IniFile.cs` | 极简 ini 读取（QQ 音乐 / 酷狗登录账号） |
| `src/MusicToppin/SongResolvers.cs` | 按 `SongRequest.Source` 分派：`Auto`/`Netease` → 网易云，`QQ` → QQ 音乐，`Bilibili` → BV |
| `src/MusicToppin/ResolvedSong.cs` | 各平台统一的「可播放条目」（从 `Bilibili.cs` 拆出来） |

行为要点：

- **网易云不需要 weapi 加密**：2026-11 实测 `/api/*` 带 UA + `Referer` + `os=pc` Cookie 就能用
- **网易云登录优先「同步官方客户端」**：读 `webapp*xx*\LocalPrefs.json` 的 `os_crypt.encrypted_key`
  → DPAPI 得到 AES 密钥 → `Cookies`（SQLite）里 AES-GCM 解出 `MUSIC_U`；读不到再走应用内扫码
- 关键词取搜索前 3 个候选逐个试（搜索第一名常是翻唱）；`id:` 直接查详情
- 无版权 / 会员歌：`player/url` 返回 `code=404`、`outer/url` 302 到 `music.163.com/404` → null → 队列跳过
- **QQ 音乐匿名大概率拿不到 purl**（`req_0.code=1000`），少数免费歌可以；登录后正常
- **播放器检查**（对齐参考工具的 3.1.4）：网易云读 CEF cookie（MUSIC_U）；QQ 音乐读内嵌 chromium 的
  `QQMusicCache\WebkitCache\*\Cookies` 里的 `pt2gguin`（值形如 `o1305835491`，也认 `uin` / `qm_keyst`），
  `ComData\qmcomdata.ini` 的 `[SectQMAPI] uin` 只作辅助（实测登录后它仍是 0，不能单靠它）；
  酷狗读 `HKCU\Software\KuGoo3` 的 `LastLoginUserID` 或 `kugou.ini`；没开客户端可直接「启动」，状态可「再次检查」
- 完整接口与候选平台（酷我 / 酷狗 / 汽水 / YouTube / Spotify）见 **[docs/v2-sources.md](v2-sources.md)**

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
- 还没做：托盘里的队列列表（现在只有数量）、点歌成功/失败的弹幕提示、叠加层（OBS）同步「下一首」
## 3. 关键决策与代价

| 决策 | 原因 | 代价 |
|---|---|---|
| 播放用 **WebView2** 而不是 mpv/ffmpeg | Chromium 原生支持 B 站 DASH(fMP4)，且能注入 Referer 绕过热链；mpv 要多 30–70 MB | 安装包 → **381 KB**；播放时多 7 个 `msedgewebview2` 进程，约 **+160 MB** 内存 |
| **双模式**：纯显示 / 点歌 | 保住 V1「轻量」卖点 | 首次播放时才创建 WebView2（纯显示模式 25–33 MB） |
| 弹幕用 **getConf** 而不是 getDanmuInfo | 新接口要 wbi 签名 + cookie，未签名直接 -352 | 老接口随时可能下线，届时要做 wbi |
| 鉴权 **protover=2**（只 zlib） | .NET Framework 4.8 没有 Brotli | 服务器若强制 brotli 就收不到弹幕（会记警告） |
| 点歌自动播放做成「队列驱动」 | S2 先闭环：弹幕 → 入队 → 出声 | 支持网易云 + QQ 音乐 + B 站 BV；Apple 入队后会被跳过并提示 |
| 网易云走 **公开 `/api/*`** 而不是 weapi 加密 | 2026-11 实测游客可用，省掉 AES/RSA 一大段代码 | 游客曲库有限；登录后更多（客户端同步 / 扫码） |
| 登录优先「**同步官方客户端**」 | 参考工具的文档就是「登录交给官方客户端」，用户不想复制 cookie | 读客户端私有 cookie（仅本机、不上传）；换号后要重新同步；依赖 Win10+ 的 `winsqlite3.dll` |

## 4. 待办（建议顺序）

### S3 剩余：Apple Music / 酷我 / 酷狗

- 分派已经做好：`SongResolvers.Resolve()` 里加一个 `case`，照 `Netease.cs` 的形状写（搜索 → 取流 → 兜底 → 纯函数解析 + 测试）
- **酷我**：`search.kuwo.cn/r.s` + `antiserver.kuwo.cn/anti.s?type=convert_url…` 匿名就给 mp3，约 100 行，优先做
- **酷狗**：搜索匿名可用，取流要 `key = md5(hash + "kgcloud")` 的 tracker 接口 + 跟随 302，中等成本
- **Apple Music**：网页端无直链，只能试听或 MusicKit（要开发者 token），保持「只入队提示」
- 候选平台评估见 [docs/v2-sources.md](v2-sources.md)

### S5 剩余

- 托盘「点歌队列」列出前几首（现在只有数量 + 下一步按钮）
- 点歌成功/失败给一路弹幕提示（需要 S2 的发送能力；在那之前状态只在托盘菜单和日志里）
- 叠加层（OBS 浏览器源）加「下一首 / 队列」字段，和桌面 HUD 保持一致

## 5. 已知坑（别再踩）

1. **安装器会删文件**：现在只清理 `*_MusicToppin.exe` 残留。加新依赖 DLL 时不要假设会被保留，确认 payload 里有。
2. **WebView2 必须在可见窗口里**：宿主窗口是屏幕外 2×2（`-32000,-32000`），不能用隐藏窗口，否则不渲染/不出声。
3. **B 站音频直链有热链保护**：所有 CDN 请求必须带 `Referer: https://www.bilibili.com`，否则 403。备用地址（`backupUrl`）要自动重试。
4. **playurl 目前不需要 wbi 签名**（2026-11 实测），但 B 站随时可能改；如果突然返回 `-403`，先怀疑这一点。
5. **弹幕包是 zlib 不是裸 deflate**：`DeflateStream` 要先剥 2 字节头 + 4 字节 Adler32；解出来的内容还是若干完整包，要递归拆。
6. **`DANMU_MSG` 的 `cmd` 可能带后缀**（`DANMU_MSG:4:0:2:2:2:0`），必须前缀匹配。
7. **冷场房间**（如房号 6 的赛事重播）能鉴权成功但收不到弹幕，别当成连接失败。
8. **csc 编译要带 `/nowarn:8632`**，且所有 `.cs` 里中文字符串用 UTF-8；`toolchain.ps1` 已处理。
9. **`git` 是便携版**：`D:\tools\MinGit\cmd\git.exe`（没进 PATH）。提交前先 `cd D:\music_toppin`。
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
20. **酷狗的登录标记不统一**：新版读注册表 `HKCU\Software\KuGoo3` 的 `LastLoginUserID`/`UserID`，
    老版可能在 `%APPDATA%\KuGou8\kugou.ini`；都没有就显示「未知」，别硬说成「未登录」。

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
- 设置：`HKCU\Software\MusicToppin`（`Hotkey`、`UpdateUrl`、`LiveRoomId`、`LiveAutoConnect`、`LiveToast`、`NeteaseCookie`、`QQMusicCookie`、`QQMusicUin`、`Overlay`、`CaptureOffscreen`、`CaptureOccluded` 等）
- 安装目录：默认 `%LocalAppData%\Programs\MusicToppin`，可自定义
- 单元测试：`run-tests.ps1`（124 项，不必联网）
- 叠加层：托盘开启后日志里有 `overlay server started at http://127.0.0.1:<port>/`，浏览器可直接打开验证

## 7. 新会话建议的第一次动作

1. `git checkout main && git pull`
2. `run-tests.ps1` + `build.ps1` 确认环境（应全绿、约 381 KB）
3. `MusicToppin.exe --room=5050` 跑 30 秒，看日志里有没有 `danmaku auth ok` 与 `song request`
4. `MusicToppin.exe --fake-danmaku="点歌 起风了"` 验证网易云链路（日志应有 `resolved:` + `queue play:`）
5. 接着做 **S3 剩余**：Apple Music（只有试听）或酷我/酷狗，见 `docs/v2-sources.md`

## 8. 分支与版本（重要）

- `main`：唯一开发线，已用 merge 提交把 **v1.1.1**（叠加层 / 离屏采集 / 遮挡模式）合进来，冲突点都在
  `TrayApp.cs`（菜单项/字段）、`Settings.cs`（注册表项）、`CHANGELOG.md`，处理原则是**两边都保留**。
- `fix/v1`：停在 v1.1.1 发布点，只做 V1 修复；不要在这里加 V2 功能。
- **版本号已升到 1.2.0**（`VersionInfo.cs` + `installer/Installer.cs`），但还没发布。
  发版时确认 GitHub Release 的 tag 与之一致，否则更新器会比对失败。
