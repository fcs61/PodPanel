# PodPanel'i kaldırır. Kaldir.exe tarafından yönetici olarak, gizli bir PowerShell sürecinde çalıştırılır.
# Girdi ortam değişkenleriyle gelir:
#   PODPANEL_KAYNAK         kurulum klasörü (C:\Program Files\PodPanel); betikler onun "kurulum" alt klasöründedir
#   PODPANEL_MOD            "denetim": yalnızca neyin kurulu olduğunu bildirir; "kaldir": kaldırır
#   PODPANEL_USBIP_KALDIR   "1" ise usbip-win2 de kaldırılır
# Sıra: paneli kapat -> köprü servisini kaldır ve Bluetooth'un Windows'un kendi sürücüsüne dönmesini bekle ->
# (istenirse) usbip-win2'yi kaldır -> kayıtları ve dosyaları sil. Bluetooth geri gelmezse dosyalar silinmez.
# Kaldir.exe kendi dosyasını ve boş kalan klasörü iş bittikten sonra siler.
$Kaynak = $env:PODPANEL_KAYNAK
$Mod = $env:PODPANEL_MOD
$RemoveUsbip = $env:PODPANEL_USBIP_KALDIR -eq '1'
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $Kaynak 'kurulum\ortak.ps1'), [Text.Encoding]::UTF8)))
if ($Mod -eq 'denetim') { $script:LogEnabled = $false }

$Toplam = 4
$UsbipUninstaller = Join-Path $UsbipDir 'unins000.exe'

try {
    if ($Mod -eq 'denetim') {
        $installed = (Get-ItemProperty $StateKey -Name Version -ErrorAction SilentlyContinue).Version
        Info 'kurulu' $(if ($installed) { $installed } else { 'bilinmiyor' })
        Info 'usbip' $(if (Test-Path -LiteralPath $UsbipUninstaller) { 'kurulu' } else { 'yok' })
        Info 'usbipbizim' $(if ((Get-ItemProperty $StateKey -Name UsbipInstalledByUs -ErrorAction SilentlyContinue).UsbipInstalledByUs -eq 1) { '1' } else { '0' })
        Finish 0 $false 'denetim'
    }
    if (-not (Test-Admin)) {
        $script:LogEnabled = $false
        Finish 2 $false 'Kaldırma yönetici olarak çalıştırılmalı.'
    }
    Note 'PodPanel kaldırılıyor.'

    # 1. Panel. Kendi "çık" komutuyla kapanırsa mikrofon ve gecikme ayarlarını geri alır.
    Step 1 $Toplam 'Panel kapatılıyor'
    if (Stop-Panel) {
        Note 'Panel zorla kapatıldı. AirPods mikrofonu Windows''ta görünmezse: Denetim Masası > Aygıtlar ve Yazıcılar > AirPods > Özellikler > Hizmetler sekmesinde "El-serbest Telefon" kutusunu işaretle.'
    }

    # 2. Köprü servisi.
    Step 2 $Toplam 'Köprü servisi kaldırılıyor'
    $service = Get-BridgeService
    if ($service -and -not (Test-OurService $service)) {
        Note 'Kurulu servis bu kuruluma ait değil (WPods uygulaması); ona dokunulmadı.'
    } elseif ($service -or (Test-Path -LiteralPath $ServiceExe) -or (Get-OnWinUsb)) {
        Note 'Bluetooth birkaç saniyeliğine kesilecek (bir dakika sürebilir)...'
        if (-not (Remove-Bridge)) {
            Finish 3 $true 'Servisin kaldırıldığı ve Bluetooth''un geri geldiği doğrulanamadı; dosyalar silinmedi. Bilgisayarı yeniden başlatıp kaldırmayı yeniden çalıştır. Bluetooth gelmezse Sistem Geri Yükleme ile "PodPanel kurulumu oncesi" noktasına dön.'
        }
        Note 'Servis kaldırıldı ve Bluetooth Windows''un kendi sürücüsüne döndü.'
    } else {
        Note 'Köprü servisi kurulu değil.'
    }

    # 3. usbip-win2.
    Step 3 $Toplam 'usbip-win2 sürücüsü'
    $restart = $false
    if (-not $RemoveUsbip) {
        Note 'usbip-win2 bırakıldı (Uygulamalar listesinden "USBip" olarak kaldırılabilir).'
    } elseif (-not (Test-Path -LiteralPath $UsbipUninstaller)) {
        Note 'usbip-win2 kurulu değil.'
    } else {
        Note 'usbip-win2 kaldırılıyor. USB aygıtların bir an kopabilir.'
        $uninstallLog = Join-Path $LogDir 'usbip-kaldirma.log'
        $process = Start-Process -FilePath $UsbipUninstaller -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $uninstallLog + '"') -PassThru -Wait
        Note "usbip-win2 kaldırıcısı çıkış kodu: $($process.ExitCode)"
        # Kaldırıcı işi geçici bir kopyasına devreder; dosyalar gidene kadar beklenir.
        for ($i = 0; $i -lt 40 -and (Test-Path -LiteralPath $UsbipExe); $i++) { Start-Sleep -Seconds 3 }
        if (Test-Path -LiteralPath $UsbipExe) {
            Note "usbip-win2 kaldırılamadı; Uygulamalar listesinden elle kaldırılabilir. Ayrıntı: $uninstallLog"
        } else {
            Note 'usbip-win2 kaldırıldı.'
            $restart = $true
        }
    }

    # 4. Kayıtlar ve dosyalar.
    Step 4 $Toplam 'Dosyalar siliniyor'
    if (Test-Path -LiteralPath $Shortcut) { Remove-Item -LiteralPath $Shortcut -Force -ErrorAction SilentlyContinue }
    Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $StateKey -Recurse -Force -ErrorAction SilentlyContinue
    # "Windows ile başlat" kaydı: yalnızca bu kurulumu gösteriyorsa silinir (kaldırmayı çalıştıran kullanıcı için).
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $run = (Get-ItemProperty $runKey -Name PodPanel -ErrorAction SilentlyContinue).PodPanel
    if ($run -and "$run".Trim('"').StartsWith($Hedef + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-ItemProperty $runKey -Name PodPanel -ErrorAction SilentlyContinue
    }
    $left = @()
    foreach ($name in 'App', 'Bridge', 'Bridge.onceki', 'kurulum', '_kurulum') {
        $path = Join-Path $Hedef $name
        if ((Test-Path -LiteralPath $path) -and -not (Remove-Folder $path)) { $left += $name }
    }
    foreach ($name in 'LISANS.txt', 'UCUNCU-TARAFLAR.txt', 'kaynak.zip', 'surum.txt') {
        Remove-Item -LiteralPath (Join-Path $Hedef $name) -Force -ErrorAction SilentlyContinue
    }
    # Servisin günlük klasörü (servis bu kuruluma aitti).
    if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) { Remove-Folder (Join-Path $env:ProgramData 'WPods') | Out-Null }
    if ($left.Count -gt 0) { Note "Silinemeyen klasörler (yeniden başlatmadan sonra elle silinebilir): $($left -join ', ')" }
    Note 'Kayıtlar ve dosyalar silindi. Kişisel ayarlar (%LOCALAPPDATA%\PodPanel) yerinde bırakıldı.'

    if ($restart) { Finish 0 $true 'PodPanel kaldırıldı. usbip-win2 sürücüsünün tamamen kalkması için bilgisayarı yeniden başlat.' }
    Finish 0 $false 'PodPanel kaldırıldı.'
} catch {
    Note "Beklenmeyen hata: $($_.Exception.Message)"
    Note "$($_.ScriptStackTrace)"
    Finish 1 $false "Kaldırma yarıda kaldı: $($_.Exception.Message) Bluetooth çalışmıyorsa bilgisayarı yeniden başlat ve kaldırmayı yeniden çalıştır."
}
