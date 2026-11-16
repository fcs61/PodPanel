# Aygıt, sürücü ve güvenlik durumunun anlık görüntüsünü alır; yönetici yetkisi gerekmez, hiçbir şeyi değiştirmez.
#   -Mod taban        : görüntüyü durum\taban.json olarak kaydeder (kurulumdan önce).
#   -Mod karsilastir  : şimdiki durumu taban ile karşılaştırır, raporu durum\karsilastirma.txt dosyasına da yazar.
param(
    [ValidateSet('taban', 'karsilastir')]
    [string]$Mod = 'karsilastir'
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'durum'
$basePath = Join-Path $dir 'taban.json'
New-Item -ItemType Directory -Force $dir | Out-Null

function Get-Snapshot {
    $devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{
            Id      = $_.InstanceId
            Class   = "$($_.Class)"
            Name    = "$($_.FriendlyName)"
            Status  = "$($_.Status)"
            Problem = [int]$_.ConfigManagerErrorCode
        }
    }
    $hubs = foreach ($hub in $devices | Where-Object { $_.Id -like 'USB\ROOT_HUB30\*' }) {
        $filters = (Get-PnpDeviceProperty -InstanceId $hub.Id -KeyName DEVPKEY_Device_UpperFilters -ErrorAction SilentlyContinue).Data
        [pscustomobject]@{ Id = $hub.Id; Status = $hub.Status; UpperFilters = @($filters) -join ',' }
    }
    $drivers = Get-CimInstance Win32_SystemDriver -Filter "Name LIKE 'usbip%'" | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; State = $_.State; StartMode = $_.StartMode; Path = $_.PathName }
    }
    $guard = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue
    $secureBoot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -ErrorAction SilentlyContinue).UEFISecureBootEnabled
    $startOptions = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -ErrorAction SilentlyContinue).SystemStartOptions
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{
        Taken        = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        Boot         = $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss')
        Build        = "$($os.Version).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"
        SecureBoot   = [int]$secureBoot
        Hvci         = [bool]($guard.SecurityServicesRunning -contains 2)
        TestSigning  = [bool]("$startOptions" -match 'TESTSIGNING')
        UsbipExe     = Test-Path 'C:\Program Files\USBip\usbip.exe'
        UsbipDrivers = @($drivers)
        RootHubs     = @($hubs)
        Devices      = @($devices)
    }
}

function Get-BootEvents([datetime]$since) {
    # Açılıştan beri sürücü yükleme hataları, engellenen sürücüler ve çökme kayıtları.
    $lines = @()
    $queries = @(
        @{ LogName = 'System'; StartTime = $since; Level = 1, 2 },
        @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; StartTime = $since; Level = 1, 2, 3 }
    )
    foreach ($query in $queries) {
        try {
            $events = Get-WinEvent -FilterHashtable $query -MaxEvents 60 -ErrorAction Stop
        } catch {
            if ($_.Exception.Message -notmatch 'No events were found|bulunamad') {
                $lines += "  ($($query.LogName) okunamadı: $($_.Exception.Message))"
            }
            continue
        }
        foreach ($e in $events) {
            $text = ("$($e.Message)" -split "`r?`n")[0]
            if ($text.Length -gt 170) { $text = $text.Substring(0, 170) + '...' }
            $lines += "  $($e.TimeCreated.ToString('HH:mm:ss')) $($e.ProviderName) $($e.Id): $text"
        }
    }
    $lines
}

$now = Get-Snapshot
if ($Mod -eq 'taban') {
    $now | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $basePath
    "Taban kaydedildi: $($now.Devices.Count) aygıt, derleme $($now.Build), Secure Boot $($now.SecureBoot), bellek bütünlüğü $($now.Hvci), test imzası $($now.TestSigning), usbip kurulu $($now.UsbipExe)"
    return
}

if (-not (Test-Path $basePath)) { throw "Taban yok: önce -Mod taban ile çalıştır." }
$base = Get-Content $basePath -Raw -Encoding utf8 | ConvertFrom-Json
$report = @()
$report += "Taban: $($base.Taken) (açılış $($base.Boot))   Şimdi: $($now.Taken) (açılış $($now.Boot))"
$report += "Yeniden başlatıldı: $($base.Boot -ne $now.Boot)"
$report += "Güvenlik: Secure Boot $($base.SecureBoot)->$($now.SecureBoot), bellek bütünlüğü $($base.Hvci)->$($now.Hvci), test imzası $($base.TestSigning)->$($now.TestSigning)"
$report += "usbip.exe kurulu: $($now.UsbipExe)"
$report += 'usbip sürücüleri:'
if ($now.UsbipDrivers.Count -eq 0) { $report += '  (yok)' }
foreach ($d in $now.UsbipDrivers) { $report += "  $($d.Name): $($d.State), başlatma $($d.StartMode)" }
$report += 'USB 3.0 kök hub''ları:'
foreach ($h in $now.RootHubs) { $report += "  $($h.Id): $($h.Status), üst filtre [$($h.UpperFilters)]" }

$before = @{}
foreach ($d in $base.Devices) { $before[$d.Id] = $d }
$after = @{}
foreach ($d in $now.Devices) { $after[$d.Id] = $d }
$watched = 'USB', 'USBDevice', 'HIDClass', 'Keyboard', 'Mouse', 'Bluetooth', 'MEDIA', 'AudioEndpoint', 'Net', 'XboxComposite', 'XnaComposite'

$missing = $base.Devices | Where-Object { -not $after.ContainsKey($_.Id) -and $watched -contains $_.Class }
$added = $now.Devices | Where-Object { -not $before.ContainsKey($_.Id) }
$broken = $now.Devices | Where-Object { $_.Status -ne 'OK' }
$newlyBroken = $broken | Where-Object { $before.ContainsKey($_.Id) -and $before[$_.Id].Status -eq 'OK' }

$report += "Aygıt sayısı: $($base.Devices.Count) -> $($now.Devices.Count)"
$report += "Tabanda olup şimdi olmayan (izlenen sınıflar): $(@($missing).Count)"
foreach ($d in $missing) { $report += "  - [$($d.Class)] $($d.Name)  $($d.Id)" }
$report += "Yeni aygıtlar: $(@($added).Count)"
foreach ($d in $added) { $report += "  + [$($d.Class)] $($d.Name) ($($d.Status))  $($d.Id)" }
$report += "Tabanda sorunsuzken şimdi sorunlu: $(@($newlyBroken).Count)"
foreach ($d in $newlyBroken) { $report += "  ! [$($d.Class)] $($d.Name): $($d.Status), kod $($d.Problem)  $($d.Id)" }
$report += "Şu an sorunlu olan toplam aygıt: $(@($broken).Count)"
foreach ($d in $broken | Where-Object { -not ($newlyBroken.Id -contains $_.Id) }) { $report += "  . [$($d.Class)] $($d.Name): $($d.Status), kod $($d.Problem)" }

$report += 'Açılıştan beri hata kayıtları (System, CodeIntegrity):'
$events = @(Get-BootEvents ([datetime]::ParseExact($now.Boot, 'yyyy-MM-dd HH:mm:ss', $null)))
if ($events.Count -eq 0) { $report += '  (yok)' } else { $report += $events }

$report | Set-Content -Encoding utf8 (Join-Path $dir 'karsilastirma.txt')
$report
