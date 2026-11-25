# V2 交接说明（给新会话）

> 本文是「下一次继续开发 V2」的上下文快照。读完这一篇即可接手，不需要翻历史对话。

## 0. 一句话现状

**V1.1.1 已发布（叠加层 / 离屏采集 / 遮挡模式，已合进 main）；V2 已完成 S1（点歌指令 + 队列）、S2（B 站弹幕接入 + 点歌自动播放）、S3-网易云、S4（BV 播放链路）。**
剩下 S3 的 QQ / Apple Music、S5（展示整合）。版本号已升到 **1.2.0**（未发布）。

## 1. 仓库与构建

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/EonloveL/D-music_toppin |
| 分支 | `main` 是唯一开发线（V2 + v1.1.1 已合并）；`fix/v1` 保留为 v1.1.1 发布点 |
| 最新提交 | 见 `git log -1` |
| 构建 | `powershell -ExecutionPolicy Bypass -File .\build.ps1`（**不需要 .NET SDK**，首次自动下载工具链到 `.cache\nuget`） |
| 测试 | `powershell -ExecutionPolicy Bypass -File .\run-tests.ps1`（94 项，退出码 0 = 全过） |
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
| `tests/` | 94 项单元测试（点歌解析、队列、弹幕协议、JSON） |

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

### S3 网易云取流（已验证可播）

```
点歌 起风了 / id:1330348068
  → /api/search/get/web?s=…（或 /api/song/detail?ids=[id]）
  → /api/song/enhance/player/url?br=320000   （320k mp3 直链）
  → 失败再走 /song/media/outer/url?id=<id>.mp3（302 → 128k）
  → 隐藏 WebView2 <audio> 直接播（CDN 实测不校验 Referer）
```

| 文件 | 职责 |
|---|---|
| `src/MusicToppin/Netease.cs` | 搜索 / 详情 / 320k 取流 / outer 兜底 / 解析（纯函数可测） |
| `src/MusicToppin/SongResolvers.cs` | 按 `SongRequest.Source` 分派：`Auto`/`Netease` → 网易云，`Bilibili` → BV |
| `src/MusicToppin/ResolvedSong.cs` | 各平台统一的「可播放条目」（从 `Bilibili.cs` 拆出来） |

行为要点：

- **不需要 weapi 加密**：2026-11 实测 `/api/*` 带 UA + `Referer: https://music.163.com/` + `Cookie: os=pc; appver=8.9.70` 就能用
- 关键词取搜索前 3 个候选逐个试（搜索第一名常是翻唱）；`id:` 直接查详情
- 无版权 / 会员歌：`player/url` 返回 `code=404`、`outer/url` 302 到 `music.163.com/404` → 返回 null → 队列跳过并提示
- 游客身份拿不到 VIP 曲库；以后要做「登录 cookie」的话，从 `MUSIC_U` 读起

> 直播中**默认不弹任何通知**：点歌/连接/播放只写日志 + 更新菜单状态行，避免右下角弹窗干扰键鼠输入判定。
> 需要气泡时打开「弹幕点歌 → 点歌/播放时弹气泡提示」（`HKCU\Software\MusicToppin\LiveToast=1`）。

## 3. 关键决策与代价

| 决策 | 原因 | 代价 |
|---|---|---|
| 播放用 **WebView2** 而不是 mpv/ffmpeg | Chromium 原生支持 B 站 DASH(fMP4)，且能注入 Referer 绕过热链；mpv 要多 30–70 MB | 安装包 → **369 KB**；播放时多 7 个 `msedgewebview2` 进程，约 **+160 MB** 内存 |
| **双模式**：纯显示 / 点歌 | 保住 V1「轻量」卖点 | 首次播放时才创建 WebView2（纯显示模式 25–33 MB） |
| 弹幕用 **getConf** 而不是 getDanmuInfo | 新接口要 wbi 签名 + cookie，未签名直接 -352 | 老接口随时可能下线，届时要做 wbi |
| 鉴权 **protover=2**（只 zlib） | .NET Framework 4.8 没有 Brotli | 服务器若强制 brotli 就收不到弹幕（会记警告） |
| 点歌自动播放做成「队列驱动」 | S2 先闭环：弹幕 → 入队 → 出声 | 目前支持网易云 + B 站 BV；QQ/Apple 入队后会被跳过并提示 |
| 网易云走 **公开 `/api/*`** 而不是 weapi 加密 | 2026-11 实测游客可用，省掉 AES/RSA 一大段代码 | 只拿得到游客曲库；风控一变就得补 weapi |

## 4. 待办（建议顺序）

### S3 剩余：QQ 音乐 / Apple Music

- 分派已经做好了：`SongResolvers.Resolve()` 里加一个 `case`，各平台实现 `ISongResolver` 形状的方法即可
- 网易云已完成，可作为模板：`Netease.cs`（搜索 → 取流 → 兜底 → 解析纯函数 + 测试）
- QQ 音乐：非官方接口（`u.y.qq.com/cgi-bin/musicu.fcg`），需要构造 `comm` + `req_1`，返回的播放地址常带 `vkey` 有效期
- Apple Music：网页端无直链，基本只能播 30 秒试听或走 MusicKit（要开发者 token），建议先做成「只入队提示、不播放」并标注清楚
- 两个源都建议做成可开关，失败只影响单源；播放失败的兜底逻辑（跳过下一首）已经由队列负责

### S5 展示整合

- HUD 增加「队列预览」（下一首/排队数）——`NowPlaying` 已有 `PrevTitles/PrevArtists` 模式可参考，建议加 `NextTitle/NextArtist/QueueCount`
- 点歌成功/失败给一路弹幕提示（需要 S2 的发送能力；在那之前状态只在托盘菜单和日志里，不弹窗）
- 托盘「点歌队列」可以列出前几首（现在是数量 + 下一首）

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

## 6. 调试入口

```
MusicToppin.exe --demo                      演示数据（不读系统播放）
MusicToppin.exe --demo-fast                 演示数据，12 秒切一首
MusicToppin.exe --play-bv=BV1xx411c7mD      启动即播放指定 BV（自动化验证）
MusicToppin.exe --room=5050                 启动即连接直播间弹幕
MusicToppin.exe --fake-danmaku="点歌 BV…"   注入一条假弹幕，走「解析→入队→播放」全链路
MusicToppin.exe --fake-danmaku="点歌 起风了" 同上，但走网易云取流（S3 验证用）
```

- 日志：`%TEMP%\musictoppin.log`（`danmaku:`、`song request:`、`resolved:`、`queue play:`、`queue skip:`、`netease:`、`notify:`、`player failed:`、`overlay server started at` 是排错关键词）
- 设置：`HKCU\Software\MusicToppin`（`Hotkey`、`UpdateUrl`、`LiveRoomId`、`LiveAutoConnect`、`LiveToast`、`Overlay`、`CaptureOffscreen`、`CaptureOccluded` 等）
- 安装目录：默认 `%LocalAppData%\Programs\MusicToppin`，可自定义
- 单元测试：`run-tests.ps1`（94 项，不必联网）
- 叠加层：托盘开启后日志里有 `overlay server started at http://127.0.0.1:<port>/`，浏览器可直接打开验证

## 7. 新会话建议的第一次动作

1. `git checkout main && git pull`
2. `run-tests.ps1` + `build.ps1` 确认环境（应全绿、约 369 KB）
3. `MusicToppin.exe --room=5050` 跑 30 秒，看日志里有没有 `danmaku auth ok` 与 `song request`
4. `MusicToppin.exe --fake-danmaku="点歌 起风了"` 验证网易云链路（日志应有 `resolved:` + `queue play:`）
5. 接着做 **S3 剩余（QQ 音乐）**：照着 `Netease.cs` 的形状写 `QQ.cs`，在 `SongResolvers` 加 case

## 8. 分支与版本（重要）

- `main`：唯一开发线，已用 merge 提交把 **v1.1.1**（叠加层 / 离屏采集 / 遮挡模式）合进来，冲突点都在
  `TrayApp.cs`（菜单项/字段）、`Settings.cs`（注册表项）、`CHANGELOG.md`，处理原则是**两边都保留**。
- `fix/v1`：停在 v1.1.1 发布点，只做 V1 修复；不要在这里加 V2 功能。
- **版本号已升到 1.2.0**（`VersionInfo.cs` + `installer/Installer.cs`），但还没发布。
  发版时确认 GitHub Release 的 tag 与之一致，否则更新器会比对失败。
