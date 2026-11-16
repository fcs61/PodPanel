# PodPanel simgesini çizer: mavi yuvarlak köşeli kare üzerinde iki beyaz kulaklık. Çıktı: podpanel.ico (16-256 px)
# ve podpanel.png (kurulum penceresinin başlığı için). Simge derlemeye dahil edilir; yeniden üretmek için:
#   powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\simge-uret.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\..\src\simge'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

# 256'lık tasarım ızgarasında çizer, istenen boyuta ölçekler.
function New-IconBitmap([int]$size) {
    $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $back = New-RoundedRect 8 8 240 240 56
    $rect = New-Object Drawing.RectangleF(8, 8, 240, 240)
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(255, 64, 160, 255), [Drawing.Color]::FromArgb(255, 10, 96, 214), 90.0)
    $g.FillPath($brush, $back)

    $white = New-Object Drawing.SolidBrush([Drawing.Color]::White)
    # Sol kulaklık: sap ve içe bakan baş. Sağdaki bunun aynadaki görüntüsü.
    foreach ($mirror in $false, $true) {
        $state = $g.Save()
        if ($mirror) {
            $g.TranslateTransform(256, 0)
            $g.ScaleTransform(-1, 1)
        }
        $stem = New-RoundedRect 62 84 26 124 13
        $g.FillPath($white, $stem)
        $g.FillEllipse($white, 60, 58, 62, 66)
        $g.Restore($state)
    }
    $g.Dispose()
    $bitmap
}

function Get-PngBytes([Drawing.Bitmap]$bitmap) {
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    , $stream.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $images += , (Get-PngBytes $bitmap)
    $bitmap.Dispose()
}

# ICO: başlık, her boyut için 16 baytlık dizin girdisi, ardından PNG verileri.
$ico = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'podpanel.ico'), $ico.ToArray())

$large = New-IconBitmap 192
$large.Save((Join-Path $OutDir 'podpanel.png'), [Drawing.Imaging.ImageFormat]::Png)
$large.Dispose()
"Simge yazıldı: $OutDir"
