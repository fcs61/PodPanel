# kur.ps1 ve kaldir.ps1 betiklerinin ortak işlevleri. Kurulum programı (Kurulum.exe / Kaldir.exe) bu betikleri gizli bir
# PowerShell sürecinde çalıştırır ve standart çıktıyı okur:
#   @@ADIM <n> <toplam> <metin>      n. adım başladı
#   @@BILGI <anahtar> <değer>        denetim sonucu (yalnızca "denetim" modunda)
#   @@SON <kod> <yeniden başlat 0|1> <metin>   iş bitti; kod 0 ise başarılı
# Öteki satırlar günlük metnidir. Her satır ayrıca %ProgramData%\PodPanel\kurulum.log dosyasına yazılır.

$ErrorActionPreference = 'Stop'
$Hedef = Join-Path $env:ProgramFiles 'PodPanel'
$AppDir = Join-Path $Hedef 'App'
$BridgeDir = Join-Path $Hedef 'Bridge'
$BridgeBackup = Join-Path $Hedef 'Bridge.onceki'
$ServiceExe = Join-Path $BridgeDir 'WPods.Service.exe'
$ServiceName = 'WPodsService'
$ServiceLog = Join-Path $env:ProgramData 'WPods\logs\service.log'
$UsbipDir = Join-Path $env:ProgramFiles 'USBip'
$UsbipExe = Join-Path $UsbipDir 'usbip.exe'
$StateKey = 'HKLM:\SOFTWARE\PodPanel'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PodPanel'
$Shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PodPanel.lnk'
$LogDir = Join-Path $env:ProgramData 'PodPanel'
$LogPath = Join-Path $LogDir 'kurulum.log'
# USB üzerindeki Bluetooth adaptörlerinin uyumluluk kimliği (sınıf E0, alt sınıf 01, protokol 01).
$BluetoothUsbClass = 'USB\Class_E0&SubClass_01&Prot_01'

# Çıktı her zaman UTF-8: konsol olmadan (gizli süreçte) çalışırken de aynı kalsın diye ham akışa yazılır.
$script:Out = New-Object IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object Text.UTF8Encoding $false))
$script:Out.AutoFlush = $true

# Denetim modunda dosyaya günlük yazılmaz (hiçbir şeyi değiştirmemesi gerekir).
$script:LogEnabled = $true

function Write-Line([string]$text) {
    $script:Out.WriteLine($text)
    if (-not $script:LogEnabled) { return }
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) {
            New-Item -ItemType Directory -Force $LogDir | Out-Null
            # Günlük klasörüne yalnızca yöneticiler ve sistem yazabilsin; kullanıcılar okuyabilsin.
            & "$env:SystemRoot\System32\icacls.exe" $LogDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $text" -Encoding UTF8
    } catch {
        # Günlük yazılamıyorsa kurulum bundan etkilenmesin.
    }
}

function Note([string]$text) { Write-Line $text }

function Step([int]$number, [int]$total, [string]$text) { Write-Line "@@ADIM $number $total $text" }

function Info([string]$key, [string]$value) { Write-Line "@@BILGI $key $value" }

# İşi bitirir: son satırı yazar ve süreçten çıkar.
function Finish([int]$code, [bool]$restart, [string]$message) {
    Write-Line "@@SON $code $([int]$restart) $message"
    exit $code
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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

# Özet listesini okur: "ÖZET  göreli\yol" satırları -> yol => özet.
function Read-Manifest([string]$path) {
    $expected = @{}
    foreach ($line in [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)) {
        if ($line -match '^([0-9A-Fa-f]{64})\s+(.+)$') { $expected[$Matches[2]] = $Matches[1].ToUpperInvariant() }
    }
    $expected
}

# Klasördeki dosyaları özet listesinin $prefix ile başlayan girdileriyle karşılaştırır; sorunları döndürür
# (boşsa her şey eşleşiyor). $prefix boşsa listenin tamamı kullanılır; $skip listedeki adlar yok sayılır.
function Test-Manifest([hashtable]$expected, [string]$folder, [string]$prefix, [string[]]$skip = @()) {
    $problems = @()
    $seen = @{}
    $root = $folder.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $prefix + $file.FullName.Substring($root.Length + 1)
        if ($skip -contains $relative) { continue }
        $seen[$relative] = $true
        if (-not $expected.ContainsKey($relative)) { $problems += "listede olmayan dosya: $relative"; continue }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[$relative]) { $problems += "özeti farklı: $relative" }
    }
    $count = 0
    foreach ($name in $expected.Keys) {
        if ($prefix -and -not $name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $count++
        if (-not $seen.ContainsKey($name)) { $problems += "eksik dosya: $name" }
    }
    if ($count -eq 0) { $problems += 'özet listesinde bu klasör için girdi yok' }
    $problems
}

# USB üzerinden bağlı Bluetooth adaptörleri (gerçek ve servis kuruluysa sanal kopyası).
function Get-UsbBluetooth {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and ($_.CompatibleID -contains $BluetoothUsbClass) }
}

function Get-AdapterNodes {
    Get-UsbBluetooth | ForEach-Object { "[$($_.Class)] $($_.FriendlyName): $($_.Status), sürücü $($_.Service)  $($_.InstanceId)" }
}

# WinUSB'ye bağlı Bluetooth adaptörleri (servisin devraldığı gerçek adaptör).
function Get-OnWinUsb {
    Get-UsbBluetooth | Where-Object { $_.Service -eq 'WinUSB' }
}

# Windows'un Bluetooth yığını çalışıyor: USB adaptör ve Bluetooth numaralandırıcısı hazır.
function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

# Yönetici olmayan gruplara (Users, Authenticated Users, Everyone, Interactive) yazma hakkı veren erişim kuralları.
function Get-WritableByUsers([string]$path) {
    $writeBits = [int][Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $genericWriteOrAll = 0x50000000
    foreach ($rule in (Get-Acl -LiteralPath $path).Access) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if ($sid -notin 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4') { continue }
        $rights = [int]$rule.FileSystemRights
        if (($rights -band $writeBits) -or ($rights -band $genericWriteOrAll)) { $rule }
    }
}

# Kurulu servisin kaydı; yoksa $null.
function Get-BridgeService {
    Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
}

# Servis kaydı bu kurulumun klasörünü mü gösteriyor? (Aynı adı kullanan WPods uygulaması başka klasöre kurulur.)
function Test-OurService($service) {
    if (-not $service) { return $false }
    $path = "$($service.PathName)".Trim('"')
    $path.StartsWith($BridgeDir + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ProductVersion([string]$path) {
    if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path).VersionInfo.ProductVersion } else { '' }
}

# Çalışan paneli kapatır: önce kendi "çık" komutuyla (ayarlarını geri alarak kapanır), olmazsa zorla.
# Zorla kapatmak gerektiyse $true döndürür.
function Stop-Panel {
    $running = @(Get-Process -Name PodPanel -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return $false }
    $exe = $running | ForEach-Object { try { $_.Path } catch { $null } } | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if ($exe) {
        Note "Çalışan panel kapatılıyor: $exe"
        try { Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden } catch { Note "  çık komutu gönderilemedi: $($_.Exception.Message)" }
        for ($i = 0; $i -lt 20 -and (Get-Process -Name PodPanel -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }
    $left = @(Get-Process -Name PodPanel -ErrorAction SilentlyContinue)
    if ($left.Count -eq 0) { return $false }
    Note 'Panel kendiliğinden kapanmadı; zorla kapatılıyor.'
    $left | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $true
}

# Servisi kurar ya da başlatır ve Bluetooth'un sanal adaptör üzerinden geri gelmesini bekler.
function Start-BridgeAndWait([int]$waitSeconds) {
    $startText = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $install = Invoke-Native $ServiceExe '--install'
    Note "--install çıkış kodu: $($install.Code) $($install.Text)"
    for ($elapsed = 0; $elapsed -lt $waitSeconds; $elapsed += 3) {
        Start-Sleep -Seconds 3
        $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
        $status = Invoke-Native $ServiceExe '--status'
        $onWinUsb = $status.Text -match '\(WinUSB\)'
        $running = (Test-Path $ServiceLog) -and [bool](Get-Content $ServiceLog -Tail 200 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d).*\[engine\] state Running' -and $Matches[1] -ge $startText })
        $up = Test-BluetoothUp
        Note ("  {0,3} sn: servis {1}, adaptör WinUSB'de {2}, oturum çalışıyor {3}, Bluetooth hazır {4}" -f ($elapsed + 3), $service.Status, $onWinUsb, $running, $up)
        if ($status.Text -match 'guard tripped: True') { Note 'Servisin kendi koruması devreye girdi (üç başarısız deneme).'; return $false }
        if ($service.Status -eq 'Running' -and $onWinUsb -and $running -and $up) { return $true }
    }
    $false
}

# Servisi kaldırır ve adaptörü Windows'un kendi sürücüsüne geri verir. Bluetooth geri geldiyse $true döndürür.
function Remove-Bridge {
    $stored = (Get-ItemProperty 'HKLM:\SOFTWARE\WPods\Service' -Name RadioInstanceId -ErrorAction SilentlyContinue).RadioInstanceId
    Note "Kayıtlı adaptör: $stored"
    if (Test-Path -LiteralPath $ServiceExe) {
        $undo = Invoke-Native $ServiceExe '--uninstall'
        Note "--uninstall çıkış kodu: $($undo.Code) $($undo.Text)"
        if ($undo.Code -ne 0) {
            $native = Invoke-Native $ServiceExe '--restore-native'
            Note "--restore-native çıkış kodu: $($native.Code) $($native.Text)"
        }
    } elseif (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Note 'Servis programı bulunamadı; servis kaydı siliniyor.'
        & "$env:SystemRoot\System32\sc.exe" stop $ServiceName | Out-Null
        Start-Sleep -Seconds 5
        & "$env:SystemRoot\System32\sc.exe" delete $ServiceName | Out-Null
    }
    for ($i = 0; $i -lt 20 -and (-not (Test-BluetoothUp) -or (Get-OnWinUsb)); $i++) { Start-Sleep -Seconds 3 }

    # Adaptör hâlâ WinUSB'deyse: aygıt düğümünü kaldırıp yeniden taratmak Windows'a kendi sürücüsünü kurdurur.
    foreach ($node in @(Get-OnWinUsb)) {
        Note "Adaptör hâlâ WinUSB'de, elle geri veriliyor: $($node.InstanceId)"
        & "$env:SystemRoot\System32\pnputil.exe" /remove-device "$($node.InstanceId)" | ForEach-Object { Note "  $_" }
        & "$env:SystemRoot\System32\pnputil.exe" /scan-devices | ForEach-Object { Note "  $_" }
        for ($i = 0; $i -lt 20 -and -not (Test-BluetoothUp); $i++) { Start-Sleep -Seconds 3 }
    }
    $gone = -not (Get-Service $ServiceName -ErrorAction SilentlyContinue)
    Note "Servis kaldırıldı: $gone   Bluetooth hazır: $(Test-BluetoothUp)"
    Get-AdapterNodes | ForEach-Object { Note "  $_" }
    $gone -and (Test-BluetoothUp) -and -not (Get-OnWinUsb)
}

# Klasörü siler; kilitli dosya kalırsa birkaç kez yeniden dener. Silindiyse $true.
function Remove-Folder([string]$path) {
    for ($i = 0; $i -lt 5 -and (Test-Path -LiteralPath $path); $i++) {
        try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 }
    }
    -not (Test-Path -LiteralPath $path)
}
