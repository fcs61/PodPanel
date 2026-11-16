# Köprü servisini kurulum\servis klasöründeki sürümle değiştirir. Yönetici olarak çalıştırılır.
# Sıra: yeni dosyaları doğrula -> servisi durdur (adaptör serviste kalır) -> eski dosyaları yedekle, yenileri kopyala ->
# servisi başlat ve Bluetooth'un geri gelmesini bekle. Geri gelmezse eski sürüme döner; o da olmazsa servisi kaldırıp
# adaptörü Windows'un kendi sürücüsüne verir. Bluetooth güncelleme sırasında 10-20 saniye kesilir.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'servis'
$manifest = Join-Path $PSScriptRoot 'servis.sha256'
$dest = Join-Path $env:ProgramFiles 'PodPanel\Bridge'
$backup = Join-Path $env:ProgramFiles 'PodPanel\Bridge.onceki'
$exe = Join-Path $dest 'WPods.Service.exe'
$serviceName = 'WPodsService'
$serviceLog = Join-Path $env:ProgramData 'WPods\logs\service.log'
$dir = Join-Path $PSScriptRoot 'durum'
$resultPath = Join-Path $dir 'servis-guncelleme.txt'
$waitSeconds = 120
New-Item -ItemType Directory -Force $dir | Out-Null
$result = @("Başlangıç: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$stopped = $false

function Note([string]$text) {
    $script:result += $text
    Write-Host $text
}

function Finish([string]$message, [int]$code) {
    $script:result += $message
    $script:result += "Sonuç kodu: $code"
    if (Test-Path $serviceLog) {
        $script:result += '--- servis günlüğü (son 40 satır) ---'
        $script:result += Get-Content $serviceLog -Tail 40 -ErrorAction SilentlyContinue
    }
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

function Test-BluetoothUp {
    $radio = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' }
    $enumerator = Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\MS_BTHBRB\*' -and $_.Status -eq 'OK' }
    [bool]$radio -and [bool]$enumerator
}

# Servisi başlatır ve Bluetooth'un sanal adaptör üzerinden geri gelmesini bekler.
function Start-AndWait([string]$label) {
    $startText = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $install = Invoke-Native $exe '--install'
    Note "$label --install çıkış kodu: $($install.Code) $($install.Text)"
    for ($elapsed = 0; $elapsed -lt $waitSeconds; $elapsed += 3) {
        Start-Sleep -Seconds 3
        $service = Get-Service $serviceName -ErrorAction SilentlyContinue
        $status = Invoke-Native $exe '--status'
        $running = (Test-Path $serviceLog) -and [bool](Get-Content $serviceLog -Tail 200 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d).*\[engine\] state Running' -and $Matches[1] -ge $startText })
        $up = Test-BluetoothUp
        Write-Host ("  {0,3} sn: servis {1}, oturum çalışıyor {2}, Bluetooth hazır {3}" -f ($elapsed + 3), $service.Status, $running, $up)
        if ($status.Text -match 'guard tripped: True') { Note 'Servisin kendi koruması devreye girdi.'; return $false }
        if ($service.Status -eq 'Running' -and $running -and $up) { return $true }
    }
    $false
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Yönetici onayı isteniyor; güncelleme yeni bir pencerede sürecek.'
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        exit 0
    }

    if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue) -or -not (Test-Path $exe)) { Finish 'Servis kurulu değil. Hiçbir şey değiştirilmedi.' 2 }
    $problems = @(Test-Manifest $source)
    if ($problems.Count -gt 0) {
        $problems | Select-Object -First 10 | ForEach-Object { Note "  $_" }
        Finish 'Yeni servis dosyaları özet listesiyle eşleşmiyor. Hiçbir şey değiştirilmedi.' 3
    }
    $oldVersion = (Get-Item (Join-Path $dest 'WPods.Service.dll')).VersionInfo.ProductVersion
    $newVersion = (Get-Item (Join-Path $source 'WPods.Service.dll')).VersionInfo.ProductVersion
    Note "Kurulu sürüm: $oldVersion   Yeni sürüm: $newVersion"
    if ($oldVersion -eq $newVersion) { Finish 'Bu sürüm zaten kurulu. Hiçbir şey değiştirilmedi.' 0 }

    Write-Host 'Servis durduruluyor; Bluetooth 10-20 saniye kesilecek ve kendiliğinden geri gelecek.' -ForegroundColor Yellow
    $stop = Invoke-Native $exe '--stop'
    Note "--stop çıkış kodu: $($stop.Code) $($stop.Text)"
    $stopped = $true
    if ((Get-Service $serviceName).Status -ne 'Stopped') { Start-Sleep -Seconds 5 }
    if ((Get-Service $serviceName).Status -ne 'Stopped') {
        $stopped = $false
        Finish 'Servis durdurulamadı; dosyalara dokunulmadı. Bluetooth çalışmıyorsa bilgisayarı yeniden başlat.' 4
    }

    # Eski dosyalar yedeklenir; yeni sürüm çalışmazsa geri konur.
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Rename-Item $dest $backup
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $source '*') $dest -Recurse -Force
    $problems = @(Test-Manifest $dest)
    $ok = $false
    if ($problems.Count -gt 0) {
        Note 'Kopyalanan dosyalar özet listesiyle eşleşmiyor; eski sürüme dönülüyor.'
    } else {
        $ok = Start-AndWait 'Yeni sürüm:'
    }

    if ($ok) {
        Start-Sleep -Seconds 10
        Note 'Bluetooth aygıtları:'
        Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^BTH(ENUM\\DEV_|LE\\DEV_)' } | ForEach-Object { Note "  $($_.FriendlyName): $($_.Status)" }
        Finish "Servis $newVersion sürümüne güncellendi ve Bluetooth geri geldi. Sohbete dönüp haber ver." 0
    }

    # Geri dönüş: eski dosyalar.
    Note 'Yeni sürüm Bluetooth''u geri getiremedi; eski sürüme dönülüyor.'
    Invoke-Native $exe '--stop' | Out-Null
    Start-Sleep -Seconds 3
    Remove-Item $dest -Recurse -Force
    Rename-Item $backup $dest
    if (Start-AndWait 'Eski sürüm:') {
        Finish 'Güncelleme başarısız oldu; eski sürüm yeniden çalışıyor ve Bluetooth geri geldi. Sohbete dönüp haber ver.' 5
    }
    Note 'Eski sürüm de başlatılamadı; servis kaldırılıyor ve adaptör Windows''a geri veriliyor.'
    $undo = Invoke-Native $exe '--uninstall'
    Note "--uninstall çıkış kodu: $($undo.Code) $($undo.Text)"
    for ($i = 0; $i -lt 20 -and -not (Test-BluetoothUp); $i++) { Start-Sleep -Seconds 3 }
    Finish "Servis kaldırıldı. Bluetooth hazır: $(Test-BluetoothUp). Sohbete dönüp haber ver." 6
} catch {
    $state = if ($stopped) { 'Servis durdurulmuştu: Bluetooth çalışmıyorsa bilgisayarı yeniden başlat, yine gelmezse 9-servis-kaldir.ps1 betiğini çalıştır.' } else { 'Servise dokunulmadı.' }
    Finish "Beklenmeyen hata: $($_.Exception.Message) $state" 1
}
