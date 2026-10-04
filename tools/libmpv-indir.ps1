# mpv motoru için libmpv-2.dll'yi indirip lib\ klasörüne koyar.
# Kaynak: mpv.io'nun Windows için yönlendirdiği shinchiro derlemeleri.
# Gerekli: 7-Zip (arşiv .7z biçiminde).
param([string]$Release = "20261004", [string]$Asset = "mpv-dev-x86_64-20261004-git-413ff0b1cd.7z")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root "lib"
$tmp = Join-Path $env:TEMP "libmpv-indir"
New-Item -ItemType Directory -Force $lib, $tmp | Out-Null

$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sevenZip) { throw "7-Zip bulunamadı. https://www.7-zip.org adresinden kurup tekrar deneyin." }

$url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Release/$Asset"
$archive = Join-Path $tmp $Asset
Write-Host "İndiriliyor: $url"
Invoke-WebRequest $url -OutFile $archive

& $sevenZip e -y "-o$tmp" $archive libmpv-2.dll | Out-Null
Move-Item -Force (Join-Path $tmp "libmpv-2.dll") (Join-Path $lib "libmpv-2.dll")
Remove-Item -Recurse -Force $tmp
Write-Host "Hazır: $(Join-Path $lib 'libmpv-2.dll')"
