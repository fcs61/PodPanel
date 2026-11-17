# Kapatıp açtıktan ya da uykudan sonra Bluetooth gelmediyse onarır. Yönetici olarak çalıştırılır.
#
# Neden gerekir: Windows "Kapat" dendiğinde (Hızlı Başlangıç açıkken) aslında hazırda bekletilir. Köprü servisi o sırada
# sanal Bluetooth adaptörünü yeniden bağlarsa Windows onu yarım tanır ve usbip'in sanal USB hub'ında "Bilinmeyen Aygıt"
# (kod 43) kaydı kalır; açılışta servis adaptörü yeniden bağlasa da Windows o bağlantı noktasını yeniden taramaz.
#
# Sıra (her adımdan sonra Bluetooth gelmişse durur):
#   1. Servisi durdur (adaptör serviste kalır), bozuk aygıt kaydını sil, aygıtları yeniden tarat, servisi başlat.
#   2. Servisi durdur, usbip'in sanal USB denetleyicisini kapatıp aç, servisi başlat.
#   3. Olmadıysa: Windows'u "Yeniden başlat" ile (Kapat ile değil) yeniden başlatmak gerekir.
# Sonuç kurulum\durum\bluetooth-onarim.txt dosyasına yazılır.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:ProgramFiles 'PodPanel\Bridge\WPods.Service.exe'
$serviceName = 'WPodsService'
$serviceLog = Join-Path $env:ProgramData 'WPods\logs\service.log'
$usbip = Join-Path $env:ProgramFiles 'USBip\usbip.exe'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'bluetooth-onarim.txt'
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
        $script:result += '--- servis günlüğü (son 60 satır) ---'
        $script:result += Get-Content $serviceLog -Tail 60 -ErrorAction SilentlyContinue
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

# Windows'un Bluetooth yığını çalışıyor: USB adaptör ve Bluetooth numaralandırıcısı hazır.
function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

# usbip'in sanal USB denetleyicisi.
function Get-Controller {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Service -eq 'usbip2_ude' } | Select-Object -First 1
}

# Sanal denetleyicinin hub'ına bağlı, düzgün çalışmayan aygıt kayıtları.
function Get-FailedChildren {
    $controller = Get-Controller
    if (-not $controller) { return @() }
    $failed = @()
    foreach ($hub in @((Get-PnpDeviceProperty -InstanceId $controller.InstanceId -KeyName DEVPKEY_Device_Children -ErrorAction SilentlyContinue).Data)) {
        if (-not $hub) { continue }
        foreach ($child in @((Get-PnpDeviceProperty -InstanceId $hub -KeyName DEVPKEY_Device_Children -ErrorAction SilentlyContinue).Data)) {
            if (-not $child) { continue }
            $device = Get-PnpDevice -InstanceId $child -ErrorAction SilentlyContinue
            if ($device -and $device.Status -ne 'OK') { $failed += $device }
        }
    }
    $failed
}

function Write-State([string]$title) {
    Note "--- $title"
    $service = Get-Service $serviceName -ErrorAction SilentlyContinue
    Note "  servis: $($service.Status)"
    (Invoke-Native $exe '--status').Text -split "`r?`n" | ForEach-Object { Note "  $_" }
    if (Test-Path $usbip) { (Invoke-Native $usbip 'port').Text -split "`r?`n" | Where-Object { $_ -match 'Port|usbip://' } | ForEach-Object { Note "  $($_.Trim())" } }
    foreach ($device in @(Get-FailedChildren)) {
        $code = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_ProblemCode -ErrorAction SilentlyContinue).Data
        $arrived = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_LastArrivalDate -ErrorAction SilentlyContinue).Data
        Note "  bozuk aygıt: $($device.InstanceId) (kod $code, geliş $arrived)"
    }
    Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' } | ForEach-Object { Note "  Bluetooth adaptörü: $($_.FriendlyName): $($_.Status)  $($_.InstanceId)" }
    Note "  Bluetooth hazır: $(Test-BluetoothUp)"
}

function Stop-Bridge {
    $stop = Invoke-Native $exe '--stop'
    Note "  --stop çıkış kodu: $($stop.Code) $($stop.Text)"
    for ($i = 0; $i -lt 10 -and (Get-Service $serviceName).Status -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
    (Get-Service $serviceName).Status -eq 'Stopped'
}

function Start-BridgeAndWait([int]$seconds) {
    $install = Invoke-Native $exe '--install'
    Note "  --install çıkış kodu: $($install.Code) $($install.Text)"
    for ($elapsed = 0; $elapsed -lt $seconds; $elapsed += 3) {
        Start-Sleep -Seconds 3
        if (Test-BluetoothUp) { Note "  Bluetooth $($elapsed + 3) saniyede geldi."; return $true }
    }
    Note "  Bluetooth $seconds saniyede gelmedi."
    $false
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Yönetici onayı isteniyor; onarım yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }
    if (-not (Test-Path $exe) -or -not (Get-Service $serviceName -ErrorAction SilentlyContinue)) { Finish 'Köprü servisi kurulu değil; bu betik yalnızca köprü kuruluyken işe yarar.' 3 }

    Write-State 'Başlangıç durumu'
    if (Test-BluetoothUp) { Finish 'Bluetooth zaten çalışıyor. Hiçbir şey değiştirilmedi.' 0 }

    # 1. Bozuk aygıt kaydını sil.
    Note '--- 1. deneme: bozuk aygıt kaydını sil'
    if (-not (Stop-Bridge)) { Finish 'Servis durdurulamadı. Windows''u "Yeniden başlat" ile yeniden başlat.' 4 }
    foreach ($device in @(Get-FailedChildren)) {
        Note "  siliniyor: $($device.InstanceId)"
        & "$env:SystemRoot\System32\pnputil.exe" /remove-device "$($device.InstanceId)" | Where-Object { "$_".Trim() } | ForEach-Object { Note "    $_" }
    }
    & "$env:SystemRoot\System32\pnputil.exe" /scan-devices | Where-Object { "$_".Trim() } | ForEach-Object { Note "    $_" }
    $ok = Start-BridgeAndWait 45
    Write-State '1. denemeden sonra'
    if ($ok) { Finish 'Bluetooth geri geldi (1. deneme: bozuk aygıt kaydını silmek yetti). Sohbete dönüp haber ver.' 0 }

    # 2. Sanal USB denetleyicisini kapatıp aç.
    Note '--- 2. deneme: usbip sanal USB denetleyicisini kapatıp aç'
    if (-not (Stop-Bridge)) { Finish 'Servis durdurulamadı. Windows''u "Yeniden başlat" ile yeniden başlat.' 4 }
    $controller = Get-Controller
    if ($controller) {
        try {
            Disable-PnpDevice -InstanceId $controller.InstanceId -Confirm:$false
            Start-Sleep -Seconds 3
            Enable-PnpDevice -InstanceId $controller.InstanceId -Confirm:$false
            Start-Sleep -Seconds 4
            Note "  denetleyici kapatılıp açıldı: $((Get-PnpDevice -InstanceId $controller.InstanceId).Status)"
        } catch {
            Note "  denetleyici kapatılıp açılamadı: $($_.Exception.Message)"
            try { Enable-PnpDevice -InstanceId $controller.InstanceId -Confirm:$false } catch { Note "  yeniden açma hatası: $($_.Exception.Message)" }
        }
    } else {
        Note '  usbip denetleyicisi bulunamadı.'
    }
    foreach ($device in @(Get-FailedChildren)) {
        Note "  siliniyor: $($device.InstanceId)"
        & "$env:SystemRoot\System32\pnputil.exe" /remove-device "$($device.InstanceId)" | Where-Object { "$_".Trim() } | ForEach-Object { Note "    $_" }
    }
    $ok = Start-BridgeAndWait 45
    Write-State '2. denemeden sonra'
    if ($ok) { Finish 'Bluetooth geri geldi (2. deneme: sanal denetleyiciyi kapatıp açmak gerekti). Sohbete dönüp haber ver.' 0 }

    Finish 'Bluetooth bu yolla gelmedi. Başlat > Güç > "Yeniden başlat" ile yeniden başlat ("Kapat" işe yaramaz). Sonra sohbete dönüp haber ver.' 2
} catch {
    Note "Beklenmeyen hata: $($_.Exception.Message)"
    $running = (Get-Service $serviceName -ErrorAction SilentlyContinue).Status -eq 'Running'
    if (-not $running) {
        try { $start = Invoke-Native $exe '--install'; Note "  servis yeniden başlatıldı: çıkış kodu $($start.Code)" } catch { Note "  servis başlatılamadı: $($_.Exception.Message)" }
    }
    Finish 'Onarım yarıda kaldı. Başlat > Güç > "Yeniden başlat" ile yeniden başlat, sonra sohbete dönüp haber ver.' 1
}
