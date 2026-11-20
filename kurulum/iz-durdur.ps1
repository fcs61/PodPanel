# kurulum\iz-baslat.ps1 ile başlatılan izlemeyi durdurur. Yönetici gerektirmez.
& logman.exe stop PodPanelIz -ets 2>&1 | Select-Object -Last 1
$etl = Join-Path $env:USERPROFILE 'PodPanel\iz\kapanis.etl'
if (Test-Path -LiteralPath $etl) { Write-Host ("İzleme dosyası: {0} ({1:N0} MB)" -f $etl, ((Get-Item -LiteralPath $etl).Length / 1MB)) }
