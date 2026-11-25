# 共享工具链路径解析（不依赖 .NET SDK，全部来自本地缓存）
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cache = Join-Path $root '.cache\nuget'

$Csc = Join-Path $cache 'roslyn\tasks\net472\csc.exe'
$FwRef = Join-Path $cache 'fwref\build\.NETFramework\v4.8'
$Facades = Join-Path $FwRef 'Facades'
$WinmdDir = Join-Path $cache 'sdk-contracts\ref\netstandard2.0'
$WinRuntime = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'

if (-not (Test-Path $Csc)) { throw "缺少 Roslyn 编译器: $Csc" }
if (-not (Test-Path $FwRef)) { throw "缺少 .NET Framework 引用程序集: $FwRef" }

function Get-FrameworkRefArgs {
    $refs = @()
    $skip = @('System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll')
    Get-ChildItem $FwRef -Filter *.dll -File | Where-Object { $skip -notcontains $_.Name } | ForEach-Object { $refs += ('/r:"' + $_.FullName + '"') }
    Get-ChildItem $Facades -Filter System.Runtime.dll -File | ForEach-Object { $refs += ('/r:"' + $_.FullName + '"') }
    Get-ChildItem $Facades -Filter System.Runtime.InteropServices.WindowsRuntime.dll -File | ForEach-Object { $refs += ('/r:"' + $_.FullName + '"') }
    if (Test-Path $WinRuntime) { $refs += ('/r:"' + $WinRuntime + '"') }
    return $refs
}

function Get-WinmdRefArgs {
    $refs = @()
    if (Test-Path $WinmdDir) {
        Get-ChildItem $WinmdDir -Filter *.winmd -File | ForEach-Object { $refs += ('/r:"' + $_.FullName + '"') }
    }
    return $refs
}

function Invoke-Csc {
    param([string[]]$Arguments)
    $all = @('/nologo', '/langversion:12', '/nostdlib+', '/nowarn:8632,0108') + $Arguments
    $log = Join-Path $env:TEMP 'csc-last.log'
    $output = & $Csc @all 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $output) { Write-Host $line }
    $output | Set-Content -Path $log -Encoding UTF8
    return $code
}
