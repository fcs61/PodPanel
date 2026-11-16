# Aşama 3: köprü servisini kurar ve başlatır. Yönetici olarak çalıştırılır.
# Sıra: dosya özetlerini denetle -> geri yükleme noktası -> Program Files'a kopyala -> servisi kur ve başlat ->
# Bluetooth'un sanal adaptör üzerinden geri geldiğini bekle. Geri gelmezse servisi kendisi kaldırır ve adaptörü
# Windows'un kendi sürücüsüne geri verir.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'servis'
$manifest = Join-Path $PSScriptRoot 'servis.sha256'
$dest = Join-Path $env:ProgramFiles 'PodPanel\Bridge'
$exe = Join-Path $dest 'WPods.Service.exe'
$serviceName = 'WPodsService'
$serviceLog = Join-Path $env:ProgramData 'WPods\logs\service.log'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'servis-kurulum.txt'
$waitSeconds = 150
# USB üzerindeki Bluetooth adaptörlerinin uyumluluk kimliği (sınıf E0, alt sınıf 01, protokol 01).
$BluetoothUsbClass = 'USB\Class_E0&SubClass_01&Prot_01'
New-Item -ItemType Directory -Force $dir | Out-Null
$result = @("Başlangıç: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$serviceTouched = $false

function Note([string]$text) {
    $script:result += $text
    Write-Host $text
}

function Finish([string]$message, [int]$code) {
    $script:result += $message
    $script:result += "Sonuç kodu: $code"
    if (Test-Path $serviceLog) {
        $script:result += '--- servis günlüğü (son 80 satır) ---'
        $script:result += Get-Content $serviceLog -Tail 80 -ErrorAction SilentlyContinue
    }
    $script:result | Set-Content -Encoding utf8 $resultPath
    Write-Host ''
    Write-Host $message -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Red' })
    Write-Host ''
    Read-Host 'Pencereyi kapatmak için Enter''a bas' | Out-Null
    exit $code
}

# Bir programı çalıştırır; çıktısını ve çıkış kodunu döndürür (hata akışı PowerShell hatasına dönüşmez).
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

# Klasördeki dosyaları özet listesiyle karşılaştırır; sorunları döndürür (boşsa her şey eşleşiyor).
function Test-Manifest([string]$folder) {
    $expected = @{}
    foreach ($line in Get-Content $manifest -Encoding utf8) {
        if ($line -match '^([0-9A-F]{64})\s+(.+)$') { $expected[$Matches[2]] = $Matches[1] }
    }
    $problems = @()
    $seen = @{}
    foreach ($file in Get-ChildItem $folder -Recurse -File) {
        $relative = $file.FullName.Substring($folder.Length + 1)
        $seen[$relative] = $true
        if (-not $expected.ContainsKey($relative)) { $problems += "listede olmayan dosya: $relative"; continue }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[$relative]) { $problems += "özeti farklı: $relative" }
    }
    foreach ($name in $expected.Keys) { if (-not $seen.ContainsKey($name)) { $problems += "eksik dosya: $name" } }
    if ($expected.Count -eq 0) { $problems += 'özet listesi boş' }
    $problems
}

# Intel Bluetooth adaptörünün gerçek ve sanal kopyaları: sınıf, durum ve bağlı sürücü.
function Get-AdapterNodes {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and ($_.CompatibleID -contains $BluetoothUsbClass) } | ForEach-Object {
        "[$($_.Class)] $($_.FriendlyName): $($_.Status), sürücü $($_.Service)  $($_.InstanceId)"
    }
}

# WinUSB'ye bağlı Bluetooth adaptörleri (servisin devraldığı gerçek adaptör).
function Get-OnWinUsb {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Service -eq 'WinUSB' -and ($_.CompatibleID -contains $BluetoothUsbClass) }
}

# Yönetici olmayan gruplara (Users, Authenticated Users, Everyone, Interactive) yazma hakkı veren erişim kuralları.
function Get-WritableByUsers([string]$path) {
    $writeBits = [int][Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $genericWriteOrAll = 0x50000000
    foreach ($rule in (Get-Acl $path).Access) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if ($sid -notin 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4') { continue }
        $rights = [int]$rule.FileSystemRights
        if (($rights -band $writeBits) -or ($rights -band $genericWriteOrAll)) { $rule }
    }
}

function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

function Remove-ServiceAndRestore {
    Note 'Geri alınıyor: servis kaldırılıyor, adaptör Windows''un kendi sürücüsüne veriliyor (bir dakika sürebilir)...'
    $undo = Invoke-Native $exe '--uninstall'
    Note "  --uninstall çıkış kodu: $($undo.Code) $($undo.Text)"
    if ($undo.Code -ne 0) {
        $native = Invoke-Native $exe '--restore-native'
        Note "  --restore-native çıkış kodu: $($native.Code) $($native.Text)"
    }
    for ($i = 0; $i -lt 20 -and -not (Test-BluetoothUp); $i++) { Start-Sleep -Seconds 3 }
    Note '  Adaptör durumu:'
    Get-AdapterNodes | ForEach-Object { Note "    $_" }
    $gone = -not (Get-Service $serviceName -ErrorAction SilentlyContinue)
    if ($gone -and (Test-Path $dest)) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
    (Test-BluetoothUp) -and $gone -and -not (Get-OnWinUsb)
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Yönetici onayı isteniyor; kurulum yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }

    # 1. Ön koşullar.
    if (-not (Test-Path 'C:\Program Files\USBip\usbip.exe')) { Finish 'usbip-win2 kurulu değil. Hiçbir şey değiştirilmedi.' 2 }
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Finish 'Servis zaten kurulu. Hiçbir şey değiştirilmedi.' 3 }
    if (-not (Test-BluetoothUp)) { Finish 'Bluetooth şu an çalışmıyor; önce bunun nedenine bakmak gerekir. Hiçbir şey değiştirilmedi.' 4 }
    $problems = @(Test-Manifest $source)
    if ($problems.Count -gt 0) {
        $problems | Select-Object -First 10 | ForEach-Object { Note "  $_" }
        Finish 'Servis dosyaları özet listesiyle eşleşmiyor. Hiçbir şey değiştirilmedi.' 5
    }
    Note 'Servis dosyaları doğrulandı.'
    Note 'Kurulumdan önce adaptör:'
    Get-AdapterNodes | ForEach-Object { Note "  $_" }

    # 2. Geri yükleme noktası. Windows 24 saatte bir noktayla sınırlar; sınır yalnızca bu nokta için kaldırılır.
    Write-Host 'Geri yükleme noktası oluşturuluyor (bir dakika kadar sürebilir)...'
    $restoreKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $frequency = (Get-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
    Set-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
    try {
        Checkpoint-Computer -Description 'PodPanel: servis oncesi' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
    } finally {
        if ($null -eq $frequency) {
            Remove-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue
        } else {
            Set-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -Value $frequency -Type DWord
        }
    }
    $point = Get-ComputerRestorePoint | Sort-Object SequenceNumber | Select-Object -Last 1
    $created = if ($point) { [Management.ManagementDateTimeConverter]::ToDateTime($point.CreationTime) } else { [datetime]::MinValue }
    if (-not $point -or ((Get-Date) - $created).TotalMinutes -gt 15) {
        Finish 'Yeni bir geri yükleme noktası oluşturulamadı. Hiçbir şey kurulmadı.' 6
    }
    Note "Geri yükleme noktası: #$($point.SequenceNumber) '$($point.Description)' $($created.ToString('yyyy-MM-dd HH:mm:ss'))"

    # 3. Dosyalar: yalnızca yöneticilerin yazabildiği bir klasöre.
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $source '*') $dest -Recurse -Force
    $problems = @(Test-Manifest $dest)
    if ($problems.Count -gt 0) {
        Remove-Item $dest -Recurse -Force
        Finish 'Kopyalanan dosyalar özet listesiyle eşleşmiyor. Kopya silindi, hiçbir şey kurulmadı.' 7
    }
    $writable = @(Get-WritableByUsers $dest)
    if ($writable.Count -gt 0) {
        Remove-Item $dest -Recurse -Force
        Finish 'Kurulum klasörü yönetici olmayan kullanıcılarca yazılabiliyor. Kopya silindi, hiçbir şey kurulmadı.' 8
    }
    Note "Dosyalar kopyalandı: $dest"

    # 4. Servis. Bu adımda Bluetooth birkaç saniyeliğine kesilir.
    Write-Host ''
    Write-Host 'Servis kuruluyor. Bluetooth birkaç saniyeliğine kesilecek; Bluetooth klavye ve kumanda kendiliğinden geri gelir.' -ForegroundColor Yellow
    $startText = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $serviceTouched = $true
    $install = Invoke-Native $exe '--install'
    Note "--install çıkış kodu: $($install.Code) $($install.Text)"

    # 5. Bluetooth sanal adaptör üzerinden geri geldi mi?
    $ok = $false
    for ($elapsed = 0; $elapsed -lt $waitSeconds; $elapsed += 3) {
        Start-Sleep -Seconds 3
        $service = Get-Service $serviceName -ErrorAction SilentlyContinue
        $status = Invoke-Native $exe '--status'
        $onWinUsb = $status.Text -match '\(WinUSB\)'
        $guard = $status.Text -match 'guard tripped: True'
        $running = (Test-Path $serviceLog) -and [bool](Get-Content $serviceLog -Tail 200 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d).*\[engine\] state Running' -and $Matches[1] -ge $startText })
        $up = Test-BluetoothUp
        Write-Host ("  {0,3} sn: servis {1}, adaptör WinUSB'de {2}, oturum çalışıyor {3}, Bluetooth hazır {4}" -f ($elapsed + 3), $service.Status, $onWinUsb, $running, $up)
        if ($guard) { Note 'Servisin kendi koruması devreye girdi (üç başarısız deneme).'; break }
        if ($service.Status -eq 'Running' -and $onWinUsb -and $running -and $up) { $ok = $true; break }
    }
    Note "Bekleme sonucu: $ok"
    Note 'Kurulumdan sonra adaptör:'
    Get-AdapterNodes | ForEach-Object { Note "  $_" }

    if (-not $ok) {
        $restored = Remove-ServiceAndRestore
        if ($restored) {
            Finish 'Servis Bluetooth''u devralamadı. Servis kaldırıldı ve Bluetooth eski hâline döndü. Sohbete dönüp haber ver.' 9
        }
        Finish 'Servis Bluetooth''u devralamadı ve geri alma tam doğrulanamadı. Bilgisayarı yeniden başlat; Bluetooth gelmezse sohbete dön.' 10
    }

    # Aygıtların yeniden bağlanması için kısa bir süre, sonra kayıt.
    Start-Sleep -Seconds 15
    Note 'Bluetooth aygıtları (kurulumdan 15 sn sonra):'
    Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^BTH(ENUM\\DEV_|LE\\DEV_)' } | ForEach-Object { Note "  $($_.FriendlyName): $($_.Status)" }
    Finish 'Servis çalışıyor ve Bluetooth sanal adaptör üzerinden geri geldi. Sohbete dönüp haber ver.' 0
} catch {
    $message = "Beklenmeyen hata: $($_.Exception.Message)"
    if ($serviceTouched) {
        Note $message
        $restored = $false
        try { $restored = Remove-ServiceAndRestore } catch { Note "Geri alma hatası: $($_.Exception.Message)" }
        Finish "Kurulum yarıda kaldı. Geri alma $(if ($restored) { 'tamamlandı' } else { 'doğrulanamadı; bilgisayarı yeniden başlatıp sohbete dön' })." 1
    }
    Finish "$message Servis kurulmadı." 1
}
