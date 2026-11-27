$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\toolchain.ps1"

$root = $PSScriptRoot
$out = Join-Path $root 'build\PetTester'
$app = Join-Path $root 'build\app'
if (-not (Test-Path (Join-Path $app 'Microsoft.Web.WebView2.Core.dll'))) {
    throw '请先运行 build.ps1 生成 build\app（需要 WebView2 DLL）'
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($name in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
    Copy-Item (Join-Path $app $name) $out -Force
}

$refs = @(Get-FrameworkRefArgs) + @(Get-WinmdRefArgs)
$args = @('/target:winexe', ('/out:"' + (Join-Path $out 'SvgPetTester.exe') + '"')) + $refs + @(
    ('/r:"' + (Join-Path $out 'Microsoft.Web.WebView2.Core.dll') + '"'),
    ('/r:"' + (Join-Path $out 'Microsoft.Web.WebView2.WinForms.dll') + '"'),
    ('"' + (Join-Path $root 'tools\PetTester\Program.cs') + '"'),
    ('"' + (Join-Path $root 'src\MusicToppin\SvgPet.cs') + '"')
)

$code = Invoke-Csc -Arguments $args
if ($code -ne 0) { throw "SVG Pet Tester 编译失败 ($code)" }
Write-Host "完成：$(Join-Path $out 'SvgPetTester.exe')"
