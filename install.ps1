# MTGA Enhancement Suite Installer for Windows
# Usage: irm https://raw.githubusercontent.com/Beligal/MTGA-Enhancement-Suite/main/install.ps1 | iex

$ErrorActionPreference = "Stop"
$repo = "Beligal/MTGA-Enhancement-Suite"
$bepinexRepo = "BepInEx/BepInEx"
$bepinexTag = "v5.4.23.5"
$pluginDir = "BepInEx\plugins\MTGAEnhancementSuite"

Write-Host ""
Write-Host "=== MTGA Enhancement Suite Installer ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Find MTGA install path
function Find-MTGAPath {
    # Check registry for Epic Games install
    $epicPaths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher",
        "HKLM:\SOFTWARE\Epic Games\EpicGamesLauncher"
    )
    foreach ($regPath in $epicPaths) {
        try {
            $epicRoot = (Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue).AppDataPath
            if ($epicRoot) {
                $mtgaPath = Join-Path (Split-Path $epicRoot -Parent) "MTGA"
                if (Test-Path (Join-Path $mtgaPath "MTGA.exe")) { return $mtgaPath }
            }
        } catch {}
    }

    # Check common install locations
    $commonPaths = @(
        "C:\Program Files\Wizards of the Coast\MTGA",
        "C:\Program Files (x86)\Wizards of the Coast\MTGA",
        "${env:ProgramFiles}\Wizards of the Coast\MTGA",
        "${env:ProgramFiles(x86)}\Wizards of the Coast\MTGA",
        "D:\Program Files\Wizards of the Coast\MTGA",
        "E:\Program Files\Wizards of the Coast\MTGA",
        "C:\Program Files (x86)\Steam\steamapps\common\MTGA",
        "C:\Program Files\Steam\steamapps\common\MTGA",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\MTGA",
        "${env:ProgramFiles}\Steam\steamapps\common\MTGA",
        "C:\SteamLibrary\steamapps\common\MTGA",
        "D:\SteamLibrary\steamapps\common\MTGA",
        "E:\SteamLibrary\steamapps\common\MTGA",
        "F:\SteamLibrary\steamapps\common\MTGA",
        "D:\Steam\steamapps\common\MTGA",
        "E:\Steam\steamapps\common\MTGA"
    )
    foreach ($path in $commonPaths) {
        try {
            if (Test-Path (Join-Path $path "MTGA.exe")) { return $path }
        } catch {}
    }

    # Check Epic Games manifests
    $manifestDir = Join-Path $env:ProgramData "Epic\EpicGamesLauncher\Data\Manifests"
    if (Test-Path $manifestDir) {
        Get-ChildItem $manifestDir -Filter "*.item" | ForEach-Object {
            try {
                $manifest = Get-Content $_.FullName | ConvertFrom-Json
                if ($manifest.DisplayName -like "*Magic*Arena*" -or $manifest.AppName -like "*MTGA*") {
                    $mtgaPath = $manifest.InstallLocation
                    if (Test-Path (Join-Path $mtgaPath "MTGA.exe")) { return $mtgaPath }
                }
            } catch {}
        }
    }

    return $null
}

$mtgaPath = Find-MTGAPath
if (-not $mtgaPath) {
    Write-Host "Could not auto-detect MTGA installation." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Please enter the path to your MTGA folder (the one containing MTGA.exe)." -ForegroundColor White
    Write-Host "Examples:" -ForegroundColor Gray
    Write-Host "  C:\Program Files\Wizards of the Coast\MTGA" -ForegroundColor Gray
    Write-Host "  D:\SteamLibrary\steamapps\common\MTGA" -ForegroundColor Gray
    Write-Host "  C:\Program Files (x86)\Steam\steamapps\common\MTGA" -ForegroundColor Gray
    Write-Host ""
    $mtgaPath = Read-Host "MTGA path"
    $mtgaPath = $mtgaPath.Trim('"').Trim("'").Trim()
    if (-not $mtgaPath -or -not (Test-Path (Join-Path $mtgaPath "MTGA.exe"))) {
        Write-Host "MTGA.exe not found at '$mtgaPath'. Aborting." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Found MTGA at: $mtgaPath" -ForegroundColor Green

# Step 2: Check if MTGA is running
$mtgaProcess = Get-Process -Name "MTGA" -ErrorAction SilentlyContinue
if ($mtgaProcess) {
    Write-Host "MTGA is currently running. Please close it before installing." -ForegroundColor Red
    exit 1
}

# Step 3: Install or upgrade BepInEx 5
#
# Checking only whether winhttp.dll exists is not enough. Arena moved to
# Unity 6.3 in August 2026, and BepInEx 5.4.23.2 crashes the game on startup
# under that engine. Anyone who already had the mod has that version, so
# "already installed" would leave them with a game that will not launch --
# which is exactly the case this script has to fix. Compare the version.
$doorstop = Join-Path $mtgaPath "winhttp.dll"
$bepinexCore = Join-Path $mtgaPath "BepInEx\core\BepInEx.dll"

$installedVersion = $null
if (Test-Path $bepinexCore) {
    try { $installedVersion = [version](Get-Item $bepinexCore).VersionInfo.FileVersion } catch { }
}
$requiredVersion = [version]$bepinexTag.TrimStart("v")

$needsBepInEx = (-not (Test-Path $doorstop)) -or
                ($null -eq $installedVersion) -or
                ($installedVersion -lt $requiredVersion)

if ($needsBepInEx) {
    if ($installedVersion) {
        Write-Host "Upgrading BepInEx $installedVersion -> $requiredVersion..." -ForegroundColor Yellow
        Write-Host "  (5.4.23.2 crashes Arena on Unity 6.3; your plugins and settings are kept.)" -ForegroundColor Gray
    } else {
        Write-Host "Installing BepInEx 5..." -ForegroundColor Yellow
    }

    $bepinexUrl = "https://github.com/$bepinexRepo/releases/download/$bepinexTag/BepInEx_win_x64_$($bepinexTag.TrimStart('v')).zip"
    $zipPath = Join-Path $env:TEMP "bepinex.zip"

    Write-Host "  Downloading from $bepinexUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $bepinexUrl -OutFile $zipPath -UseBasicParsing

    Write-Host "  Extracting to $mtgaPath"
    Expand-Archive -Path $zipPath -DestinationPath $mtgaPath -Force
    Remove-Item $zipPath -Force

    Write-Host "  BepInEx 5 installed." -ForegroundColor Green
} else {
    Write-Host "BepInEx $installedVersion already up to date." -ForegroundColor Green
}

# Step 4: Download latest plugin release
Write-Host "Fetching latest plugin release..." -ForegroundColor Yellow

$releaseUrl = "https://api.github.com/repos/$repo/releases/latest"
try {
    $release = Invoke-RestMethod -Uri $releaseUrl -UseBasicParsing
    $dllAsset = $release.assets | Where-Object { $_.name -eq "MTGAEnhancementSuite.dll" }
    $configAsset = $release.assets | Where-Object { $_.name -eq "config.json" }
    $bootstrapperAsset = $release.assets | Where-Object { $_.name -eq "MTGAESBootstrapper.dll" }
    $manifestAsset = $release.assets | Where-Object { $_.name -eq "manifest.json" }
    $iconsAsset = $release.assets | Where-Object { $_.name -eq "icons.zip" }

    if (-not $dllAsset) {
        Write-Host "No MTGAEnhancementSuite.dll found in latest release. Aborting." -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "Could not fetch release info: $_" -ForegroundColor Red
    exit 1
}

$pluginPath = Join-Path $mtgaPath $pluginDir
if (-not (Test-Path $pluginPath)) {
    New-Item -ItemType Directory -Path $pluginPath -Force | Out-Null
}

# Download manifest and verify signature if available
$manifest = $null
if ($manifestAsset) {
    Write-Host "  Downloading manifest..."
    $manifestPath = Join-Path $env:TEMP "mtgaes_manifest.json"
    Invoke-WebRequest -Uri $manifestAsset.browser_download_url -OutFile $manifestPath -UseBasicParsing
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    Write-Host "  Manifest downloaded (v$($manifest.version))." -ForegroundColor Green
} else {
    Write-Host "  No manifest.json in release — skipping integrity verification." -ForegroundColor Yellow
}

function Get-FileSHA256($path) {
    $hash = Get-FileHash -Path $path -Algorithm SHA256
    return $hash.Hash.ToLower()
}

function Test-FileHash($path, $expectedHash) {
    if (-not $expectedHash) { return $true }
    $actual = Get-FileSHA256 $path
    if ($actual -ne $expectedHash.ToLower()) {
        Write-Host "  HASH MISMATCH for $(Split-Path $path -Leaf)!" -ForegroundColor Red
        Write-Host "    Expected: $expectedHash" -ForegroundColor Red
        Write-Host "    Got:      $actual" -ForegroundColor Red
        return $false
    }
    return $true
}

# Download DLL
$dllPath = Join-Path $pluginPath "MTGAEnhancementSuite.dll"
Write-Host "  Downloading plugin DLL..."
Invoke-WebRequest -Uri $dllAsset.browser_download_url -OutFile $dllPath -UseBasicParsing
if ($manifest) {
    $expectedHash = $manifest.files.'MTGAEnhancementSuite.dll'
    if (-not (Test-FileHash $dllPath $expectedHash)) {
        Remove-Item $dllPath -Force -ErrorAction SilentlyContinue
        Write-Host "DLL integrity check failed. Aborting." -ForegroundColor Red
        exit 1
    }
    Write-Host "  DLL hash verified." -ForegroundColor Green
}
Write-Host "  Plugin DLL installed." -ForegroundColor Green

# Download config.json if available and not already present
$configPath = Join-Path $pluginPath "config.json"
if ($configAsset -and -not (Test-Path $configPath)) {
    Write-Host "  Downloading config.json..."
    Invoke-WebRequest -Uri $configAsset.browser_download_url -OutFile $configPath -UseBasicParsing
    if ($manifest) {
        $expectedHash = $manifest.files.'config.json'
        if (-not (Test-FileHash $configPath $expectedHash)) {
            Remove-Item $configPath -Force -ErrorAction SilentlyContinue
            Write-Host "Config integrity check failed. Aborting." -ForegroundColor Red
            exit 1
        }
        Write-Host "  Config hash verified." -ForegroundColor Green
    }
    Write-Host "  Config installed." -ForegroundColor Green
} elseif (Test-Path $configPath) {
    Write-Host "  Config already exists, skipping." -ForegroundColor Green
}

# Download Bootstrapper DLL (handles auto-updates on launch)
if ($bootstrapperAsset) {
    $bootstrapperPath = Join-Path $pluginPath "MTGAESBootstrapper.dll"
    Write-Host "  Downloading bootstrapper..."
    Invoke-WebRequest -Uri $bootstrapperAsset.browser_download_url -OutFile $bootstrapperPath -UseBasicParsing
    if ($manifest) {
        $expectedHash = $manifest.files.'MTGAESBootstrapper.dll'
        if (-not (Test-FileHash $bootstrapperPath $expectedHash)) {
            Remove-Item $bootstrapperPath -Force -ErrorAction SilentlyContinue
            Write-Host "Bootstrapper integrity check failed. Aborting." -ForegroundColor Red
            exit 1
        }
        Write-Host "  Bootstrapper hash verified." -ForegroundColor Green
    }
    Write-Host "  Bootstrapper installed." -ForegroundColor Green
}

# Download icons bundle (optional — falls back to vector glyphs if absent)
if ($iconsAsset) {
    $iconsZipPath = Join-Path $env:TEMP "mtgaes_icons.zip"
    $iconsDestPath = Join-Path $pluginPath "icons"
    Write-Host "  Downloading icons..."
    Invoke-WebRequest -Uri $iconsAsset.browser_download_url -OutFile $iconsZipPath -UseBasicParsing
    if (-not (Test-Path $iconsDestPath)) {
        New-Item -ItemType Directory -Path $iconsDestPath -Force | Out-Null
    }
    # -Force overwrites existing icons (e.g. a previous install's set)
    Expand-Archive -Path $iconsZipPath -DestinationPath $iconsDestPath -Force
    Remove-Item $iconsZipPath -Force
    Write-Host "  Icons installed." -ForegroundColor Green
}

# Step 5: Register URL scheme
Write-Host "Registering mtgaes:// URL scheme..." -ForegroundColor Yellow
$mtgaExe = Join-Path $mtgaPath "MTGA.exe"
$regPath = "HKCU:\Software\Classes\mtgaes"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "(Default)" -Value "URL:MTGA Enhancement Suite"
New-ItemProperty -Path $regPath -Name "URL Protocol" -Value "" -Force | Out-Null
$cmdPath = Join-Path $regPath "shell\open\command"
New-Item -Path $cmdPath -Force | Out-Null
Set-ItemProperty -Path $cmdPath -Name "(Default)" -Value "`"$mtgaExe`" `"%1`""
Write-Host "  URL scheme registered." -ForegroundColor Green

# Done
Write-Host ""
Write-Host "=== Installation Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Launch MTGA normally"
Write-Host "  2. Look for the 'MTGA+' tab in the top navigation bar"
Write-Host "  3. First launch may take a moment while BepInEx initializes"
Write-Host ""
Write-Host "To uninstall, delete:" -ForegroundColor Gray
Write-Host "  $pluginPath"
Write-Host "  To fully remove BepInEx: delete winhttp.dll, doorstop_config.ini, and BepInEx/ from $mtgaPath"
Write-Host ""
