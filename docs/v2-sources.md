# 曲源与登录：现状与候选平台调研

> 目标：观众发「点歌 xxx」，把歌放进队列、由本机播出来。
> 每条链路 = **搜索**（关键词 → 平台 id）→ **取流**（id → 可播 URL）→ **登录**（能放多少歌）。

## 1. 已接入

| 平台 | 搜索 | 取流 | 登录方式 | 说明 |
|---|---|---|---|---|
| **网易云** | `/api/search/get/web`（匿名可用） | `/api/song/enhance/player/url?br=320000` → `/song/media/outer/url` 兜底 | ① **自动读官方客户端已登录会话**（推荐，零操作）② 应用内扫码/手机号 ③ 手动 cookie | 游客能放免费曲；会员曲取决于账号 |
| **QQ 音乐** | `c.y.qq.com/soso/fcgi-bin/client_search_cp`（匿名可用） | `u.y.qq.com/cgi-bin/musicu.fcg` → `vkey.GetVkeyServer` | 应用内扫码（QQ/微信）；匿名时部分免费歌也能拿到 purl | 未登录时大部分歌 `code=1000` + 空 purl |
| **B 站** | BV 号直接解析（无需搜索） | `view` → `playurl?fnval=16`（DASH） | 不需要登录 | 视频点播，WebView2 播音频 |

### 网易云「官方客户端同步登录」是怎么做的

参考工具的文档写的是「登录信息由官方音乐软件负责」——理念相同，我们把它落到实现上：

```
%LOCALAPPDATA%\Netease\CloudMusic\webapp*xx*\LocalPrefs.json   → os_crypt.encrypted_key
        ↓ Base64 去 "DPAPI" 前缀 → CryptUnprotectData → 32 字节 AES 密钥
%LOCALAPPDATA%\Netease\CloudMusic\webapp*xx*\Cookies          → SQLite，winsqlite3.dll 读取
        ↓ encrypted_value = "v10" + nonce(12) + 密文 + tag(16)
        ↓ AES-256-GCM（BCrypt，.NET Framework 4.8 没有 AesGcm）
MUSIC_U / __csrf / NMTID  → 直接当请求 cookie
```

- 全部本机操作，不上传；读不到自动回退到「应用内登录」
- 客户端里换账号/退出登录后，重启应用会重新同步（或点「网易云：从官方客户端同步登录」）
- 依赖：Windows 10/11 自带的 `winsqlite3.dll`（老系统没有就回退）

## 2. 登录态检测（播放器检查）

| 平台 | 检测方式 | 说明 |
|---|---|---|
| 网易云 | 客户端 CEF cookie 解密（`winsqlite3` + DPAPI + AES-GCM） | 能直接拿到会话，还能顺便用来播放 |
| QQ 音乐 | `%APPDATA%\Tencent\QQMusic\ComData\qmcomdata.ini` → `[SectQMAPI] uin` | `0` = 未登录；文件没有时退回 `UserData\<QQ号>` 目录 |
| 酷狗 | 注册表 `HKCU\Software\KuGoo3`（`LastLoginUserID`/`UserID`）或 `kugou.ini` | 都找不到时显示「未知」 |

对话框还会显示「已安装 / 运行中」，没开的客户端可以直接点「启动」。

## 3. 候选平台（按性价比排序）

| 平台 | 搜索 | 取流 | 登录/风控 | 结论 |
|---|---|---|---|---|
| **酷我音乐** | `search.kuwo.cn/r.s?all=…` 匿名可用 | `antiserver.kuwo.cn/anti.s?type=convert_url&format=mp3&response=url&rid=MUSIC_<id>` **匿名直接给 mp3** | 基本不需要 | ⭐ 最优先：约 100 行，能明显提高「没登录也能点歌」的命中率 |
| **酷狗音乐** | `mobilecdn.kugou.com/api/v3/search/song` 匿名可用 | `trackercdn.kugou.com/i/v2/?…` 需要 `key = md5(hash + "kgcloud")` + `appid/pid`，返回 302 到 CDN | 免费曲匿名可用 | 中等成本（MD5 签名 + 302 跟随），值得做 |
| **汽水音乐（抖音）** | 有但需要 `X-Bogus` / `a_bogus` 签名 | 同上 | 风控强、接口常变 | ❌ 不建议，维护成本高 |
| **YouTube Music** | 需要 YouTube Data API + 第三方解析 | 音轨提取要 yt-dlp / 第三方服务 | 合规风险高 | ❌ 不做 |
| **Spotify** | 官方 API 可搜索 | 播放受 DRM 保护，无法直接取音频 | 必须 OAuth + Premium | ❌ 不可行 |
| **本地文件** | 扫目录 | 直接播 | 无 | ⏳ 可选兜底：主播自己放背景音乐时替代 SMTC 读取 |

> 另：苹果 Apple Music 网页端只有 30 秒试听，且需要 MusicKit token；保持「只入队、不播放」并提示。

## 4. 官方播放器 vs 内置播放器

| 方式 | 登录 | 版权/合规 | 进度/结束检测 | 代价 |
|---|---|---|---|---|
| **内置 WebView2（当前）** | 自己拿会话（客户端同步 / 扫码） | 自行取流，风险自负（仅个人直播互动） | 精确（500ms 轮询，可自动下一首） | 播放时 +160 MB |
| **官方客户端播放** | 完全由客户端负责 | 最稳妥 | 只能靠窗口标题/音频会话间接判断，切歌不及时 | 需要驱动客户端（`orpheus://` 在本机实测未响应，暂不可靠） |

结论：保留内置播放；登录用「客户端同步」拿到开箱即用的效果。若以后要完全合规，再研究官方客户端控制。

## 5. 验证方式

```powershell
# 网易云关键词（默认源）
MusicToppin.exe --fake-danmaku="点歌 起风了"
# 网易云指定 id
MusicToppin.exe --fake-danmaku="点歌 id:1330348068"
# QQ 音乐
MusicToppin.exe --fake-danmaku="点歌 qq 起风了"
# B 站
MusicToppin.exe --fake-danmaku="点歌 BV1GJ411x7h7"
```

日志关键词：`sources:`、`netease client:`、`netease account:`、`resolved:`、`queue play:`、`queue skip:`、`qq:`。
