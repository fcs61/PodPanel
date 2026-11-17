# Köprü servisini kapatma/uyku düzeltmesini içeren sürüme günceller ve onarım yolunu sınar. Yönetici olarak çalıştırılır.
#
#  1. 3-servis-guncelle.ps1 ile servisi kurulum\servis klasöründeki sürüme günceller (Bluetooth 10-20 saniye kesilir;
#     yeni sürüm çalışmazsa eski sürüme kendisi döner).
#  2. Servisten, usbip'in sanal USB denetleyicisini bir kez yeniden başlatmasını ister (kayıt değeri RestartVhciOnce) ve
#     servisi yeniden başlatır. Bluetooth'un geri geldiğini ve servis günlüğünde yeniden başlatmanın göründüğünü denetler.
#     Bu, kapatıp açtıktan sonra Bluetooth gelmediğinde servisin kendi kendine uygulayacağı onarımın aynısıdır.
# Sonuç kurulum\durum\uyku-duzeltmesi.txt dosyasına yazılır.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:ProgramFiles 'PodPanel\Bridge\WPods.Service.exe'
$dll = Join-Path $env:ProgramFiles 'PodPanel\Bridge\WPods.Service.dll'
$serviceName = 'WPodsService'
$serviceLog = Join-Path $env:ProgramData 'WPods\logs\service.log'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'uyku-duzeltmesi.txt'
New-Item -ItemType Directory -Force $dir | Out-Null
$result = @("Başlangıç: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")

function Note([string]$text) {
    $script:result += $text
    Write-Host $text
}

function Finish([string]$message, [int]$code) {
    $script:result += $message
    $script:result += "Sonuç kodu: $code"
    if (Test-Path $serviceLog) {
        $script:result += '--- servis günlüğü (son 50 satır) ---'
        $script:result += Get-Content $serviceLog -Tail 50 -ErrorAction SilentlyContinue
    }
    $script:result | Set-Content -Encoding utf8 $resultPath
    Write-Host ''
    Write-Host $message -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Yellow' })
    Write-Host ''
    Read-Host 'Pencereyi kapatmak için Enter''a bas' | Out-Null
    exit $code
}

function Invoke-Native([string]$file, [string]$arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo $file, $arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ Code = $process.ExitCode; Text = ("$output`n$($errorTask.Result)").Trim() }
}

function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Yönetici onayı isteniyor; işlem yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }

    # 1. Güncelleme: denenmiş betikle, beklemeden.
    Write-Host '--- 1. Servis güncelleniyor ---' -ForegroundColor Cyan
    $env:PODPANEL_DEVAM = '1'
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '3-servis-guncelle.ps1')
    $updateCode = $LASTEXITCODE
    $env:PODPANEL_DEVAM = ''
    $version = if (Test-Path $dll) { (Get-Item $dll).VersionInfo.ProductVersion } else { '' }
    Note "Güncelleme çıkış kodu: $updateCode; kurulu sürüm: $version"
    if ($updateCode -ne 0) { Finish 'Servis güncellenemedi; ayrıntı kurulum\durum\servis-guncelleme.txt dosyasında. Sohbete dönüp haber ver.' 2 }
    if ($version -notmatch 'noupdate\.(\d+)$' -or [int]$Matches[1] -lt 6) { Finish "Kurulu servis ($version) düzeltmeyi içermiyor. Sohbete dönüp haber ver." 3 }
    if (-not (Test-BluetoothUp)) { Finish 'Güncellemeden sonra Bluetooth hazır değil. kurulum\bluetooth-onar.ps1 betiğini çalıştır, sonra sohbete dön.' 4 }

    # 2. Onarım yolunun sınaması: servis denetleyiciyi kendisi yeniden başlatsın.
    Write-Host ''
    Write-Host '--- 2. Onarım yolu sınanıyor (Bluetooth 10-15 saniye kesilir) ---' -ForegroundColor Cyan
    $startText = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Set-ItemProperty 'HKLM:\SOFTWARE\WPods\Service' -Name RestartVhciOnce -Value 1 -Type DWord
    $stop = Invoke-Native $exe '--stop'
    Note "--stop çıkış kodu: $($stop.Code) $($stop.Text)"
    for ($i = 0; $i -lt 10 -and (Get-Service $serviceName).Status -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
    $install = Invoke-Native $exe '--install'
    Note "--install çıkış kodu: $($install.Code) $($install.Text)"
    $up = $false
    for ($elapsed = 0; $elapsed -lt 90 -and -not $up; $elapsed += 3) {
        Start-Sleep -Seconds 3
        $up = Test-BluetoothUp
    }
    Note "Bluetooth hazır: $up ($elapsed saniyede)"
    $lines = @(Get-Content $serviceLog -Tail 200 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)' -and $Matches[1] -ge $startText -and $_ -match 'host controller' })
    $lines | ForEach-Object { Note "  $_" }
    $restarted = [bool]($lines | Where-Object { $_ -match 'restarted: enable CR 0x0, started True' })
    $pending = (Get-ItemProperty 'HKLM:\SOFTWARE\WPods\Service' -Name RestartVhciOnce -ErrorAction SilentlyContinue).RestartVhciOnce -eq 1
    Note "Servis denetleyiciyi yeniden başlattı: $restarted; istek hâlâ bekliyor: $pending"

    if ($up -and $restarted) {
        Finish 'Servis güncellendi ve onarım yolu çalışıyor. Şimdi asıl deneme: Başlat > Güç > Kapat ile bilgisayarı kapat, yarım dakika bekle, aç. Bluetooth''un gelip gelmediğini sohbete yaz.' 0
    }
    if ($up) {
        Finish 'Servis güncellendi ve Bluetooth çalışıyor, ama denetleyicinin yeniden başlatıldığı günlükte görünmedi. Sohbete dönüp haber ver.' 5
    }
    Finish 'Sınamadan sonra Bluetooth gelmedi. kurulum\bluetooth-onar.ps1 betiğini çalıştır, sonra sohbete dönüp haber ver.' 6
} catch {
    Note "Beklenmeyen hata: $($_.Exception.Message)"
    Finish 'İşlem yarıda kaldı. Bluetooth çalışmıyorsa kurulum\bluetooth-onar.ps1 betiğini çalıştır; sonra sohbete dönüp haber ver.' 1
}
