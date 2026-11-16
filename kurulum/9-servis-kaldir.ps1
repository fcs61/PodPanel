# Köprü servisini kaldırır ve Bluetooth adaptörünü Windows'un kendi sürücüsüne geri verir. Yönetici olarak çalıştırılır.
# usbip-win2'ye dokunmaz (o, Ayarlar > Uygulamalar > USBip üzerinden kaldırılır).
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:ProgramFiles 'PodPanel\Bridge'
$exe = Join-Path $dest 'WPods.Service.exe'
$serviceName = 'WPodsService'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'servis-kaldirma.txt'
New-Item -ItemType Directory -Force $dir | Out-Null
$result = @("Başlangıç: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$BluetoothUsbClass = 'USB\Class_E0&SubClass_01&Prot_01'

function Note([string]$text) {
    $script:result += $text
    Write-Host $text
}

function Finish([string]$message, [int]$code) {
    $script:result += $message
    $script:result += "Sonuç kodu: $code"
    $script:result | Set-Content -Encoding utf8 $resultPath
    Write-Host ''
    Write-Host $message -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Red' })
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

# WinUSB'ye bağlı Bluetooth adaptörleri (uyumluluk kimliği: sınıf E0, alt sınıf 01, protokol 01).
function Get-OnWinUsb {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Service -eq 'WinUSB' -and ($_.CompatibleID -contains $BluetoothUsbClass) }
}

function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Yönetici onayı isteniyor; kaldırma yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }

    # Servis kaldırılınca bu kayıt silinir; elle geri verme gerekirse adaptörün kimliği buradan alınır.
    $stored = (Get-ItemProperty 'HKLM:\SOFTWARE\WPods\Service' -Name RadioInstanceId -ErrorAction SilentlyContinue).RadioInstanceId
    Note "Kayıtlı adaptör: $stored"

    if (Test-Path $exe) {
        Write-Host 'Servis kaldırılıyor; Bluetooth birkaç saniyeliğine kesilecek (bir dakika sürebilir)...' -ForegroundColor Yellow
        $undo = Invoke-Native $exe '--uninstall'
        Note "--uninstall çıkış kodu: $($undo.Code) $($undo.Text)"
        if ($undo.Code -ne 0) {
            $native = Invoke-Native $exe '--restore-native'
            Note "--restore-native çıkış kodu: $($native.Code) $($native.Text)"
        }
    } else {
        Note 'Servis programı bulunamadı; adaptör elle geri verilecek.'
        if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
            & "$env:SystemRoot\System32\sc.exe" stop $serviceName | Out-Null
            Start-Sleep -Seconds 5
            & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null
        }
    }

    for ($i = 0; $i -lt 20 -and (-not (Test-BluetoothUp) -or (Get-OnWinUsb)); $i++) { Start-Sleep -Seconds 3 }

    # Adaptör hâlâ WinUSB'deyse: aygıt düğümünü kaldırıp yeniden taratmak Windows'a en uygun sürücüyü (Intel) kurdurur.
    foreach ($node in @(Get-OnWinUsb)) {
        Note "Adaptör hâlâ WinUSB'de, elle geri veriliyor: $($node.InstanceId)"
        & "$env:SystemRoot\System32\pnputil.exe" /remove-device "$($node.InstanceId)" | ForEach-Object { Note "  $_" }
        & "$env:SystemRoot\System32\pnputil.exe" /scan-devices | ForEach-Object { Note "  $_" }
        for ($i = 0; $i -lt 20 -and -not (Test-BluetoothUp); $i++) { Start-Sleep -Seconds 3 }
    }

    $serviceGone = -not (Get-Service $serviceName -ErrorAction SilentlyContinue)
    if ($serviceGone -and (Test-Path $dest)) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
    Note "Servis kaldırıldı: $serviceGone"
    Note "Bluetooth hazır: $(Test-BluetoothUp)"
    Note 'Adaptör:'
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and ($_.CompatibleID -contains $BluetoothUsbClass) } | ForEach-Object {
        Note "  [$($_.Class)] $($_.FriendlyName): $($_.Status), sürücü $($_.Service)  $($_.InstanceId)"
    }

    if ($serviceGone -and (Test-BluetoothUp) -and -not (Get-OnWinUsb)) {
        Finish 'Servis kaldırıldı ve Bluetooth Windows''un kendi sürücüsüne döndü.' 0
    }
    Finish 'Kaldırma tam doğrulanamadı. Bilgisayarı yeniden başlat; Bluetooth gelmezse Sistem Geri Yükleme ile "PodPanel: servis oncesi" noktasına dön.' 2
} catch {
    Finish "Beklenmeyen hata: $($_.Exception.Message)" 1
}
