# V2 交接说明（给新会话）

> 本文是「下一次继续开发 V2」的上下文快照。读完这一篇即可接手，不需要翻历史对话。

## 0. 一句话现状

**V1 已发布（v1.1.0）；V2 已完成 S1（点歌指令 + 队列）、S2（B 站弹幕接入 + 点歌自动播放）、S4（BV 播放链路）。**
剩下 S3（多平台取流）、S5（展示整合）。

## 1. 仓库与构建

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/EonloveL/D-music_toppin |
| 分支 | `main`（V2 线）；`fix/v1` 是 V1 维护线（见 §8） |
| 最新提交 | 见 `git log -1`（`feat(v2): S2 弹幕接入`） |
| 构建 | `powershell -ExecutionPolicy Bypass -File .\build.ps1`（**不需要 .NET SDK**，首次自动下载工具链到 `.cache\nuget`） |
| 测试 | `powershell -ExecutionPolicy Bypass -File .\run-tests.ps1`（79 项，退出码 0 = 全过） |
| 产物 | `build\MusicToppin-Setup.exe`（约 361 KB）、`build\app\`（主程序 153 KB） |
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
| `tests/` | 79 项单元测试（点歌解析、队列、弹幕协议、JSON） |

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

托盘菜单已有：**「弹幕点歌」**（连接直播间/断开/队列）、**「点歌测试（播放 BV）…」**、**「停止播放」**。

## 3. 关键决策与代价

| 决策 | 原因 | 代价 |
|---|---|---|
| 播放用 **WebView2** 而不是 mpv/ffmpeg | Chromium 原生支持 B 站 DASH(fMP4)，且能注入 Referer 绕过热链；mpv 要多 30–70 MB | 安装包 → **361 KB**；播放时多 7 个 `msedgewebview2` 进程，约 **+160 MB** 内存 |
| **双模式**：纯显示 / 点歌 | 保住 V1「轻量」卖点 | 首次播放时才创建 WebView2（纯显示模式 25–33 MB） |
| 弹幕用 **getConf** 而不是 getDanmuInfo | 新接口要 wbi 签名 + cookie，未签名直接 -352 | 老接口随时可能下线，届时要做 wbi |
| 鉴权 **protover=2**（只 zlib） | .NET Framework 4.8 没有 Brotli | 服务器若强制 brotli 就收不到弹幕（会记警告） |
| 点歌自动播放做成「队列驱动」 | S2 先闭环：弹幕 → 入队 → 出声 | 目前只支持 B 站 BV；其他源入队后会被跳过并提示 |

## 4. 待办（建议顺序）

### S3 多平台取流

- 接口建议：`interface ISongResolver { Task<ResolvedSong> ResolveAsync(SongRequest req, CancellationToken ct); }`
- 每个平台一个实现，失败只影响单个源；`ResolveQueuedSongAsync`（`TrayApp.cs`）改成按 `request.Source` 分派
- 网易云：搜索 + 取流要 weapi/eapi 加密（参考项目 `BxNcm-DJ` 有实现可对照重写）
- QQ / Apple Music：非官方接口，风险更高，建议做成可开关的独立 resolver
- 网易云建议从「公开搜索接口 + 试听/低码率直链」起步，先打通链路再接加密

### S5 展示整合

- HUD 增加「队列预览」（下一首/排队数）——`NowPlaying` 已有 `PrevTitles/PrevArtists` 模式可参考，建议加 `NextTitle/NextArtist/QueueCount`
- 点歌成功/失败给一路弹幕提示（需要 S2 的发送能力，或至少托盘气泡）
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

## 6. 调试入口

```
MusicToppin.exe --demo                      演示数据（不读系统播放）
MusicToppin.exe --demo-fast                 演示数据，12 秒切一首
MusicToppin.exe --play-bv=BV1xx411c7mD      启动即播放指定 BV（自动化验证）
MusicToppin.exe --room=5050                 启动即连接直播间弹幕
MusicToppin.exe --fake-danmaku="点歌 BV…"   注入一条假弹幕，走「解析→入队→播放」全链路
```

- 日志：`%TEMP%\musictoppin.log`（`danmaku:`、`song request:`、`queue play:`、`queue skip:`、`player failed:` 是排错关键词）
- 设置：`HKCU\Software\MusicToppin`（`Hotkey`、`UpdateUrl`、`LiveRoomId`、`LiveAutoConnect`、`WizardShown` 等）
- 安装目录：默认 `%LocalAppData%\Programs\MusicToppin`，可自定义
- 单元测试：`run-tests.ps1`（79 项，不必联网）

## 7. 新会话建议的第一次动作

1. `git checkout main && git pull`（注意 §8 的分支情况）
2. `run-tests.ps1` + `build.ps1` 确认环境（应全绿、约 361 KB）
3. `MusicToppin.exe --room=5050` 跑 30 秒，看日志里有没有 `danmaku auth ok` 与 `song request`
4. 从 **S3 网易云**开始：先写 `ISongResolver` + `TrayApp.ResolveQueuedSongAsync` 的分派，再补具体平台

## 8. 分支现状（重要）

`main` 是 V2 线（BV 播放 + 弹幕点歌）；`fix/v1` 是 V1 维护线，另有两条 V1 功能提交：
**直播叠加层（OBS 浏览器源）** 与 **离屏采集模式**。

两边都改过 `TrayApp.cs` / `Settings.cs` / `README.md`，合并时会有冲突（菜单项、字段、文档段落），
建议做法：先把 `fix/v1` 合进 `main`，再在 `main` 上发布 V2；`fix/v1` 只保留 V1 修复。
