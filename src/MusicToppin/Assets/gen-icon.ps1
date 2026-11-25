$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# 背景圆角矩形 + 渐变
$rect = New-Object System.Drawing.RectangleF -ArgumentList 8, 8, ($size - 16), ($size - 16)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 48
$path.AddArc([single]$rect.X, [single]$rect.Y, [single]$r, [single]$r, 180, 90)
$path.AddArc([single]($rect.Right - $r), [single]$rect.Y, [single]$r, [single]$r, 270, 90)
$path.AddArc([single]($rect.Right - $r), [single]($rect.Bottom - $r), [single]$r, [single]$r, 0, 90)
$path.AddArc([single]$rect.X, [single]($rect.Bottom - $r), [single]$r, [single]$r, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
    (New-Object System.Drawing.PointF -ArgumentList 0, 0), `
    (New-Object System.Drawing.PointF -ArgumentList $size, $size), `
    ([System.Drawing.Color]::FromArgb(255, 26, 30, 38)), `
    ([System.Drawing.Color]::FromArgb(255, 10, 12, 16))
$g.FillPath($brush, $path)

# 左侧红色强调条
$g.SetClip($path)
$barBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
    (New-Object System.Drawing.PointF -ArgumentList 0, 0), `
    (New-Object System.Drawing.PointF -ArgumentList 0, $size), `
    ([System.Drawing.Color]::FromArgb(255, 255, 59, 48)), `
    ([System.Drawing.Color]::FromArgb(255, 255, 122, 69))
$g.FillRectangle($barBrush, 8, 8, 10, ($size - 16))
$g.ResetClip()

# 音符 ♪
$family = New-Object System.Drawing.FontFamily -ArgumentList 'Segoe UI Symbol'
$font = [System.Drawing.Font]::new($family, 132.0, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$white = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 245, 248, 252))
$box = New-Object System.Drawing.RectangleF -ArgumentList 20, 10, ($size - 40), ($size - 20)
$g.DrawString([string][char]0x266A, $font, $white, $box, $fmt)

$g.Dispose()

# 先写临时 PNG，再包成 ICO
$tmp = Join-Path $env:TEMP 'musictoppin-icon.png'
$bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$png = [System.IO.File]::ReadAllBytes($tmp)
Remove-Item $tmp -Force

$out = Join-Path $PSScriptRoot 'app.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]1)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)
$bw.Write($png)
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

"icon written: $out ($([math]::Round((Get-Item $out).Length / 1KB, 1)) KB)"
