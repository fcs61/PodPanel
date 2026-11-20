# Köprü servisini kaynaktan derler, kurulum\servis klasörüne koyar ve özet listesini (kurulum\servis.sha256) yazar.
# paketle.ps1 bu klasörü ve listeyi kullanır. Yönetici gerektirmez; hiçbir şey kurmaz. Kullanım:
#   powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\servis-derle.ps1
param(
    # .NET 10 SDK'nın dotnet.exe dosyası. Boşsa önce %USERPROFILE%\PodPanel\tools\dotnet10, yoksa PATH'teki dotnet.
    [string]$Dotnet = '',
    [string]$Cikti = '',
    [string]$Ozet = '',
    # Ara dosyalar. Aynı klasörle aynı kaynaktan yapılan derleme aynı dosyaları üretir.
    [string]$Calisma = (Join-Path $env:USERPROFILE 'PodPanel\tools\build')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Cikti) { $Cikti = Join-Path $repo 'kurulum\servis' }
if (-not $Ozet) { $Ozet = Join-Path $repo 'kurulum\servis.sha256' }

# Sürüm etiketi: üst projenin sürümü + alındığı commit + bu depodaki değişiklik sırası. Servisin kaynağı her
# değiştiğinde son sayı artırılır ve bridge\CHANGES.md dosyasına bir satır eklenir.
$Surum = '0.3.2'
$Etiket = '0.3.2+f02e957.noupdate.11'

if (-not $Dotnet) {
    $local = Join-Path $env:USERPROFILE 'PodPanel\tools\dotnet10\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
}

$publish = Join-Path $Calisma 'bridge-publish'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

# bridge klasöründen çalıştırılır: oradaki global.json .NET 10 SDK'yı seçer (kökteki global.json .NET 8'i seçer).
Push-Location (Join-Path $repo 'bridge')
try {
    Write-Host "== Köprü servisi derleniyor ($Etiket)" -ForegroundColor Cyan
    & $Dotnet --version
    & $Dotnet publish 'src\WPods.Service\WPods.Service.csproj' -c Release --self-contained true -o $publish `
        --artifacts-path (Join-Path $Calisma 'bridge') "-p:Version=$Surum" "-p:InformationalVersion=$Etiket" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız oldu (çıkış kodu $LASTEXITCODE)." }
} finally {
    Pop-Location
}

if (Test-Path -LiteralPath $Cikti) { Remove-Item -LiteralPath $Cikti -Recurse -Force }
New-Item -ItemType Directory -Force $Cikti | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $Cikti -Recurse
Get-ChildItem -LiteralPath $Cikti -Filter *.pdb -Recurse | Remove-Item -Force
$Cikti = (Resolve-Path -LiteralPath $Cikti).Path

$lines = foreach ($file in Get-ChildItem -LiteralPath $Cikti -Recurse -File) {
    "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)  $($file.FullName.Substring($Cikti.Length + 1))"
}
$lines | Set-Content -Encoding utf8 -LiteralPath $Ozet

$version = (Get-Item (Join-Path $Cikti 'WPods.Service.dll')).VersionInfo.ProductVersion
if ($version -ne $Etiket) { throw "Derlenen servisin sürümü '$version'; beklenen '$Etiket'." }
Write-Host "Hazır: $Cikti ($($lines.Count) dosya, sürüm $version)" -ForegroundColor Green
Write-Host "Özet listesi: $Ozet"
