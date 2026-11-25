$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cacheRoot = Join-Path $root '.cache\nuget'

# 首次构建自动拉取工具链（Roslyn 编译器 + .NET Framework 引用程序集 + WinRT 元数据）
if (-not (Test-Path (Join-Path $cacheRoot 'roslyn\tasks\net472\csc.exe')))
{
    Write-Host "== [0/5] 下载构建工具链（约 45 MB，仅首次）" -ForegroundColor Cyan
    $packages = @(
        @{ Name = 'sdk-contracts'; Id = 'microsoft.windows.sdk.contracts'; Version = '10.0.19041.1' },
        @{ Name = 'fwref';         Id = 'microsoft.netframework.referenceassemblies.net48'; Version = '1.0.3' },
        @{ Name = 'roslyn';        Id = 'microsoft.net.compilers.toolset'; Version = '4.11.0' },
        @{ Name = 'webview2';      Id = 'microsoft.web.webview2'; Version = '1.0.4370.26' }
    )

    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($package in $packages)
    {
        $nupkg = Join-Path $cacheRoot ($package.Name + '.nupkg')
        $target = Join-Path $cacheRoot $package.Name
        if (-not (Test-Path $nupkg))
        {
            $url = 'https://api.nuget.org/v3-flatcontainer/' + $package.Id + '/' + $package.Version + '/' + $package.Id + '.' + $package.Version + '.nupkg'
            Write-Host "   下载 $($package.Id) $($package.Version)"
            Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
        }
        if (-not (Test-Path $target))
        {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $target)
        }
    }
}

. "$PSScriptRoot\toolchain.ps1"

$appOut = Join-Path $root 'build\app'
$setup = Join-Path $root 'build\MusicToppin-Setup.exe'
$payload = Join-Path $root 'build\payload.zip'
$icon = Join-Path $root 'src\MusicToppin\Assets\app.ico'
$manifest = Join-Path $root 'src\MusicToppin\app.manifest'
$appExe = Join-Path $appOut 'MusicToppin.exe'

# 先清空输出目录，避免上一次的残留被打进 payload
if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $appOut | Out-Null

Write-Host "== [1/5] 图标" -ForegroundColor Cyan
if (-not (Test-Path $icon)) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\MusicToppin\Assets\gen-icon.ps1')
}

Write-Host "== [2/5] 编译主程序（.NET Framework 4.8，产物几十 KB）" -ForegroundColor Cyan
$refs = @(Get-FrameworkRefArgs) + @(Get-WinmdRefArgs)

$webView2 = Join-Path $cacheRoot 'webview2\pkg\lib\net462'
if (Test-Path (Join-Path $webView2 'Microsoft.Web.WebView2.Core.dll'))
{
    $refs += '/r:"' + (Join-Path $webView2 'Microsoft.Web.WebView2.Core.dll') + '"'
    $refs += '/r:"' + (Join-Path $webView2 'Microsoft.Web.WebView2.WinForms.dll') + '"'
}
$sources = Get-ChildItem (Join-Path $root 'src\MusicToppin') -Filter *.cs -File | ForEach-Object { '"' + $_.FullName + '"' }
$appArgs = @(
    '/target:winexe',
    "/out:`"$appExe`"",
    "/win32icon:`"$icon`"",
    "/win32manifest:`"$manifest`"",
    '/optimize+',
    '/debug-',
    '/platform:anycpu'
) + $refs + $sources
$code = Invoke-Csc -Arguments $appArgs
if ($code -ne 0) { throw "主程序编译失败 ($code)" }
if (Test-Path (Join-Path $webView2 'Microsoft.Web.WebView2.Core.dll'))
{
    Copy-Item (Join-Path $webView2 'Microsoft.Web.WebView2.Core.dll') $appOut -Force
    Copy-Item (Join-Path $webView2 'Microsoft.Web.WebView2.WinForms.dll') $appOut -Force
    Copy-Item (Join-Path $cacheRoot 'webview2\pkg\runtimes\win-x64\native\WebView2Loader.dll') $appOut -Force
}
Write-Host ("   MusicToppin.exe = " + [math]::Round((Get-Item $appExe).Length / 1KB, 1) + " KB")

Write-Host "== [3/5] 编译卸载器" -ForegroundColor Cyan
$uninstall = Join-Path $appOut 'Uninstall.exe'
$unArgs = @(
    '/target:winexe',
    "/out:`"$uninstall`"",
    "/win32icon:`"$icon`"",
    '/optimize+',
    '/debug-'
) + $refs + @('"' + (Join-Path $root 'installer\Uninstall.cs') + '"')
$code = Invoke-Csc -Arguments $unArgs
if ($code -ne 0) { throw "卸载器编译失败 ($code)" }

Write-Host "== [4/5] 打包 payload" -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($appOut, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("   payload.zip = " + [math]::Round((Get-Item $payload).Length / 1KB, 1) + " KB")

Write-Host "== [5/5] 编译安装器" -ForegroundColor Cyan
$setupArgs = @(
    '/target:winexe',
    "/out:`"$setup`"",
    "/win32icon:`"$icon`"",
    "/win32manifest:`"$manifest`"",
    "/resource:`"$payload`",payload.zip",
    '/optimize+',
    '/debug-'
) + $refs + @('"' + (Join-Path $root 'installer\Installer.cs') + '"')
$code = Invoke-Csc -Arguments $setupArgs
if ($code -ne 0) { throw "安装器编译失败 ($code)" }

Write-Host ""
$setupKb = [math]::Round((Get-Item $setup).Length / 1KB, 1)
Write-Host "完成：$setup  ($setupKb KB)" -ForegroundColor Green
