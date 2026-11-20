# Dağıtım paketini üretir: tek dosyalık kurulum programı (PodPanel-Kurulum-<sürüm>.exe).
# Yönetici gerektirmez; hiçbir şey kurmaz. Kullanım:
#   powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\paketle.ps1 [-Surum 1.0.0] [-CalismaZamani 8.0.x]
# Girdi:
#   src\App                         panel (kendi çalışma zamanıyla yayımlanır; hedef bilgisayarda .NET gerekmez)
#   %USERPROFILE%\PodPanel\app\ffmpeg   FFmpeg kitaplıkları ve lisansı
#   kurulum\servis + servis.sha256  derlenmiş köprü servisi (özet listesiyle doğrulanır)
#   kurulum\usbip-win2              usbip-win2 kurulum dosyası (özeti doğrulanır)
#   kurulum\paket\*.ps1             kurulum ve kaldırma betikleri
# Çıktı: %USERPROFILE%\PodPanel\dagitim
param(
    [string]$Surum = '1.0.0',
    # Panele eklenecek .NET 8 çalışma zamanı sürümü; boşsa SDK'nın bildiği sürüm kullanılır.
    [string]$CalismaZamani = '',
    [string]$Cikti = (Join-Path $env:USERPROFILE 'PodPanel\dagitim'),
    # Köprüsüz paket: köprü servisi ve usbip-win2 olmadan, yalnızca panel.
    [switch]$Koprusuz
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$work = Join-Path $env:USERPROFILE 'PodPanel\tools\build\paket'
$payload = Join-Path $work 'payload'
$UsbipSetup = 'USBip-0.9.8.1-x64.exe'
$UsbipSha256 = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'

function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) başarısız oldu (çıkış kodu $LASTEXITCODE)." }
}

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force $payload, $Cikti | Out-Null

Step 'Panel yayımlanıyor (kendi çalışma zamanıyla)'
$publish = @('publish', (Join-Path $repo 'src\App\App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:CiktiEki=-sc', "-p:Version=$Surum", '-p:DebugType=none', '-o', (Join-Path $payload 'app'), '-nologo', '-v', 'q')
if ($CalismaZamani) { $publish += "-p:CalismaZamani=$CalismaZamani" }
Invoke-Dotnet $publish
Get-ChildItem (Join-Path $payload 'app') -Filter *.pdb -Recurse | Remove-Item -Force
$ffmpeg = Join-Path $env:USERPROFILE 'PodPanel\app\ffmpeg'
foreach ($name in 'avcodec-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'LICENSE.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $ffmpeg $name))) { throw "FFmpeg dosyası eksik: $ffmpeg\$name" }
}
New-Item -ItemType Directory -Force (Join-Path $payload 'app\ffmpeg') | Out-Null
Copy-Item -Path (Join-Path $ffmpeg '*') -Destination (Join-Path $payload 'app\ffmpeg') -Force

if ($Koprusuz) {
    Step 'Köprüsüz paket: köprü servisi ve usbip-win2 eklenmiyor'
    $bridgeVersion = '-'
} else {
Step 'Köprü servisi doğrulanıyor ve kopyalanıyor'
$service = Join-Path $repo 'kurulum\servis'
$expected = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $repo 'kurulum\servis.sha256'), [Text.Encoding]::UTF8)) {
    if ($line -match '^([0-9A-Fa-f]{64})\s+(.+)$') { $expected[$Matches[2]] = $Matches[1].ToUpperInvariant() }
}
$files = @(Get-ChildItem -LiteralPath $service -Recurse -File)
if ($files.Count -ne $expected.Count) { throw "Servis klasöründe $($files.Count) dosya var, özet listesinde $($expected.Count)." }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($service.Length + 1)
    if ($expected[$relative] -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "Servis dosyası özet listesiyle eşleşmiyor: $relative" }
}
Copy-Item -LiteralPath $service -Destination (Join-Path $payload 'bridge') -Recurse
$bridgeVersion = (Get-Item (Join-Path $service 'WPods.Service.dll')).VersionInfo.ProductVersion

Step 'usbip-win2 kurulum dosyası'
$usbip = Join-Path $repo "kurulum\usbip-win2\$UsbipSetup"
if ((Get-FileHash -LiteralPath $usbip -Algorithm SHA256).Hash -ne $UsbipSha256) { throw 'usbip-win2 kurulum dosyasının özeti beklenenle eşleşmiyor.' }
New-Item -ItemType Directory -Force (Join-Path $payload 'usbip') | Out-Null
Copy-Item -LiteralPath $usbip -Destination (Join-Path $payload 'usbip')
}

Step 'Betikler, sürüm, lisanslar ve kaynak kodu'
New-Item -ItemType Directory -Force (Join-Path $payload 'kurulum') | Out-Null
foreach ($name in 'ortak.ps1', 'kur.ps1', 'kaldir.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $payload 'kurulum')
}
$kind = if ($Koprusuz) { "koprusuz`r`n" } else { '' }
[IO.File]::WriteAllText((Join-Path $payload 'surum.txt'), "$Surum`r`n$bridgeVersion`r`n$kind", (New-Object Text.UTF8Encoding $false))
Copy-Item -LiteralPath (Join-Path $repo 'bridge\LICENSE') -Destination (Join-Path $payload 'LISANS.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UCUNCU-TARAFLAR.txt') -Destination $payload

# Kaynak kodu: derleme çıktıları ve ikili dosyalar olmadan.
$source = Join-Path $work 'kaynak'
New-Item -ItemType Directory -Force $source | Out-Null
foreach ($name in 'src', 'bridge') {
    $from = Join-Path $repo $name
    foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File) {
        $relative = $file.FullName.Substring($repo.Length + 1)
        if ($relative -match '\\(bin|obj)\\') { continue }
        $target = Join-Path $source $relative
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
New-Item -ItemType Directory -Force (Join-Path $source 'kurulum\paket') | Out-Null
Copy-Item -Path (Join-Path $repo 'kurulum\*.ps1') -Destination (Join-Path $source 'kurulum')
Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination (Join-Path $source 'kurulum\paket')
foreach ($name in 'README.md', 'LICENSE', 'Directory.Build.props', 'Directory.Build.targets') { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $source }
[IO.Compression.ZipFile]::CreateFromDirectory($source, (Join-Path $payload 'kaynak.zip'), 'Optimal', $false)

Step 'Kaldırma programı (Kaldir.exe)'
Invoke-Dotnet @('build', (Join-Path $repo 'src\Setup\Setup.csproj'), '-c', 'Release', '-p:Tur=Kaldir', '-p:CiktiEki=-kaldir', "-p:Version=$Surum",
    '-p:DebugType=none', '-o', (Join-Path $work 'kaldir'), '-nologo', '-v', 'q')
Copy-Item -LiteralPath (Join-Path $work 'kaldir\Kaldir.exe') -Destination $payload

Step 'Özet listesi'
$lines = foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)  $($file.FullName.Substring($payload.Length + 1))"
}
[IO.File]::WriteAllLines((Join-Path $payload 'dosyalar.sha256'), $lines, (New-Object Text.UTF8Encoding $false))
$payloadSize = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("   {0} dosya, {1:N0} MB" -f $lines.Count, ($payloadSize / 1MB))

Step 'Dosyalar sıkıştırılıyor'
$zip = Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, 'Optimal', $false)
Write-Host ("   payload.zip: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB))

Step 'Kurulum programı (Kurulum.exe)'
Invoke-Dotnet @('build', (Join-Path $repo 'src\Setup\Setup.csproj'), '-c', 'Release', "-p:Payload=$zip", '-p:CiktiEki=-kur', "-p:Version=$Surum",
    '-p:DebugType=none', '-o', (Join-Path $work 'kur'), '-nologo', '-v', 'q')
$name = if ($Koprusuz) { "PodPanel-Kurulum-$Surum-koprusuz" } else { "PodPanel-Kurulum-$Surum" }
$final = Join-Path $Cikti "$name.exe"
Copy-Item -LiteralPath (Join-Path $work 'kur\Kurulum.exe') -Destination $final -Force
$hash = (Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $Cikti "$name.sha256.txt"), "$hash  $name.exe`r`n", (New-Object Text.UTF8Encoding $false))

# Paketi alacak kişi için kısa kullanım notu (Not Defteri doğru okusun diye BOM ile).
[IO.File]::WriteAllText((Join-Path $Cikti 'OKU-BENI.txt'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'OKU-BENI.txt'), [Text.Encoding]::UTF8).Replace('1.0.0', $Surum), (New-Object Text.UTF8Encoding $true))

Write-Host ''
Write-Host "Hazır: $final" -ForegroundColor Green
Write-Host ("Boyut: {0:N0} MB   SHA-256: {1}" -f ((Get-Item $final).Length / 1MB), $hash)
Write-Host "Panel sürümü $Surum, köprü servisi $bridgeVersion"
