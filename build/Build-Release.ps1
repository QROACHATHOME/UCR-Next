<#
.SYNOPSIS
  Builds the release downloads into dist\:
    UCR-Next-<version>-setup.exe    installer (small app folder; installs the .NET 8 Desktop Runtime if missing)
    UCR-Next-<version>-win-x64.zip  portable, self-contained (no .NET needed; for "install in place of UCR")

.EXAMPLE
  .\build\Build-Release.ps1                 # version from Directory.Build.props
  .\build\Build-Release.ps1 -Version 0.13.0
  .\build\Build-Release.ps1 -SkipInstaller  # zip only (no Inno Setup needed)

  The installer needs Inno Setup 6 (ISCC.exe): winget install JRSoftware.InnoSetup
#>
param(
    [string]$Version,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v')   # accepts a tag name like v0.13.0
Write-Host "UCR-Next $Version"

$out  = Join-Path $root 'out'
$dist = Join-Path $root 'dist'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $dist | Out-Null

# Shared publish options: English only (no 13 WPF language folders), no .pdb files.
$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:SatelliteResourceLanguages=en', '-p:DebugType=none')

function Publish([string]$dir, [string[]]$extra) {
    dotnet publish src/UcrNext.App @common @extra -o $dir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    Copy-Item README.md, LICENSE $dir
}

# 1. Portable zip: self-contained (the .NET runtime is in the folder, so it is a few hundred files).
$portable = Join-Path $out 'portable'
Publish $portable @('--self-contained', 'true')
$zip = Join-Path $dist "UCR-Next-$Version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$portable\*" -DestinationPath $zip
Write-Host "zip:       $zip"

if ($SkipInstaller) { return }

# 2. Installer: framework-dependent app (about 25 files) plus the runtime installer, run only if needed.
$app = Join-Path $out 'app'
Publish $app @('--self-contained', 'false')

$redist = Join-Path $out 'redist'
New-Item -ItemType Directory -Force $redist | Out-Null
$runtime = Join-Path $redist 'windowsdesktop-runtime-8-win-x64.exe'
$cache = Join-Path $env:LOCALAPPDATA 'UCR-Next-build\windowsdesktop-runtime-8-win-x64.exe'
if ((Test-Path $cache) -and ((Get-Item $cache).LastWriteTime -gt (Get-Date).AddDays(-30))) {
    Copy-Item $cache $runtime
} else {
    Write-Host 'Downloading the .NET 8 Desktop Runtime installer from Microsoft...'
    Invoke-WebRequest 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' -OutFile $runtime -UseBasicParsing
    New-Item -ItemType Directory -Force (Split-Path $cache) | Out-Null
    Copy-Item $runtime $cache -Force
}
$sig = Get-AuthenticodeSignature $runtime
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "The downloaded runtime installer is not signed by Microsoft ($($sig.Status))."
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (ISCC.exe). Install it: winget install JRSoftware.InnoSetup' }

& $iscc /Qp "/DAppVersion=$Version" "/DAppDir=$app" "/DRuntime=$runtime" "/DRoot=$root" "/O$dist" (Join-Path $PSScriptRoot 'UCR-Next.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Host "installer: $(Join-Path $dist "UCR-Next-$Version-setup.exe")"
