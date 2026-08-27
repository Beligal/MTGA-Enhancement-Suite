# Builds the bundled MSI installer (release/MTGAPlus-Installer.msi).
#
# Unlike the .NET EXE installer, the MSI bundles BepInEx + the plugin and
# installs them declaratively (files + registry), so Windows Defender doesn't
# flag it the way it flags a self-contained EXE that downloads and runs a
# remote PowerShell script.
#
# Prereqs (one-time):
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.UI.wixext/5.0.2
#
# Usage:
#   tools\build_msi.ps1 0.18.0
#
# Run AFTER tools\build_release.ps1 (it consumes release\*.dll / config.json).

param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot   = (Resolve-Path "$PSScriptRoot\..").Path
$releaseDir = Join-Path $repoRoot "release"
$msiDir     = Join-Path $repoRoot "MsiInstaller"
$stage      = Join-Path $msiDir "stage"
$wxs        = Join-Path $msiDir "MTGAPlus.wxs"
$licenseRtf = Join-Path $msiDir "license.rtf"
$outMsi     = Join-Path $releaseDir "MTGAPlus-Installer.msi"

$bepinexTag = "v5.4.23.5"
$bepinexVer = $bepinexTag.TrimStart('v')

# --- sanity: plugin payload must already be built ---
foreach ($f in @("MTGAEnhancementSuite.dll", "MTGAESBootstrapper.dll", "config.json")) {
    if (-not (Test-Path (Join-Path $releaseDir $f))) {
        throw "release\$f not found. Run tools\build_release.ps1 $Version first."
    }
}

Write-Host "=== Staging payload ===" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# 1. BepInEx 5 (doorstop + core) -> stage root
$bepinexUrl = "https://github.com/BepInEx/BepInEx/releases/download/$bepinexTag/BepInEx_win_x64_$bepinexVer.zip"
$zip = Join-Path $env:TEMP "bepinex_msi.zip"
Write-Host "  Downloading BepInEx $bepinexTag..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $bepinexUrl -OutFile $zip -UseBasicParsing
Expand-Archive -Path $zip -DestinationPath $stage -Force
Remove-Item $zip -Force

# BepInEx ships a 'changelog.txt' and an empty plugins/patchers tree; harmless.
# Remove the empty BepInEx\plugins (we recreate it with our plugin) so the
# Files harvest doesn't choke on a 0-file directory.
$emptyPlugins = Join-Path $stage "BepInEx\plugins"
if (Test-Path $emptyPlugins) { Remove-Item $emptyPlugins -Recurse -Force }

# 2. Plugin payload -> stage\BepInEx\plugins\MTGAEnhancementSuite
$pluginStage = Join-Path $stage "BepInEx\plugins\MTGAEnhancementSuite"
New-Item -ItemType Directory -Path $pluginStage -Force | Out-Null
Copy-Item (Join-Path $releaseDir "MTGAEnhancementSuite.dll") $pluginStage -Force
Copy-Item (Join-Path $releaseDir "MTGAESBootstrapper.dll")  $pluginStage -Force
Copy-Item (Join-Path $releaseDir "config.json")             $pluginStage -Force

# 3. Icons -> plugin\icons (matches what install.ps1 lays down)
$iconsSrc = Join-Path $repoRoot "assets\icons"
if (Test-Path $iconsSrc) {
    $iconsStage = Join-Path $pluginStage "icons"
    New-Item -ItemType Directory -Path $iconsStage -Force | Out-Null
    Copy-Item (Join-Path $iconsSrc "*.png") $iconsStage -Force
    Write-Host "  Bundled $((Get-ChildItem $iconsSrc -Filter *.png).Count) icon(s)."
}

Write-Host "  Stage tree:" -ForegroundColor Gray
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    Write-Host ("    " + $_.FullName.Substring($stage.Length + 1))
}
Write-Host ""

# --- build ---
Write-Host "=== Building MSI ===" -ForegroundColor Cyan
$wixVersion = $Version
if (($wixVersion -split '\.').Count -lt 3) { $wixVersion = "$wixVersion.0" }

# -arch x64 makes this a 64-bit package. Required so ProgramFiles64Folder (the
# fallback install dir) resolves to C:\Program Files rather than being undefined
# and landing under C:\Program Files (x86). Every MTGA machine is 64-bit Windows.
wix build $wxs `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d StageDir="$stage" `
    -d Version="$wixVersion" `
    -d LicenseRtf="$licenseRtf" `
    -o $outMsi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host ""
Write-Host "MSI built: $outMsi" -ForegroundColor Green
Get-Item $outMsi | Format-Table Name, Length, LastWriteTime
