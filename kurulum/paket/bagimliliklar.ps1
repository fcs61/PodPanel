# Depoda olmayan iki dış girdiyi indirir ve SHA-256 özetleriyle doğrular:
#   - usbip-win2 kurulum dosyası (yayımcısının kendi sürüm sayfasından)
#   - FFmpeg kitaplıkları (bu deponun "bagimlilik-ffmpeg-8.1" sürümündeki sabit kopya; BtbN'in "latest" derlemesi
#     her gün değiştiği için özetle sabitlenemez)
# Dosyalar yerindeyse ve özetleri tutuyorsa hiçbir şey indirmez. Yönetici gerektirmez; hiçbir şey kurmaz. Kullanım:
#   powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\bagimliliklar.ps1
param(
    # paketle.ps1'in FFmpeg kitaplıklarını aradığı klasör.
    [string]$Ffmpeg = (Join-Path $env:USERPROFILE 'PodPanel\app\ffmpeg'),
    # paketle.ps1'in usbip-win2 kurulum dosyasını aradığı klasör.
    [string]$Usbip = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
if (-not $Usbip) { $Usbip = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'usbip-win2' }

# Özetler paketle.ps1 ve kur.ps1 içindekilerle aynı olmalıdır.
$UsbipName = 'USBip-0.9.8.1-x64.exe'
$UsbipUrl = 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe'
$UsbipSha256 = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'

# FFmpeg 8.1, BtbN/FFmpeg-Builds "win64-lgpl-shared" derlemesinden değiştirilmeden alınan dosyalar.
$FfmpegUrl = 'https://github.com/fcs61/PodPanel/releases/download/bagimlilik-ffmpeg-8.1/ffmpeg-8.1-win64-lgpl-shared.zip'
$FfmpegFiles = [ordered]@{
    'avcodec-62.dll'   = '60060219F755DE8561409A529CE53B21A2C72FC3D4621B366D49B9254ED0DF6B'
    'avutil-60.dll'    = '22C964BCA3B239E463F0D156BC3786C145463073F27F72D762AB55981CEDAB47'
    'swresample-6.dll' = '00422269C0F0A58FB24DA0AA11E5A85DFACF1EE2E29631C7D3F3F380F746651E'
    'LICENSE.txt'      = 'DA7EABB7BAFDF7D3AE5E9F223AA5BDC1EECE45AC569DC21B3B037520B4464768'
}

function Test-Hash([string]$path, [string]$expected) {
    (Test-Path -LiteralPath $path) -and ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $expected)
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("podpanel-bagimlilik-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temp | Out-Null
try {
    # usbip-win2
    $usbipTarget = Join-Path $Usbip $UsbipName
    if (Test-Hash $usbipTarget $UsbipSha256) {
        Write-Host "usbip-win2: yerinde ve özeti doğru ($usbipTarget)"
    } else {
        Write-Host "usbip-win2 indiriliyor: $UsbipUrl"
        $download = Join-Path $temp $UsbipName
        Invoke-WebRequest -Uri $UsbipUrl -OutFile $download -UseBasicParsing
        if (-not (Test-Hash $download $UsbipSha256)) { throw 'İndirilen usbip-win2 kurulum dosyasının özeti beklenenle eşleşmiyor.' }
        New-Item -ItemType Directory -Force $Usbip | Out-Null
        Copy-Item -LiteralPath $download -Destination $usbipTarget -Force
        Write-Host "usbip-win2: indirildi ve doğrulandı ($usbipTarget)"
    }

    # FFmpeg
    $missing = @($FfmpegFiles.Keys | Where-Object { -not (Test-Hash (Join-Path $Ffmpeg $_) $FfmpegFiles[$_]) })
    if ($missing.Count -eq 0) {
        Write-Host "FFmpeg: yerinde ve özetleri doğru ($Ffmpeg)"
    } else {
        Write-Host "FFmpeg indiriliyor: $FfmpegUrl"
        $zip = Join-Path $temp 'ffmpeg.zip'
        Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing
        $extracted = Join-Path $temp 'ffmpeg'
        Expand-Archive -LiteralPath $zip -DestinationPath $extracted
        foreach ($name in $FfmpegFiles.Keys) {
            if (-not (Test-Hash (Join-Path $extracted $name) $FfmpegFiles[$name])) { throw "İndirilen FFmpeg dosyasının özeti beklenenle eşleşmiyor: $name" }
        }
        New-Item -ItemType Directory -Force $Ffmpeg | Out-Null
        foreach ($name in $FfmpegFiles.Keys) {
            Copy-Item -LiteralPath (Join-Path $extracted $name) -Destination (Join-Path $Ffmpeg $name) -Force
        }
        Write-Host "FFmpeg: indirildi ve doğrulandı ($Ffmpeg)"
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
