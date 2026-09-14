<#
.SYNOPSIS
  Compila e copia la build in BepInEx_Dev\plugins\MTGAPlus.
  Non tocca mai BepInEx\ (installazione ufficiale).
#>
[CmdletBinding()]
param([string]$MTGARoot, [switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not $MTGARoot) {
    $candidates = @(
        "$env:ProgramFiles\Wizards of the Coast\MTGA",
        "${env:ProgramFiles(x86)}\Wizards of the Coast\MTGA",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\MTGA",
        "$env:ProgramFiles\Steam\steamapps\common\MTGA",
        "C:\SteamLibrary\steamapps\common\MTGA",
        "D:\SteamLibrary\steamapps\common\MTGA"
    )
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'doorstop_config.ini')) { $MTGARoot = $c; break } }
}
if (-not $MTGARoot) { throw "Installazione MTGA non trovata." }

$proc = Get-Process -Name 'MTGA' -ErrorAction SilentlyContinue
if ($proc) { throw "MTGA e' in esecuzione (PID $($proc.Id -join ', ')). Chiudi il gioco." }

if (-not $SkipBuild) {
    Write-Host "Build..." -ForegroundColor DarkGray
    & dotnet build (Join-Path $repo 'MTGAEnhancementSuite.sln') -c Release -v quiet -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build fallita." }
}

$dest = Join-Path $MTGARoot 'BepInEx_Dev\plugins\MTGAPlus'
if (-not (Test-Path $dest)) { throw "Cartella dev mancante: $dest" }

# BepInEx tronca LogOutput.log a ogni avvio del gioco: archiviamo quello
# della sessione precedente prima che vada perso.
$log = Join-Path $MTGARoot 'BepInEx_Dev\LogOutput.log'
if (Test-Path $log) {
    $archive = Join-Path $MTGARoot 'BepInEx_Dev\logs'
    if (-not (Test-Path $archive)) { New-Item -ItemType Directory -Path $archive | Out-Null }
    $stamp = (Get-Item $log).LastWriteTime.ToString('yyyyMMdd-HHmmss')
    Copy-Item $log (Join-Path $archive "LogOutput-$stamp.log")
    Write-Host "  Log archiviato: logs\LogOutput-$stamp.log" -ForegroundColor DarkGray
}

# Anche Arena ruota il suo log: a ogni avvio Player.log diventa Player-prev.log
# e quello di prima sparisce. Due riavvii e la prova di una sessione non c'e'
# piu' (successo davvero l'11/09/2026). Lo archiviamo accanto all'altro, con lo
# stesso criterio: il nome porta la data di ULTIMA SCRITTURA del file, non
# quella di adesso, cosi' due deploy di fila non ne fanno due copie.
$arenaLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Wizards Of The Coast\MTGA\Player.log'
if (Test-Path $arenaLog) {
    try {
        $archive = Join-Path $MTGARoot 'BepInEx_Dev\logs'
        if (-not (Test-Path $archive)) { New-Item -ItemType Directory -Path $archive | Out-Null }
        $item = Get-Item $arenaLog
        $stamp = $item.LastWriteTime.ToString('yyyyMMdd-HHmmss')
        $arenaDest = Join-Path $archive "Player-$stamp.log"
        if (Test-Path $arenaDest) {
            Write-Host "  Player.log gia' archiviato: logs\Player-$stamp.log" -ForegroundColor DarkGray
        } else {
            Copy-Item $arenaLog $arenaDest
            $mb = [math]::Round($item.Length / 1MB, 1)
            Write-Host "  Player.log archiviato: logs\Player-$stamp.log ($mb MB)" -ForegroundColor DarkGray
        }
    } catch {
        # Un log non archiviabile non deve far fallire il deploy: si segnala e si tira dritto.
        Write-Host "  Player.log non archiviato: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  Player.log non trovato in AppData\LocalLow - niente da archiviare" -ForegroundColor DarkGray
}

# Icone: assets/icons e' la sorgente di verita', allineiamo la cartella dev.
$iconSrc = Join-Path $repo 'assets\icons'
if (Test-Path $iconSrc) {
    $iconDst = Join-Path $dest 'icons'
    if (-not (Test-Path $iconDst)) { New-Item -ItemType Directory -Path $iconDst | Out-Null }
    Copy-Item (Join-Path $iconSrc '*.png') $iconDst -Force
}

Copy-Item (Join-Path $repo 'Plugin\bin\Release\MTGAEnhancementSuite.dll')      $dest -Force
Copy-Item (Join-Path $repo 'Bootstrapper\bin\Release\MTGAESBootstrapper.dll')  $dest -Force

Write-Host ""
Write-Host "  Deploy -> $dest" -ForegroundColor Green
Get-ChildItem $dest -Filter *.dll | ForEach-Object {
    Write-Host ("    {0,-32} {1,8:N0} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime.ToString('HH:mm:ss'))
}
Write-Host ""
