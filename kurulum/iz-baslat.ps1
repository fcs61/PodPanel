# Kapanışta sanal Bluetooth adaptörünün bağlantısını neyin kestiğini bulmak için Windows'un ağ izlemesini başlatır.
# Yönetici gerektirmez (hesap "Performance Log Users" grubundaysa). Hiçbir ayarı değiştirmez; yalnızca TCP bağlantı
# olaylarını %USERPROFILE%\PodPanel\iz\kapanis.etl dosyasına yazar (en çok 512 MB, eskiler üzerine yazılır).
#
# Kullanım: bu betiği çalıştır, sonra Başlat > Güç > Kapat ile bilgisayarı kapat, birkaç dakika bekle, aç.
# İzleme, bilgisayar açıldıktan sonra kurulum\iz-durdur.ps1 ile ya da `logman stop PodPanelIz -ets` ile durdurulur.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE 'PodPanel\iz'
New-Item -ItemType Directory -Force $dir | Out-Null
$etl = Join-Path $dir 'kapanis.etl'

& logman.exe stop PodPanelIz -ets 2>$null | Out-Null
if (Test-Path -LiteralPath $etl) { Remove-Item -LiteralPath $etl -Force }

$created = & logman.exe create trace PodPanelIz -ets -o $etl -mode Circular -max 512 -nb 64 256 -bs 64 -ft 1 -p Microsoft-Windows-TCPIP 0xFFFFFFFFFFFFFFFF 4 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "İzleme başlatılamadı: $($created -join ' ')" -ForegroundColor Red
    Read-Host 'Pencereyi kapatmak için Enter''a bas' | Out-Null
    exit 1
}
# Yönetici olarak çalıştırıldıysa süreç ve USB olayları da eklenir; değilse sessizce atlanır.
foreach ($provider in 'Microsoft-Windows-Kernel-Process 0x10 4', 'Microsoft-Windows-WFP 0xFFFFFFFFFFFFFFFF 4', 'Microsoft-Windows-USB-USBHUB3 0xFFFFFFFFFFFFFFFF 4') {
    $parts = $provider -split ' '
    & logman.exe update trace PodPanelIz -ets -p $parts[0] $parts[1] $parts[2] 2>$null | Out-Null
}

Write-Host ''
Write-Host 'İzleme başladı.' -ForegroundColor Green
Write-Host 'Şimdi: Başlat > Güç > Kapat ile bilgisayarı kapat, birkaç dakika bekle, aç ve sohbete dön.'
Write-Host ''
Start-Sleep -Seconds 4
