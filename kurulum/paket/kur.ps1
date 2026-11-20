# PodPanel kurulumu. Kurulum.exe tarafından yönetici olarak, gizli bir PowerShell sürecinde çalıştırılır.
# Girdi ortam değişkenleriyle gelir:
#   PODPANEL_KAYNAK        kurulum dosyalarının açıldığı klasör (yalnızca yöneticilerin yazabildiği bir yer)
#   PODPANEL_MOD           "denetim": hiçbir şeyi değiştirmeden ön koşullara bakar; "kur": kurar
#   PODPANEL_GERIYUKLEME   "0" ise geri yükleme noktası oluşturulmaz
# Sıra: denetimler -> geri yükleme noktası -> panel dosyaları ve kayıt -> usbip-win2 -> köprü servisi.
# Panel önce kurulur ve Uygulamalar listesine eklenir: sonraki adımlardan biri başarısız olursa panel köprüsüz
# çalışmaya devam eder ve her şey Uygulamalar listesinden kaldırılabilir (sonuç kodu 100: "kısmen kuruldu").
# usbip-win2 bu çalıştırmada kurulduysa servis başlatılmadan kaydedilir ve yeniden başlatmada devreye girer
# (servisin kendi kurulum akışı). usbip-win2 zaten çalışıyorsa servis hemen başlatılır; Bluetooth geri gelmezse
# servis kaldırılır ve adaptör Windows'un kendi sürücüsüne geri verilir.
$Kaynak = $env:PODPANEL_KAYNAK
$Mod = $env:PODPANEL_MOD
$RestorePoint = $env:PODPANEL_GERIYUKLEME -ne '0'
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $Kaynak 'kurulum\ortak.ps1'), [Text.Encoding]::UTF8)))
if ($Mod -eq 'denetim') { $script:LogEnabled = $false }

$Toplam = 5
$Kismi = 100   # panel kuruldu, köprü kurulamadı
$KismiEk = 'Panel kuruldu ve köprüsüz çalışır: pil, bağlantı, oyun modu ve uzamsal ses kullanılabilir; dinleme modu, gecikme ve mikrofon köprüsü kullanılamaz.'
$UsbipSetupName = 'USBip-0.9.8.1-x64.exe'
$UsbipVersion = '0.9.8.1'
$UsbipSha256 = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
$UsbipSigner = '9AC56B6C76141395D74FFF6652818376E80B9C95'   # Cloudyne Systems (Scheibling Consulting AB), GlobalSign EV
$serviceTouched = $false
$panelInstalled = $false
$script:Blockers = @()
# Köprünün kurulmamasının nedeni (boşsa kurulur): Uygulama Denetimi açık ya da Windows servis programını çalıştırmadı.
$script:BridgeBlocked = ''
$script:LaunchProblem = ''
$DenetimYolu = 'Köprüyü kurmak için Akıllı Uygulama Denetimi kapalı olmalıdır (Windows Güvenliği > Uygulama ve tarayıcı denetimi > Akıllı Uygulama Denetimi). Kapatmak bilgisayarın korumasını azaltır; karar senin. Kapatırsan kurulumu yeniden çalıştırman yeter.'

# surum.txt: 1. satır paket sürümü, 2. satır köprü servisinin sürümü.
$versionLines = @([IO.File]::ReadAllLines((Join-Path $Kaynak 'surum.txt'), [Text.Encoding]::UTF8))
$Surum = $versionLines[0].Trim()
$BridgeNew = $versionLines[1].Trim()
# Köprüsüz paket: köprü servisi ve usbip-win2 içermez; yalnızca panel kurulur, Bluetooth'a dokunulmaz.
$Koprusuz = $versionLines.Count -ge 3 -and $versionLines[2].Trim() -eq 'koprusuz'

# Ön koşullar. Engelleri $script:Blockers içine koyar, bulguları @@BILGI satırlarıyla yazar; hiçbir şeyi değiştirmez.
function Find-Problems {
    $blockers = @()
    $warnings = @()
    $os = Get-CimInstance Win32_OperatingSystem
    $build = [int]$os.BuildNumber
    Info 'windows' "$($os.Caption) ($build)"
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { $blockers += 'Bu sürüm yalnızca 64 bit (x64) Windows içindir.' }
    if ($build -lt 19041) { $blockers += 'Windows 10 sürüm 2004 ya da daha yenisi gerekir.' }
    elseif ($build -lt 22000) { $warnings += 'Yalnızca Windows 11''de denendi.' }

    $service = Get-BridgeService
    $ours = Test-OurService $service
    if ($Koprusuz) {
        Info 'surumturu' 'koprusuz'
        $installed = (Get-ItemProperty $StateKey -Name Version -ErrorAction SilentlyContinue).Version
        Info 'kurulu' $(if ($installed) { $installed } elseif ($ours) { 'yalnızca köprü servisi' } else { 'yok' })
        if ($service) {
            Info 'servis' 'kurulu (kaldırılmalı)'
            $blockers += 'Bu bilgisayarda köprü servisi kurulu. Önce Ayarlar > Uygulamalar > PodPanel > Kaldır ile kaldır ("usbip-win2 sürücüsünü de kaldır" kutusu işaretli), sonra bu kurulumu yeniden çalıştır.'
        }
        Info 'geriyukleme' 'gerekmez'
        foreach ($w in $warnings) { Info 'uyari' $w }
        foreach ($b in $blockers) { Info 'engel' $b }
        $script:BridgeBlocked = 'köprüsüz sürüm'
        $script:Blockers = $blockers
        return
    }
    if ($service -and -not $ours) {
        $blockers += 'Bu bilgisayarda WPods uygulamasının servisi kurulu. İkisi aynı anda çalışamaz; önce WPods''u kaldır.'
    }
    $adapters = @(Get-UsbBluetooth)
    if ($adapters.Count -eq 0) {
        Info 'bluetooth' 'bulunamadı'
        $blockers += 'USB üzerinden bağlı bir Bluetooth adaptörü bulunamadı. Köprü yalnızca USB adaptörlerle çalışır.'
    } else {
        $name = ($adapters | Where-Object { $_.Class -eq 'Bluetooth' } | Select-Object -First 1).FriendlyName
        if (-not $name) { $name = $adapters[0].FriendlyName }
        Info 'bluetooth' $name
        if (-not $service -and -not (Test-BluetoothUp)) { $blockers += 'Bluetooth şu an çalışmıyor. Önce Bluetooth''un çalıştığından emin ol.' }
    }

    # Windows'un Akıllı Uygulama Denetimi açıksa imzasız köprü servisi çalıştırılmaz (ya da her an engellenebilir);
    # o durumda sürücüye ve Bluetooth'a hiç dokunulmaz, yalnızca panel kurulur.
    $control = Get-AppControlState
    Info 'denetim' $control
    $script:BridgeBlocked = ''
    if ($control -eq 'açık') {
        $script:BridgeBlocked = 'Akıllı Uygulama Denetimi açık: Windows imzasız köprü servisini çalıştırmıyor.'
        $warnings += 'Akıllı Uygulama Denetimi açık: Windows imzasız köprü servisini çalıştırmıyor. Köprü kurulmayacak; panel köprüsüz kurulur.'
    } elseif ($control -eq 'değerlendirme') {
        $warnings += 'Akıllı Uygulama Denetimi değerlendirme modunda: kendiliğinden açılırsa köprü servisini engelleyebilir.'
    }

    $bridgeOld = if ($ours) { Get-ProductVersion (Join-Path $BridgeDir 'WPods.Service.dll') } else { '' }
    $installed = (Get-ItemProperty $StateKey -Name Version -ErrorAction SilentlyContinue).Version
    if ($installed) { Info 'kurulu' $installed }
    elseif ($ours) { Info 'kurulu' 'yalnızca köprü servisi' }
    else { Info 'kurulu' 'yok' }
    Info 'servis' $(if ($script:BridgeBlocked) { if ($ours) { 'değiştirilmeyecek' } else { 'kurulmayacak' } } elseif (-not $ours) { 'kurulacak' } elseif ($bridgeOld -eq $BridgeNew) { 'güncel' } else { 'güncellenecek' })

    if (Test-Path -LiteralPath $UsbipExe) {
        $version = "$((Get-Item -LiteralPath $UsbipExe).VersionInfo.FileVersion)".Trim()
        Info 'usbip' "kurulu ($version)"
        if ($version -ne $UsbipVersion) { $warnings += "usbip-win2 $version kurulu; köprü $UsbipVersion ile denendi." }
    } else {
        Info 'usbip' $(if ($script:BridgeBlocked) { 'kurulmayacak' } else { 'kurulacak' })
    }
    # Geri yükleme noktası yalnızca sürücü ya da servis değişecekse gerekir. Sistem Koruması kapalıysa oluşturulamaz;
    # pencere bunu kurulumdan önce söyler ve noktasız kurmak için onay ister.
    $needsRestore = (-not $script:BridgeBlocked) -and ((-not (Test-Path -LiteralPath $UsbipExe)) -or (-not $ours) -or ($bridgeOld -ne $BridgeNew))
    if (-not $needsRestore) {
        Info 'geriyukleme' 'gerekmez'
    } else {
        $interval = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -Name RPSessionInterval -ErrorAction SilentlyContinue).RPSessionInterval
        if ($interval -eq 0) {
            Info 'geriyukleme' 'kapalı'
            $warnings += 'Sistem Koruması kapalı: geri yükleme noktası oluşturulamaz. Açmak için Başlat''a "Geri yükleme noktası oluştur" yaz, C: sürücüsünü seç, Yapılandır > Sistem korumasını aç.'
        } else {
            Info 'geriyukleme' 'gerekir'
        }
    }
    foreach ($w in $warnings) { Info 'uyari' $w }
    foreach ($b in $blockers) { Info 'engel' $b }
    $script:Blockers = $blockers
}

function New-RestorePoint {
    # Windows 24 saatte bir noktayla sınırlar; sınır yalnızca bu nokta için kaldırılır.
    $restoreKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $frequency = (Get-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
    Set-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
    try {
        Checkpoint-Computer -Description 'PodPanel kurulumu oncesi' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
    } catch {
        Note "Geri yükleme noktası hatası: $($_.Exception.Message)"
    } finally {
        if ($null -eq $frequency) {
            Remove-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue
        } else {
            Set-ItemProperty $restoreKey -Name SystemRestorePointCreationFrequency -Value $frequency -Type DWord
        }
    }
    $point = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber | Select-Object -Last 1
    $created = if ($point) { [Management.ManagementDateTimeConverter]::ToDateTime($point.CreationTime) } else { [datetime]::MinValue }
    if (-not $point -or ((Get-Date) - $created).TotalMinutes -gt 15) { return $false }
    Note "Geri yükleme noktası: #$($point.SequenceNumber) '$($point.Description)' $($created.ToString('yyyy-MM-dd HH:mm:ss'))"
    $true
}

# Uygulamalar listesi, Başlat menüsü ve kurulum kaydı. Kurulu boyut her çağrıda yeniden hesaplanır.
function Register-Install {
    New-Item -Path $StateKey -Force | Out-Null
    Set-ItemProperty $StateKey -Name Version -Value $Surum
    Set-ItemProperty $StateKey -Name InstallDir -Value $Hedef
    $sizeKb = [int]((Get-ChildItem -LiteralPath $Hedef -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike "$Kaynak\*" } | Measure-Object Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKey -Force | Out-Null
    Set-ItemProperty $UninstallKey -Name DisplayName -Value 'PodPanel'
    Set-ItemProperty $UninstallKey -Name DisplayVersion -Value $Surum
    Set-ItemProperty $UninstallKey -Name Publisher -Value 'PodPanel'
    Set-ItemProperty $UninstallKey -Name InstallLocation -Value $Hedef
    Set-ItemProperty $UninstallKey -Name DisplayIcon -Value (Join-Path $AppDir 'PodPanel.exe')
    Set-ItemProperty $UninstallKey -Name UninstallString -Value ('"' + (Join-Path $Hedef 'Kaldir.exe') + '"')
    Set-ItemProperty $UninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $UninstallKey -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty $UninstallKey -Name EstimatedSize -Value $sizeKb -Type DWord
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Shortcut)
    $link.TargetPath = Join-Path $AppDir 'PodPanel.exe'
    $link.WorkingDirectory = $AppDir
    $link.Description = 'AirPods paneli'
    $link.Save()
}

# Yeni servis dosyalarını yerine koyar ve doğrular. Sorun varsa metnini, yoksa boş döndürür.
function Install-BridgeFiles([hashtable]$expected) {
    if ((Test-Path -LiteralPath $BridgeDir) -and -not (Remove-Folder $BridgeDir)) { return 'Eski servis klasörü silinemedi.' }
    Move-Item -LiteralPath (Join-Path $Kaynak 'bridge') -Destination $BridgeDir
    $problems = @(Test-Manifest $expected $BridgeDir 'bridge\')
    if ($problems.Count -gt 0) {
        $problems | Select-Object -First 10 | ForEach-Object { Note "  $_" }
        return 'Kopyalanan servis dosyaları özet listesiyle eşleşmiyor.'
    }
    if (@(Get-WritableByUsers $BridgeDir).Count -gt 0) { return 'Servis klasörü yönetici olmayan kullanıcılarca yazılabiliyor.' }
    ''
}

try {
    if ($Mod -eq 'denetim') {
        Find-Problems
        Finish 0 $false "$($script:Blockers.Count) engel"
    }
    if (-not (Test-Admin)) {
        # Yönetici değilken günlük klasörü de açılmaz: hiçbir şeye dokunmadan durur.
        $script:LogEnabled = $false
        Finish 2 $false 'Kurulum yönetici olarak çalıştırılmalı.'
    }
    Note "PodPanel $Surum kurulumu başladı (köprü servisi $BridgeNew)."

    # 1. Denetimler.
    Step 1 $Toplam 'Denetimler'
    Find-Problems
    if ($script:Blockers.Count -gt 0) { Finish 2 $false $script:Blockers[0] }
    $expected = Read-Manifest (Join-Path $Kaynak 'dosyalar.sha256')
    $problems = @(Test-Manifest $expected $Kaynak '' @('dosyalar.sha256'))
    if ($problems.Count -gt 0) {
        $problems | Select-Object -First 10 | ForEach-Object { Note "  $_" }
        Finish 3 $false 'Kurulum dosyaları özet listesiyle eşleşmiyor. Hiçbir şey kurulmadı.'
    }
    Note "Kurulum dosyaları doğrulandı ($($expected.Count) dosya)."
    Note 'Kurulumdan önce adaptör:'
    Get-AdapterNodes | ForEach-Object { Note "  $_" }

    $service = Get-BridgeService
    $bridgeOld = if ($service) { Get-ProductVersion (Join-Path $BridgeDir 'WPods.Service.dll') } else { '' }
    $usbipPresent = Test-Path -LiteralPath $UsbipExe

    # Windows servis programını çalıştırıyor mu? Hiçbir şeye dokunmadan önce, yalnızca durum soran komutla denenir.
    if (-not $script:BridgeBlocked) {
        $probe = Invoke-Native (Join-Path $Kaynak 'bridge\WPods.Service.exe') '--status'
        if ($probe.LaunchError -ne 0) {
            Note "Servis programı denemesi: $($probe.Text) (Windows hata kodu $($probe.LaunchError))"
            $script:BridgeBlocked = "Windows köprü servisinin programını çalıştırmadı ($($probe.Text -replace '^başlatılamadı: ', ''))."
        } else {
            Note 'Servis programı denemesi: çalışıyor.'
        }
    }
    if ($script:BridgeBlocked) { Note "Köprü kurulmayacak: $($script:BridgeBlocked)" }
    $driverWork = (-not $script:BridgeBlocked) -and ((-not $usbipPresent) -or (-not $service) -or ($bridgeOld -ne $BridgeNew))

    # 2. Geri yükleme noktası: yalnızca sürücü ya da servis değişecekse.
    Step 2 $Toplam 'Geri yükleme noktası'
    if (-not $driverWork) {
        Note 'Sürücü ve servis değişmeyecek; geri yükleme noktası gerekmiyor.'
    } elseif (-not $RestorePoint) {
        Note 'Geri yükleme noktası istenmedi; atlandı.'
    } else {
        Note 'Geri yükleme noktası oluşturuluyor (bir dakika kadar sürebilir)...'
        if (-not (New-RestorePoint)) {
            Finish 4 $false 'Geri yükleme noktası oluşturulamadı (Sistem Koruması kapalı olabilir). Hiçbir şey kurulmadı. İstersen kutunun işaretini kaldırıp yeniden dene.'
        }
    }

    # 3. Panel dosyaları ve kayıt.
    Step 3 $Toplam 'Panel dosyaları'
    Stop-Panel | Out-Null
    if ((Test-Path -LiteralPath $AppDir) -and -not (Remove-Folder $AppDir)) {
        Finish 5 $false 'Eski panel dosyaları silinemedi (bir dosya kullanımda olabilir). Bilgisayarı yeniden başlatıp kurulumu yeniden çalıştır.'
    }
    # Önce kaldırıcı ve betikler: panel dosyaları yerine konduğu anda kaldırılabilir olsun.
    $scripts = Join-Path $Hedef 'kurulum'
    New-Item -ItemType Directory -Force $scripts | Out-Null
    Copy-Item -Path (Join-Path $Kaynak 'kurulum\*') -Destination $scripts -Force
    foreach ($name in 'Kaldir.exe', 'LISANS.txt', 'UCUNCU-TARAFLAR.txt', 'kaynak.zip', 'surum.txt') {
        Copy-Item -LiteralPath (Join-Path $Kaynak $name) -Destination (Join-Path $Hedef $name) -Force
    }
    Move-Item -LiteralPath (Join-Path $Kaynak 'app') -Destination $AppDir
    $problems = @(Test-Manifest $expected $AppDir 'app\')
    if ($problems.Count -gt 0) {
        $problems | Select-Object -First 10 | ForEach-Object { Note "  $_" }
        Remove-Folder $AppDir | Out-Null
        Finish 6 $false 'Kopyalanan panel dosyaları özet listesiyle eşleşmiyor. Panel kurulmadı.'
    }
    Register-Install
    $panelInstalled = $true
    Note "Panel kuruldu: $AppDir (Uygulamalar listesine ve Başlat menüsüne eklendi)"
    if (@(Get-WritableByUsers $Hedef).Count -gt 0) {
        Finish $Kismi $false "Kurulum klasörü yönetici olmayan kullanıcılarca yazılabiliyor; köprü servisi buraya kurulamaz. $KismiEk"
    }

    if ($Koprusuz) {
        Step 4 $Toplam 'usbip-win2 sürücüsü'
        Note 'Köprüsüz sürüm: atlandı.'
        Step 5 $Toplam 'Köprü servisi'
        Note 'Köprüsüz sürüm: atlandı.'
        Register-Install
        Finish 0 $false 'Kurulum tamamlandı.'
    }

    # Köprü kurulamıyorsa burada biter: sürücüye, servise ve Bluetooth'a dokunulmaz.
    if ($script:BridgeBlocked) {
        Step 4 $Toplam 'usbip-win2 sürücüsü'
        Note 'Köprü kurulmayacağı için atlandı.'
        Step 5 $Toplam 'Köprü servisi'
        if (-not $service -and (Test-Path -LiteralPath $BridgeDir)) {
            # Önceki bir denemeden kalan, kaydedilmemiş servis dosyaları.
            Remove-Folder $BridgeDir | Out-Null
            Note 'Önceki denemeden kalan servis dosyaları silindi.'
        }
        Note 'Köprü servisi kurulmadı.'
        Register-Install
        $extra = if ($usbipPresent) { ' usbip-win2 kurulu duruyor; kullanılmıyor, PodPanel kaldırılırken birlikte kaldırılabilir.' } else { '' }
        Finish $Kismi $false "$($script:BridgeBlocked) Sürücülere ve Bluetooth'a dokunulmadı.$extra $KismiEk||$DenetimYolu"
    }

    # 4. usbip-win2.
    Step 4 $Toplam 'usbip-win2 sürücüsü'
    $restart = $false
    if (-not $usbipPresent) {
        $setup = Join-Path $Kaynak "usbip\$UsbipSetupName"
        $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        Note "usbip-win2 kurulum dosyası: SHA-256 $hash, imza $($signature.Status), $($signature.SignerCertificate.Subject)"
        if ($hash -ne $UsbipSha256) { Finish $Kismi $false "usbip-win2 kurulum dosyasının özeti beklenenle eşleşmiyor; sürücü kurulmadı. $KismiEk" }
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $UsbipSigner) {
            Finish $Kismi $false "usbip-win2 kurulum dosyasının imzası geçerli değil ya da beklenen imzacıya ait değil; sürücü kurulmadı. $KismiEk"
        }
        Note 'usbip-win2 kuruluyor. USB aygıtların (klavye ve fare dahil) bir an kopabilir; bu beklenen bir durum.'
        $setupLog = Join-Path $LogDir 'usbip-setup.log'
        $arguments = '/SILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS="!desktopicon" /LOG="' + $setupLog + '"'
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru -Wait
        Note "usbip-win2 kurulumu çıkış kodu: $($process.ExitCode)"
        if (-not (Test-Path -LiteralPath $UsbipExe)) { Finish $Kismi $false "usbip-win2 kurulamadı (çıkış kodu $($process.ExitCode); ayrıntı: $setupLog). $KismiEk" }
        Set-ItemProperty $StateKey -Name UsbipInstalledByUs -Value 1 -Type DWord
        $restart = $true
    } else {
        $ude = Get-CimInstance Win32_SystemDriver -Filter "Name='usbip2_ude'" -ErrorAction SilentlyContinue
        Note "usbip-win2 zaten kurulu; sürücü durumu: $(if ($ude) { $ude.State } else { 'yok' })"
        if (-not $ude -or $ude.State -ne 'Running') {
            Note 'usbip-win2 sürücüsü henüz çalışmıyor: servis yeniden başlatmada devreye girecek.'
            $restart = $true
        }
    }

    # 5. Köprü servisi.
    Step 5 $Toplam 'Köprü servisi'
    if (-not $service) {
        $problem = Install-BridgeFiles $expected
        if ($problem) {
            Remove-Folder $BridgeDir | Out-Null
            Finish $Kismi $restart "$problem Köprü servisi kurulmadı; Bluetooth'a dokunulmadı. $KismiEk"
        }
        $serviceTouched = $true
        if ($restart) {
            $install = Invoke-Native $ServiceExe '--install --no-start'
            Note "--install --no-start çıkış kodu: $($install.Code) $($install.Text)"
            if ($install.Code -ne 0) {
                Remove-Bridge | Out-Null
                if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) { Remove-Folder $BridgeDir | Out-Null }
                $why = if ($install.LaunchError -ne 0) { " Windows servis programını çalıştırmadı ($($install.Text -replace '^başlatılamadı: ', ''))." } else { '' }
                Finish $Kismi $restart "Köprü servisi kaydedilemedi; Bluetooth'a dokunulmadı.$why $KismiEk"
            }
            Note 'Servis kaydedildi; yeniden başlatmada Bluetooth adaptörünü devralacak.'
        } else {
            Note 'Servis kuruluyor. Bluetooth birkaç saniyeliğine kesilecek ve kendiliğinden geri gelecek.'
            if (-not (Start-BridgeAndWait 150)) {
                Note 'Servis Bluetooth''u devralamadı; geri alınıyor (bir dakika sürebilir)...'
                $restored = Remove-Bridge
                if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) { Remove-Folder $BridgeDir | Out-Null }
                $why = if ($script:LaunchProblem) { " Windows servis programını çalıştırmadı ($($script:LaunchProblem -replace '^başlatılamadı: ', ''))." } else { '' }
                if ($restored) { Finish $Kismi $false "Köprü servisi Bluetooth'u devralamadı.$why Servis kaldırıldı ve Bluetooth eski hâline döndü. $KismiEk" }
                Finish 13 $true 'Köprü servisi Bluetooth''u devralamadı ve geri alma doğrulanamadı. Bilgisayarı yeniden başlat; Bluetooth gelmezse Sistem Geri Yükleme ile "PodPanel kurulumu oncesi" noktasına dön.'
            }
        }
    } elseif ($bridgeOld -eq $BridgeNew) {
        $state = (Get-Service $ServiceName).Status
        Note "Servis güncel ($bridgeOld), durumu: $state"
        if ($state -ne 'Running') {
            $waits = (Get-ItemProperty 'HKLM:\SOFTWARE\WPods\Service' -Name WaitsForRestart -ErrorAction SilentlyContinue).WaitsForRestart -eq 1
            if ($waits -or $restart) {
                Note 'Servis yeniden başlatmayı bekliyor.'
                $restart = $true
            } elseif (-not (Start-BridgeAndWait 120)) {
                Finish 14 $true 'Köprü servisi kurulu ama başlatılamadı. Bilgisayarı yeniden başlat; köprü yine çalışmazsa PodPanel''i kaldırıp yeniden kur.'
            }
        }
    } else {
        Note "Servis güncelleniyor: $bridgeOld -> $BridgeNew. Bluetooth 10-20 saniye kesilecek ve kendiliğinden geri gelecek."
        $stop = Invoke-Native $ServiceExe '--stop'
        Note "--stop çıkış kodu: $($stop.Code) $($stop.Text)"
        if ((Get-Service $ServiceName).Status -ne 'Stopped') { Start-Sleep -Seconds 5 }
        if ((Get-Service $ServiceName).Status -ne 'Stopped') {
            Finish 15 $true 'Köprü servisi durdurulamadı; servis dosyalarına dokunulmadı. Bluetooth çalışmıyorsa bilgisayarı yeniden başlat.'
        }
        $serviceTouched = $true
        if (Test-Path -LiteralPath $BridgeBackup) { Remove-Folder $BridgeBackup | Out-Null }
        Rename-Item -LiteralPath $BridgeDir -NewName (Split-Path $BridgeBackup -Leaf)
        $problem = Install-BridgeFiles $expected
        $ok = $false
        if ($problem) { Note "$problem Eski sürüme dönülüyor." } else { $ok = Start-BridgeAndWait 120 }
        if (-not $ok) {
            Note 'Yeni sürüm Bluetooth''u geri getiremedi; eski sürüme dönülüyor.'
            if (Test-Path -LiteralPath $ServiceExe) { Invoke-Native $ServiceExe '--stop' | Out-Null; Start-Sleep -Seconds 3 }
            Remove-Folder $BridgeDir | Out-Null
            Rename-Item -LiteralPath $BridgeBackup -NewName (Split-Path $BridgeDir -Leaf)
            if (Start-BridgeAndWait 120) {
                Finish $Kismi $false 'Köprü servisi güncellenemedi; eski sürümü yeniden çalışıyor ve Bluetooth geri geldi. Panel yeni sürüme güncellendi.'
            }
            Note 'Eski sürüm de başlatılamadı; servis kaldırılıyor ve adaptör Windows''a geri veriliyor.'
            if (Remove-Bridge) { Finish $Kismi $false "Köprü servisi güncellenemedi ve kaldırıldı; Bluetooth Windows'un kendi sürücüsüne döndü. $KismiEk" }
            Finish 17 $true 'Köprü servisi güncellenemedi ve Bluetooth''un geri geldiği doğrulanamadı. Bilgisayarı yeniden başlat; Bluetooth gelmezse PodPanel''i Uygulamalar listesinden kaldır.'
        }
        Remove-Folder $BridgeBackup | Out-Null
    }

    Register-Install
    Note 'Kurulumdan sonra adaptör:'
    Get-AdapterNodes | ForEach-Object { Note "  $_" }
    if ($restart) { Finish 0 $true 'Kurulum tamamlandı. Köprünün devreye girmesi için bilgisayarı yeniden başlat.' }
    Finish 0 $false 'Kurulum tamamlandı.'
} catch {
    Note "Beklenmeyen hata: $($_.Exception.Message)"
    Note "$($_.ScriptStackTrace)"
    if ($serviceTouched) {
        $restored = $false
        try { $restored = Remove-Bridge } catch { Note "Geri alma hatası: $($_.Exception.Message)" }
        if ($restored) { Finish $Kismi $false "Köprü servisi kurulurken hata oluştu ($($_.Exception.Message)); servis kaldırıldı ve Bluetooth eski hâlinde. $KismiEk" }
        Finish 1 $true "Kurulum yarıda kaldı: $($_.Exception.Message) Köprü servisi geri alınamadı; bilgisayarı yeniden başlat. Bluetooth gelmezse PodPanel'i Uygulamalar listesinden kaldır."
    }
    if ($panelInstalled) { Finish $Kismi $false "Köprü kurulurken hata oluştu: $($_.Exception.Message) Bluetooth'a dokunulmadı. $KismiEk" }
    Finish 1 $false "Kurulum yarıda kaldı: $($_.Exception.Message) Sürücülere, köprü servisine ve Bluetooth'a dokunulmadı."
}
