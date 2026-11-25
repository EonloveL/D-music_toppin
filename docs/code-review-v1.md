# V1 三轮独立评审 · 交叉比对与修复记录

三个评审者各自独立读代码、互不通信，视角分别为：
**A. Win32 / 窗口与交互层** · **B. 数据采集层（SMTC / Core Audio）** · **C. 渲染 / 产品一致性 / 构建安装**。

下面是交叉比对结果：三份报告合并后共 60+ 条，这里只列**影响功能**的部分，以及每条的处理结论。

---

## 一、三方共同确认（最高优先级，已全部修复）

| # | 问题 | 谁提的 | 后果 | 修复 |
|---|---|---|---|---|
| 1 | **解锁后 1 秒内被自动回锁** | A | `LastInteractionUtc` 只在鼠标事件更新，锁定态收不到鼠标事件；`CheckRelock` 解锁后第一次 tick（1s）就把编辑态收回。热键/托盘解锁等于没用 | 解锁时刷新 `LastInteractionUtc`，并给 3 秒宽限；`CheckRelock` 增加 `IsEditing` 判断 |
| 2 | **首次向导弹两次（嵌套模态）** | A、C | 构造函数与 `ShowHud` 各排了一个 1.4s 定时器；`ShowDialog` 的嵌套消息循环会派发第二个 Tick，叠出两个向导 | 只保留构造函数一处调度；`ShowWizard` 加 `_wizardOpen` 互斥、`ShowDialog(_hud)` 指定 owner、进入前先置 `WizardShown` |
| 3 | **多屏位置被强制拉回主屏** | A、C | `PlaceHud` 恒用 `PrimaryScreen`，且把负坐标（左侧/上方副屏）判为非法；右侧副屏 x 超主屏右边界被夹回。每次 `ShowHud` 都会重犯 | 新增 `ClampToWorkArea()`（`Screen.FromRectangle` + 工作区夹取），`PlaceHud` / `MoveHudTo` 统一走它；删除负坐标判断 |
| 4 | **锚点定位不夹取，卡片会顶出屏幕** | C | `MoveHudTo` 用 `area.Bottom - h` 但没有任何下限；长标题 / 200% DPI / 绿幕加边后 `h` 变大，y 变负，整块飞出屏幕并写入注册表 | 同上，`MoveHudTo` 末尾 `ClampToWorkArea` |
| 5 | **颜色菜单色块错位** | A、C | `RefreshColorSwatches` 按 `DropDownItems` 序号赋值，而菜单顺序是 `[预设, 透明度, 分隔线, 主题色, 歌名, 歌手, ...]` → 预设/透明度多了色块，主题色显示成歌手色，歌名/歌手永不刷新 | 三个颜色项改为字段引用，`RefreshColorSwatches` 直接更新对应项，替换时 Dispose 旧 Bitmap |

## 二、数据层两个 P0（B 独有，已修复）

| # | 问题 | 后果 | 修复 |
|---|---|---|---|
| 6 | **全局端点峰值无法归属进程** | 任何声音（游戏、语音、视频）都会让暂停的网易云被判成「正在播放」，卡片永不隐藏；多播放器时不知道谁在出声 | 新增 `AudioSessions`：`IAudioSessionControl2.GetProcessId` 精确匹配播放器 PID + `GetState()` 判断 Active/Inactive；查不到才退回端点峰值，且峰值失败不再当作「播放中」 |
| 7 | **选源没有滞回** | 暂停的 SMTC 与窗口通道每 500ms 反复抢源，历史与「已播 N 首」被写脏、卡片反复闪 | 选源加入滞回：当前源还活着就保持，换源必须连续两拍确认；历史只在「该曲已连续播放 ≥15 秒」时记录（跳过不算播完） |

> 技术前提：`IAudioSessionControl2` 的 IID 从 mingw-w64 的 `audiopolicy.h` 中取得（`BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D`），vtable 顺序按接口定义对齐。

## 三、其余已修复

| # | 问题 | 来源 | 修复 |
|---|---|---|---|
| 8 | 拖动没有鼠标捕获，`MouseUp` 丢失后窗口粘着光标 | A | `Capture = true` + `OnMouseCaptureChanged` + 每帧校验左键仍按下 + 锁定/隐藏时 `CancelDrag()` |
| 9 | `HTTRANSPARENT` 是死代码，注释与实际相反 | A | 删除该分支，注释改为 `WS_EX_TRANSPARENT` + `SetWindowLong`（不触发 `UpdateStyles`） |
| 10 | 状态签名漏 `HasTimeline` 与历史内容 | A、C | 签名拼接 `HasTimeline` + 全部历史标题/歌手 |
| 11 | 渲染异常没有兜底，定时器里抛异常会杀掉托盘进程 | A、C | `Render()` / 动画 Tick 全部 try-catch 并写日志；标题最多 10 行，防超长标题撑爆窗口 |
| 12 | 关掉「显示浮窗」后再点一次才有反应（勾选态漂移） | A | `ToggleVisible` 以 `_hud.Visible` 为准 |
| 13 | 启动时 `Locked=false` 会导致「可点击但画成锁定态」 | A | 启动时同步 `SetEditing(!locked)` |
| 14 | 向导/透明度对话框在高 DPI 下不缩放 | A、C | 两个窗体加 `AutoScaleMode.Dpi` + `AutoScaleDimensions(96,96)` |
| 15 | 安装中关窗 → 后台线程回调已释放窗体 → 进程崩溃 + 半安装 | C | `FormClosing` 拦截 + 回调前校验 `IsHandleCreated/IsDisposed` + `ThreadException` 兜底 |
| 16 | 静默安装失败仍弹模态框、退出码恒 0 | C | `Main` 返回 int，失败返回 1；silent 模式只写日志不弹窗 |
| 17 | 透明度滑块每动一格写一次注册表 + 全量重绘 | A | 滑块只实时预览，关闭对话框时落盘一次 |
| 18 | 灰卡透明度只作用于填充，低透明度时边框比填充更实 | C | 边框/分隔线/进度槽的 alpha 同步乘以透明度并设下限 |
| 19 | 动画残影画到画布外被硬裁，形成重影暗带 | C | 残影改为「原位下移 14px + 淡出」，且目标矩形夹进画布 |
| 20 | 第二层灰卡文字对比度不足 | C | 层 alpha 178→196，文字 alpha 设下限 |
| 21 | SMTC 元数据脏标记先清后取，失败后永久陈旧 | B | 只有拿到非空标题才清脏标记；失败 1.5~2s 退避重试；换会话清空旧标题 |
| 22 | SMTC 选会话优先「第一个在播」而非 `GetCurrentSession` | B | 先判当前会话，再退而求其次；单个会话异常不影响其它会话 |
| 23 | 轮询无看门狗，SMTC 挂起后 HUD 永久冻结 | B | 记录轮询开始时间，超 5 秒强制复位 `_busy` 并重置 SMTC |
| 24 | 切歌路由与显示源不一致 | B | `Skip*` 按当前激活源路由；媒体键包装返回真实结果 |
| 25 | 托盘里播放器已停止仍挂一段旧标题 | B | 窗口不可见且不在出声 → 视为无会话 |
| 26 | 退出不清理定时器/菜单/模态窗 | A | `ExitApp` 关闭模态窗、Dispose 定时器与菜单、保存位置 |
| 27 | Apple Music 把「歌手 — 专辑」塞进歌手字段 | 实测发现 | 若 `—` 右侧以歌名开头，则只保留左侧歌手 |

## 四、评审中判定「不需要改」或「暂缓」

| 条目 | 结论 |
|---|---|
| `HTTRANSPARENT` 能替代 `WS_EX_TRANSPARENT` | ❌ 驳回：只在同线程窗口间生效，跨进程无效。已实测 `WindowFromPoint` 也不校验命中测试，不能作为验证手段 |
| `Bitmap.GetHbitmap` 会二次预乘导致偏暗 | 实测驳回：不透明红/半透明紫分别取样，屏幕值与「预乘一次」的理论值一致；仅把 `c*a/255` 改成四舍五入 |
| `TrackHistory` 与 `AppSettings` 跨线程写同一注册表键 | 已确认当前没有实际竞争（不同 value，且各自只在约定线程调用）；不改，仅记录 |
| 安装器/卸载器 DPI | 安装器用同一份 manifest，控件在 150% 下会有轻微拥挤，但非核心路径，暂缓 |
| `IMMNotificationClient` 设备热插拔通知 | 30 秒重建 + 2 秒会话缓存已能覆盖常见场景，暂缓（记录为已知限制） |
| csproj 与 csc 双构建链路不一致 | 保留 csproj 作为历史参考，实际构建只走 `build.ps1`，README 已写明 |

## 五、修复后实测

| 项 | 结果 |
|---|---|
| 解锁 5 秒后仍可编辑（原来 1 秒内回锁） | ✅ `clickthrough=False` |
| 12 秒无操作自动回锁 | ✅ `clickthrough=True` |
| 苹果音乐播放中显示 | ✅ 曲目/歌手正常，专辑后缀已剥离，无进度条（Apple 不报时长） |
| 静默安装 / 卸载退出码 | ✅ 均为 0，目录清理干净 |
| 内存 / CPU（Apple Music 播放中） | 47 MB 工作集 / 27 MB 私有 / 0.16% CPU |
| 连续 40 秒内存漂移 | 58.5 → 57.4 MB（修复前因 COM 未释放持续上涨） |
