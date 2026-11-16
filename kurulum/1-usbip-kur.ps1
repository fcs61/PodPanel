# Aşama 2: geri yükleme noktası oluşturur, sonra usbip-win2 0.9.8.1'i kurar. Yönetici olarak çalıştırılır.
# Kurulum dosyasını çalıştırmadan önce SHA-256 özetini ve imzasını yeniden denetler; biri tutmazsa hiçbir şey kurmaz.
# Bilgisayarı kendisi yeniden başlatmaz.
$ErrorActionPreference = 'Stop'
$Sha256 = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
$SignerThumbprint = '9AC56B6C76141395D74FFF6652818376E80B9C95'   # Cloudyne Systems (Scheibling Consulting AB), GlobalSign EV
$setup = Join-Path $PSScriptRoot 'usbip-win2\USBip-0.9.8.1-x64.exe'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'usbip-kurulum.txt'
$setupLog = Join-Path $dir 'usbip-setup.log'
New-Item -ItemType Directory -Force $dir | Out-Null
$result = @("Başlangıç: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$setupStarted = $false

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

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        # Yönetici değilse kendini yönetici olarak yeniden başlatır (Windows onay penceresi açılır).
        Write-Host 'Yönetici onayı isteniyor; kurulum yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }
    if (Test-Path 'C:\Program Files\USBip\usbip.exe') {
        Finish 'usbip-win2 zaten kurulu. Hiçbir şey değiştirilmedi.' 3
    }

    # 1. Kurulum dosyası: özet ve imza. Dosya, denetimden kurulumun sonuna kadar yazmaya kapalı tutulur.
    $held = [IO.File]::Open($setup, 'Open', 'Read', 'Read')
    try {
        $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        $result += "Kurulum dosyası SHA-256: $hash"
        $result += "İmza: $($signature.Status), $($signature.SignerCertificate.Subject)"
        if ($hash -ne $Sha256) {
            Finish 'Kurulum dosyasının özeti beklenenle eşleşmiyor. Hiçbir şey kurulmadı.' 4
        }
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SignerThumbprint) {
            Finish 'Kurulum dosyasının imzası geçerli değil ya da beklenen imzacıya ait değil. Hiçbir şey kurulmadı.' 5
        }
        Write-Host 'Kurulum dosyası doğrulandı (özet ve imza).' -ForegroundColor Green

        # 2. Geri yükleme noktası. Windows 24 saat içinde ikinci bir nokta oluşturmayabilir; o durumda son nokta yeterli sayılır.
        Write-Host 'Geri yükleme noktası oluşturuluyor (bir dakika kadar sürebilir)...'
        Checkpoint-Computer -Description 'PodPanel: usbip-win2 oncesi' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
        $point = Get-ComputerRestorePoint | Sort-Object SequenceNumber | Select-Object -Last 1
        if (-not $point) {
            Finish 'Geri yükleme noktası oluşturulamadı (Sistem Koruması kapalı olabilir). Hiçbir şey kurulmadı.' 6
        }
        $created = [Management.ManagementDateTimeConverter]::ToDateTime($point.CreationTime)
        $result += "Son geri yükleme noktası: #$($point.SequenceNumber) '$($point.Description)' $($created.ToString('yyyy-MM-dd HH:mm:ss'))"
        if (((Get-Date) - $created).TotalHours -gt 24) {
            Finish "En yeni geri yükleme noktası 24 saatten eski ($($created.ToString('yyyy-MM-dd HH:mm'))). Hiçbir şey kurulmadı." 7
        }
        Write-Host "Geri yükleme noktası hazır: '$($point.Description)' ($($created.ToString('HH:mm')))." -ForegroundColor Green
        # Gölge kopya alanı küçükse Windows eski noktaları silebilir: alanın durumu kayda geçer.
        $result += 'Gölge kopya alanı:'
        try {
            $shadow = & "$env:SystemRoot\System32\vssadmin.exe" list shadowstorage
            $shadow | Where-Object { "$_".Trim() } | Select-Object -Skip 2 | ForEach-Object { $result += "  $("$_".Trim())" }
        } catch {
            $result += "  (okunamadı: $($_.Exception.Message))"
        }

        # 3. Kurulum. WPods'un kendi kurulumunun kullandığı seçeneklerle aynı; yalnızca ilerleme penceresi görünür.
        Write-Host 'usbip-win2 kuruluyor. USB aygıtların (klavye ve fare dahil) bir an kopabilir; bu beklenen bir durum.'
        $arguments = '/SILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS="!desktopicon" /LOG="' + $setupLog + '"'
        $setupStarted = $true
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru -Wait
        $result += "Kurulum çıkış kodu: $($process.ExitCode)"
    } finally {
        $held.Dispose()
    }

    $installed = Test-Path 'C:\Program Files\USBip\usbip.exe'
    $result += "usbip.exe kurulu: $installed"
    $result += 'Sürücüler:'
    Get-CimInstance Win32_SystemDriver -Filter "Name LIKE 'usbip%'" | ForEach-Object { $result += "  $($_.Name): $($_.State), başlatma $($_.StartMode), $($_.PathName)" }
    if (-not $installed) {
        Finish "usbip-win2 kurulamadı (çıkış kodu $($process.ExitCode)). Ayrıntı: kurulum\durum\usbip-setup.log" 8
    }
    # Kurulumdan sonra geri yükleme noktası hâlâ duruyor mu?
    $still = Get-ComputerRestorePoint | Where-Object { $_.SequenceNumber -eq $point.SequenceNumber }
    $result += "Geri yükleme noktası kurulumdan sonra duruyor: $([bool]$still)"
    if (-not $still) {
        Finish 'usbip-win2 kuruldu, ama geri yükleme noktası artık görünmüyor. YENİDEN BAŞLATMADAN ÖNCE sohbete dönüp haber ver.' 9
    }
    Finish 'usbip-win2 kuruldu. Şimdi Windows''u yeniden başlat; açılınca sohbete dönüp haber ver.' 0
} catch {
    $state = if ($setupStarted) { 'Kurulum başlamıştı; durumu denetlemek gerekir.' } else { 'Kurulum başlamadı, hiçbir şey kurulmadı.' }
    Finish "Beklenmeyen hata: $($_.Exception.Message) $state" 1
}
