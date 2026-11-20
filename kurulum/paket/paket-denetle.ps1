# paketle.ps1'in ürettiği kurulum paketini denetler. Yönetici gerektirmez; hiçbir şey kurmaz ve kurulum programını
# çalıştırmaz. Bir denetim başarısızsa sıfırdan farklı kodla çıkar. Kullanım:
#   powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\paket-denetle.ps1 -Surum 1.0.0
param(
    [Parameter(Mandatory)][string]$Surum,
    [string]$Cikti = (Join-Path $env:USERPROFILE 'PodPanel\dagitim'),
    # paketle.ps1'in ara klasörü (payload ve payload.zip burada durur).
    [string]$Calisma = (Join-Path $env:USERPROFILE 'PodPanel\tools\build\paket')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$UsbipSigner = '9AC56B6C76141395D74FFF6652818376E80B9C95'
$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    Write-Host ("{0}  {1} {2}" -f $(if ($ok) { 'TAMAM ' } else { 'HATA  ' }), $name, $detail)
    if (-not $ok) { $script:failures++ }
}

# 1. Betiklerin sözdizimi.
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.ps1) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors) | Out-Null
    Check "sözdizimi: $($file.Name)" ($errors.Count -eq 0) ($errors | Select-Object -First 2 | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" })
}

# 2. Kurulum dosyası ve özet dosyası.
$installer = Join-Path $Cikti "PodPanel-Kurulum-$Surum.exe"
$exists = Test-Path -LiteralPath $installer
Check 'kurulum dosyası var' $exists $installer
if ($exists) {
    $item = Get-Item -LiteralPath $installer
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $listed = ([IO.File]::ReadAllText((Join-Path $Cikti "PodPanel-Kurulum-$Surum.sha256.txt")) -split '\s+')[0]
    Check 'özet dosyası kurulum dosyasıyla eşleşiyor' ($listed -eq $hash) $hash
    Check 'kurulum dosyasının sürümü' ($item.VersionInfo.ProductVersion -eq $Surum) $item.VersionInfo.ProductVersion
    $zipSize = (Get-Item -LiteralPath (Join-Path $Calisma 'payload.zip')).Length
    Check 'kurulum dosyası gömülü dosyaları taşıyacak boyutta' ($item.Length -gt $zipSize) ("{0:N0} MB" -f ($item.Length / 1MB))
}

# 3. Gömülen arşiv: her dosyanın özeti, arşivdeki özet listesiyle (dosyalar.sha256) eşleşmeli.
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Calisma 'payload.zip'))
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) { if ($entry.Name) { $entries[$entry.FullName.Replace('/', '\')] = $entry } }
    $manifest = $entries['dosyalar.sha256']
    Check 'arşivde özet listesi var' ($null -ne $manifest)
    if ($manifest) {
        $reader = New-Object IO.StreamReader($manifest.Open(), [Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd(); $reader.Dispose()
        $expected = @{}
        foreach ($line in $text -split "`r?`n") {
            if ($line -match '^([0-9A-Fa-f]{64})\s+(.+)$') { $expected[$Matches[2]] = $Matches[1].ToUpperInvariant() }
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        $wrong = @()
        foreach ($name in $expected.Keys) {
            if (-not $entries.ContainsKey($name)) { $wrong += "eksik: $name"; continue }
            $stream = $entries[$name].Open()
            try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') } finally { $stream.Dispose() }
            if ($actual -ne $expected[$name]) { $wrong += "özeti farklı: $name" }
        }
        foreach ($name in $entries.Keys) {
            if ($name -ne 'dosyalar.sha256' -and -not $expected.ContainsKey($name)) { $wrong += "listede yok: $name" }
        }
        Check "arşivdeki dosyalar özet listesiyle eşleşiyor ($($expected.Count) dosya)" ($wrong.Count -eq 0) ($wrong | Select-Object -First 3)
        foreach ($name in 'app\PodPanel.exe', 'bridge\WPods.Service.exe', 'Kaldir.exe', 'kaynak.zip', 'LISANS.txt', 'kurulum\kur.ps1', 'kurulum\kaldir.ps1') {
            Check "arşivde var: $name" ($entries.ContainsKey($name))
        }
    }
} finally {
    $zip.Dispose()
}

# 4. Sürüm bilgisi ve usbip-win2 kurulum dosyasının imzası (kur.ps1 kurarken aynı imzacıyı arar).
$payload = Join-Path $Calisma 'payload'
$lines = [IO.File]::ReadAllLines((Join-Path $payload 'surum.txt'))
$bridge = (Get-Item -LiteralPath (Join-Path $payload 'bridge\WPods.Service.dll')).VersionInfo.ProductVersion
Check 'surum.txt' ($lines[0] -eq $Surum -and $lines[1] -eq $bridge) ($lines -join ' | ')
$panel = (Get-Item -LiteralPath (Join-Path $payload 'app\PodPanel.dll')).VersionInfo.ProductVersion
Check 'panelin sürümü' ($panel -eq $Surum) $panel
$usbip = Get-ChildItem -LiteralPath (Join-Path $payload 'usbip') -Filter *.exe | Select-Object -First 1
$signature = Get-AuthenticodeSignature -LiteralPath $usbip.FullName
Check 'usbip-win2 imzası' ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Thumbprint -eq $UsbipSigner) "$($signature.Status)"

# 5. Kurulum penceresinin İngilizce çeviri tablosu (src\Setup\ceviri-en.txt) betiklerin Türkçe iletilerini karşılıyor mu?
#    Betiklerdeki her metin, pencerenin yaptığı gibi tablodan geçirilir; içinde Türkçeye özgü harf kalan metin eksiktir.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exact = New-Object Collections.Hashtable ([StringComparer]::Ordinal)
$rules = New-Object Collections.ArrayList
$section = 'tam'
foreach ($line in [IO.File]::ReadAllLines((Join-Path $repo 'src\Setup\ceviri-en.txt'), [Text.Encoding]::UTF8)) {
    $line = $line.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    if ($line -eq '[tam]' -or $line -eq '[parca]') { $section = $line.Trim('[', ']'); continue }
    $split = $line.IndexOf(' => ')
    if ($split -le 0) { continue }
    $source = $line.Substring(0, $split).Trim()
    $target = $line.Substring($split + 4).Trim()
    if ($section -eq 'tam') { $exact[$source] = $target; continue }
    # Ceviri.cs'teki ToPattern ile aynı: metin harfi harfine, {0} bir yakalama grubu.
    $pattern = [regex]::Escape($source)
    $pattern = [regex]::Replace($pattern, '\\\{(\d)}$', '(?<g$1>.+)')
    $pattern = [regex]::Replace($pattern, '\\\{(\d)}', '(?<g$1>.+?)')
    [void]$rules.Add(@{ Pattern = [regex]$pattern; Target = [regex]::Replace($target, '\{(\d)\}', '${g$1}') })
}
function Convert-Message([string]$text) {
    $trimmed = $text.Trim()
    if ($exact.ContainsKey($trimmed)) { return $exact[$trimmed] }
    foreach ($rule in $rules) { $text = $rule.Pattern.Replace($text, $rule.Target) }
    $text
}
$turkish = [regex]'[çğıöşüÇĞİÖŞÜ]'
$missing = @()
foreach ($name in 'ortak.ps1', 'kur.ps1', 'kaldir.ps1') {
    # Betikler BOM'suz UTF-8'dir (pencere onları UTF-8 olarak okur); ParseFile ANSI sanacağı için metin UTF-8 okunur.
    $code = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name), [Text.Encoding]::UTF8)
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($code, [ref]$null, [ref]$null)
    $strings = $ast.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)
    foreach ($string in $strings) {
        $value = $string.Value
        if ($string -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
            # Değişen parçalar örnek bir değerle doldurulur.
            $value = [regex]::Replace($value, '\$\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)', 'X')
            $value = [regex]::Replace($value, '\$(?:script:|env:)?\w+', 'X')
        }
        $value = [regex]::Replace($value, '\{(\d),[^}]*\}', '{$1}')
        if (-not $turkish.IsMatch($value)) { continue }
        foreach ($part in $value -split '\|\|') {
            $translated = Convert-Message $part
            if ($turkish.IsMatch($translated)) { $missing += "$name satır $($string.Extent.StartLineNumber): $($translated.Trim())" }
        }
    }
}
Check "İngilizce çeviri tablosu betiklerin iletilerini karşılıyor ($($rules.Count) kural)" ($missing.Count -eq 0) ''
$missing | Select-Object -First 12 | ForEach-Object { Write-Host "        çevrilmemiş: $_" }

Write-Host ''
Write-Host $(if ($failures -eq 0) { 'Tüm denetimler geçti.' } else { "$failures denetim başarısız." })
exit $failures
