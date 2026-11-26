# 把本地代码推到 GitHub

本机现在已经有可用的 `git`（如果换机器后 `git --version` 找不到，再按下面的「一次性的准备」装一个）。

## 一次性的准备

装一个 Git 客户端，任选其一：

- Git for Windows：https://git-scm.com/download/win
- GitHub Desktop：https://desktop.github.com/（图形界面，登录一次就行）
- 或 `winget install --id Git.Git -e`

## 首次推送（3 条命令）

在 `E:\D-music_toppin` 目录下开一个终端：

```powershell
git init
git add -A
git commit -m "feat: V1 悬浮窗 + 更新器；V2 队列核心与架构评估"
git branch -M main
git remote add origin https://github.com/EonloveL/D-music_toppin.git
git push -u origin main
```

如果远端仓库初始化时已经建了 README / LICENSE 导致冲突：

```powershell
git pull --rebase origin main
git push -u origin main
```

## 后续发版

```powershell
# 1. 改版本号：src/MusicToppin/VersionInfo.cs + installer/Installer.cs
# 2. 构建
powershell -ExecutionPolicy Bypass -File .\build.ps1
# 3. 提交 + 打 tag（tag 必须指向版本提交）
git add -A
git commit -m "release: v1.4.3"
git tag -a v1.4.3 -m "Music Toppin v1.4.3"
git push origin main --tags
# 4. 在 GitHub 上建 Release（已发布、Latest、不能 draft/prerelease）
#    只传 build\MusicToppin-Setup.exe，并确认 asset digest 为 sha256:… 非空
```

内置更新器默认读 `https://api.github.com/repos/EonloveL/D-music_toppin/releases/latest`，
所以只要 Release 里挂了 `MusicToppin-Setup.exe`，用户端就会自动收到更新。

## 仓库里会进什么

| 进仓库 | 不进仓库（`.gitignore`） |
|---|---|
| `src/`、`installer/`、`docs/`、`mock/` | `build/`（构建产物） |
| `build.ps1`、`toolchain.ps1`、`README.md`、`LICENSE` | `.cache/`（45 MB 工具链，构建时自动下载） |
| `docs/screenshots/*.png` | `*.exe`、`*.pdb` |

> 首次克隆后第一次 `build.ps1` 会自动下载 Roslyn + .NET Framework 引用程序集，不需要 .NET SDK。
