# 单元测试：编译源文件 + tests/ 下的测试，跑完用退出码表示结果（0 = 全过）。
# 不引测试框架，避免给构建加依赖；直接编译成 exe 跑。
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\toolchain.ps1"

$refs = @(Get-FrameworkRefArgs) + @(Get-WinmdRefArgs)
$brotli = Join-Path $PSScriptRoot 'src\MusicToppin\lib\BrotliSharpLib.dll'
$unsafe = Join-Path $PSScriptRoot 'src\MusicToppin\lib\System.Runtime.CompilerServices.Unsafe.dll'
$refs += '/r:"' + $brotli + '"'
$refs += '/r:"' + $unsafe + '"'
$app = Join-Path $PSScriptRoot 'src\MusicToppin'
$tests = Join-Path $PSScriptRoot 'tests'
$out = Join-Path $PSScriptRoot '.cache\unit-tests.exe'

New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

# 只编译测试用得到的源文件：纯逻辑 + 协议，不拉 WinForms/WebView2
$sources = @(
    'GlobalUsings.cs',
    'Logger.cs',
    'SongRequest.cs',
    'RequestQueue.cs',
    'Json.cs',
    'ResolvedSong.cs',
    'Danmaku.cs',
    'Netease.cs',
    'IniFile.cs',
    'QQMusic.cs',
    'Bilibili.cs',
    'SongResolvers.cs',
    'Playlist.cs',
    'Lyrics.cs',
    'OverlayServer.cs',
    'KillMatch.cs',
    'VideoCache.cs'
) | ForEach-Object { Join-Path $app $_ }
$sources += Get-ChildItem $tests -Filter *.cs -File | ForEach-Object { $_.FullName }

$arguments = @('/target:exe', "/out:`"$out`"") + $refs + ($sources | ForEach-Object { '"' + $_ + '"' })
$code = Invoke-Csc -Arguments $arguments
if ($code -ne 0) { exit $code }

Write-Host ""
& $out
exit $LASTEXITCODE
