# V2 点歌姬：架构评估与实施计划

> 参考项目：[BxNcm-DJ](https://github.com/Taumata-wwq/BxNcm-DJ)（Electron + Vue3 + Chromium）。
> 本文回答两件事：**能不能在当前架构上做**，以及 **BV 点播会把架构推向哪里**。
>
> **说明**：本文是 V2 立项时的评估快照，里面的体积、测试数量和弹幕链路（当时还是 getConf + zlib）已被后续版本更新。
> 最新数据与实现请以 [README](../README.md)、[CHANGELOG](../CHANGELOG.md)、[v2-handoff](v2-handoff.md) 为准（当前版本 1.5.3）。

---

## 一、V1 的架构盘点

```
[SMTC / 窗口标题 / 音频会话] → NowPlayingService → HudForm(GDI+) + 托盘
```

- 只读：不播放、不联网、不碰版权内容
- 卖点：当时约 96 KB、26 MB 内存、0.16% CPU、无运行时依赖（当前约 430 KB）
- 已有可复用资产：分层窗口渲染、颜色/位置/透明度、绿幕、上屏向导、更新器、托盘与热键

## 二、V2 必须新增的四层

| 层 | 职责 | 是否改动现有代码 |
|---|---|---|
| ① 弹幕接入 | B站直播 WebSocket：房间握手、心跳、`DANMU_MSG` 解析、断线重连 | 新增，独立线程 |
| ② 指令解析 + 队列 | `点歌 wy/qq/am/BV…` 解析、去重、冷却、上限、顶歌、撤回 | **已完成**（见下） |
| ③ 曲源解析 | 网易云 / QQ / Apple Music 搜索取流、B站 `playurl` | 新增，网络 |
| ④ 播放引擎 | 真正把音频播出来，并对外提供进度/状态 | **新增，且是唯一真正的架构变更点** |

①②实现完，HUD/托盘/设置都不用动；③④才是工作量与风险的来源。

---

## 三、BV 点播的影响（核心评估）

### 3.1 技术事实

- B站音频走 **DASH**：`playurl?fnval=16` 返回分离的音频流（`.m4s`，AAC），需要 `Referer: https://www.bilibili.com` + 浏览器 UA；部分视频还要登录 cookie（`SESSDATA`）才能拿到高码率。
- 个别稿件只给 FLV（`fnval=1` 的老接口），还有 m3u8 直播流。

### 3.2 Windows 原生播放能力的实测结论

| 方案 | DASH/m4s | FLV | 热链头（Referer/UA） | 体积/内存代价 |
|---|---|---|---|---|
| WMP `MediaPlayer` | ❌ | ❌ | 无法设置 | 0 |
| Media Foundation | ⚠️ 不稳（fragmented MP4 支持看容器） | ❌ | 无法设置 | 0 |
| ffmpeg/mpv sidecar | ✅ | ✅ | ✅（命令行参数） | +30–70 MB 磁盘 |
| **WebView2 + HTML5 audio** | ✅ | ⚠️ 部分 | ✅（`WebResourceRequested` 注入） | +1 MB 安装包；运行时 Win11 自带、Win10 多数已装 Edge |

**结论：BV 点播会把「播放引擎」从"没有"变成"内嵌 Chromium 音频宿主"。** 这是 V2 唯一真正改变架构的地方——UI、HUD、队列、设置全部可以照用。

### 3.3 三个可选方案

| 方案 | 优点 | 缺点 | 建议 |
|---|---|---|---|
| **A. WebView2 隐藏音频宿主** | 格式覆盖最好（DASH/HLS/大部分 FLV）、能注入请求头、和参考项目的 Chromium 播放语义一致 | 多一个 `msedgewebview2.exe` 进程，内存 +80–150 MB；Win10 老机器可能没有运行时 | **推荐**，按需加载：只有点歌模式才创建 |
| B. mpv/ffmpeg sidecar | 内存低（~50 MB）、格式最全、可做本地缓存 | 体积 +30–70 MB，进程管理与 IPC 复杂，杀软更敏感 | 作为"无 WebView2 环境"的兜底 |
| C. Media Foundation | 零依赖、零体积 | DASH/FLV 不可靠，热链头无解 | 不采用 |

> 关键取舍：V1 的"极低占用"卖点在点歌模式下必然被打破。建议做成**双模式**——纯显示模式（当时 96 KB / 26 MB，当前约 430 KB / 25–33 MB）和点歌模式（加载 WebView2），用户自己选。

### 3.4 双模式下的状态源

```
点歌模式：Queue → PlaybackEngine →（自家状态：标题/歌手/进度/下一首）
                                    ↓
纯显示模式：（现有）SMTC / 窗口标题 / 音频会话
```

`NowPlayingService` 增加一个"自有源"优先级最高的分支即可，HUD 不用改渲染逻辑——只是"下一首"终于有真实值了。

---

## 四、目标架构

```
┌────────────────────── 主进程（WinForms，STA） ──────────────────────┐
│  TrayApp / HudForm / Settings（复用 V1）                            │
│                                                                    │
│  SongRequestParser ──► RequestQueue ──► QueueResolver（串行解析）    │
│        ▲                    │                    │                 │
│        │                    │                    ▼                 │
│  DanmakuClient(B站WS)       │            ISongResolver             │
│                             │        ┌────────┼─────────┐          │
│                             │     网易云     QQ     Apple/BV        │
│                             ▼                    │                 │
│                    PlaybackEngine ◄──────────────┘                 │
│                     ├─ WebView2Audio（默认）                        │
│                     └─ MpvEngine（兜底，可选）                       │
│                             │                                      │
│                             ▼                                      │
│                 NowPlayingService（自有源优先）→ HUD                │
└────────────────────────────────────────────────────────────────────┘
```

接口草案：

```csharp
internal interface IDanmakuSource
{
    event Action<DanmakuMessage> Received;
    Task ConnectAsync(int roomId, CancellationToken ct);
    Task DisconnectAsync();
}

internal interface ISongResolver
{
    Task<ResolvedSong> ResolveAsync(SongRequest request, CancellationToken ct); // 失败抛/返回 null
}

internal interface IPlaybackEngine : IDisposable
{
    Task PlayAsync(ResolvedSong song, CancellationToken ct);
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    event Action<PlaybackState> StateChanged;   // 进度、时长、播放/暂停、结束
}
```

## 五、与参考项目的移植可行性

| 参考项目模块 | 能否移植 | 说明 |
|---|---|---|
| 网易云加密（weapi/eapi/linuxapi） | ✅ 算法可移植 | 纯 C# 实现，约 300 行；`netease-crypto.ts` 可逐段对照重写 |
| B站 WBI 签名 | ✅ | `wbi.ts` 逻辑固定，C# 重写约 80 行 |
| B站弹幕协议 | ✅ | WS + zlib/brotli 解包 + JSON，约 500 行 |
| B站 playurl（含 Referer/cookie） | ✅ | 逻辑简单，难点在签名与风控 |
| 播放层（Chromium `<audio>`） | ✅ 思路可移植 | 我们用 WebView2 达到同等效果 |
| UI（Vue3 + Electron） | ❌ | 不搬；我们用 WinForms + GDI+ 已有 HUD |
| OBS 浏览器源（HTTP+WS 叠加页） | ⚠️ 可选 | 若要做"点歌板"给观众看，需要新增一个 HTML overlay 页面 |

**结论：可行，且不需要重写 V1。** 工作量集中在"加密/签名/风控"这些协议细节，和参考项目踩过的坑一致。

---

## 六、分阶段计划

| 阶段 | 内容 | 交付物 | 预估 |
|---|---|---|---|
| **S1（已完成）** | 指令解析 + 队列核心（去重/冷却/上限/顶歌/撤回） | `SongRequest.cs`、`RequestQueue.cs` + 单元测试 | ✅ |
| **S2（已完成）** | B站弹幕接入：房间握手、心跳、`DANMU_MSG`、断线重连 | `Danmaku.cs`（客户端 + 协议）、`Json.cs`；托盘「弹幕点歌」连接/断开/队列；点歌自动播放 | ✅ |
| **S3（网易云已完成）** | 曲源解析：网易云 ✅、B 站 playurl ✅、QQ、Apple Music | `Netease.cs` + `SongResolvers` 分派（`Auto`/`wy` → 网易云，`BV` → B 站）；QQ / Apple 待做 | 进行中 |
| **S4（已完成）** | 播放引擎：WebView2 音频宿主 + 队列驱动 + 进度回传 | 点歌可以真正出声，HUD 显示自有状态与「下一首」 | ✅ |
| S5 | 展示与调优：HUD 队列预览、点歌板叠加页、失败提示 | 完整 V2 | 1–2 天 |

每个阶段都可独立发布：S2 先做"能连上弹幕并在托盘打印点歌"，S3 先做单平台，S4 先做本机播放。

---

## 六点五、S4 实测结论（已完成验证）

用 `BV1xx411c7mD` 走通了完整链路：

```
view API（拿 cid/标题/时长，无需 cookie）
  → playurl?fnval=16（DASH，AAC 132kbps，mcdn/bilivideo CDN）
  → WebView2 <audio> + WebResourceRequested 注入 Referer
  → 正常播放：duration 2055s、currentTime 每秒 +1、readyState=4、无 error
  → HUD 显示 B 站标题/UP主 + 真实进度
```

实测数据：

| 项 | 数值 |
|---|---|
| 安装包 | 85.5 KB → **361 KB**（多了 WebView2 的 Core/WinForms/Loader 共 ~916 KB，压缩后 ~330 KB） |
| 主程序 | 105.5 KB → 153 KB |
| WebView2 进程 | 播放时 7 个（浏览器/渲染/GPU/工具），合计约 **160 MB**；不播放不创建 |
| 播放延迟 | 解析 + 起播约 1–2 秒 |

**结论**：方案 A 可行，BV 点播保留 WebView2；纯显示模式不受影响（不创建播放器就不加载 WebView2）。

踩坑记录：安装器原本会「删除除两个 exe 外的所有文件」，加入依赖 DLL 后会被误删——已改成只清理已知的 `*_MusicToppin.exe` 残留。

## 六点六、S2 实测结论（已完成验证）

直播间 **5050**（1.8M 人气）连续 75 秒：

| 项 | 数值 |
|---|---|
| 鉴权 | `code=0`，游客 `uid=0` 即可 |
| 收包 | op5/zlib 51 个、op5/明文 118 个、op3 人气包 2 个 |
| 弹幕 | 56 条（含 emoji、繁体昵称），`DANMU_MSG` 全部解析成功 |
| 重连 | 0 次（30 秒心跳正常，连接没掉） |

端到端：`--fake-danmaku="点歌 BV1GJ411x7h7"` → 解析 → 入队 → 取 DASH → WebView2 播放，日志里能看到 `song request` / `queue play`。

> 冷场房间（如房号 6 的赛事重播）可以鉴权成功但收不到弹幕，不是连接失败。协议细节见 [v2-danmaku.md](v2-danmaku.md)。

## 七、风险与合规

1. **非官方接口**：网易云/QQ/B站的搜索与取流都不是公开 API，随时可能变；建议每个源做成可替换插件，失败只影响单个源。
2. **风控**：B站 playurl 需要 `wbi` 签名 + `Referer` + UA；高频请求会被限流；网易云需要 cookie（`MUSIC_U`）。
3. **版权与平台规则**：点歌本质是播放他人内容；应默认只播"用户自己可见"的音频流，不做下载/分发；README 写明"仅供个人直播互动使用"。
4. **体积/内存**：点歌模式建议按需加载 WebView2，并在 UI 里如实标注内存变化。
5. **稳定性**：播放失败要能自动跳到下一首并提示，而不是卡住。

---

## 八、下一步建议

1. S3 剩余：Apple Music（只有试听）；QQ 音乐 / B 站 BV 已接入
2. S5 已完成（「下一首 / 队列」已进 HUD，点歌成功/失败有叠加层提示）
3. 发布前确认版本号（当前 1.5.3）与 GitHub Release tag 一致

本仓库当前状态：V1.1.1 已发布并合入 main；V2 的 S1（队列）、S2（弹幕）、S3（网易云 / QQ 音乐）、S4（BV 播放）、S5（下一首 / 队列 / 视频小窗）以及 v1.3 权限、v1.4 环状毛玻璃与上屏向导均已完成，带 217 项单元测试。
