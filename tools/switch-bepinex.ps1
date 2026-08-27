<#
.SYNOPSIS
  Sceglie quale installazione BepInEx viene caricata da MTGA.

  MTGA legge una sola riga da doorstop_config.ini ("target_assembly") che indica
  quale cartella caricare. Questo script cambia solo quella riga: nessuna cartella
  viene rinominata o spostata.

    stable -> BepInEx\core\...      (la mod ufficiale installata, default sicuro)
    dev    -> BepInEx_Dev\core\...  (la nostra build di sviluppo)

.EXAMPLE
  .\switch-bepinex.ps1 status
  .\switch-bepinex.ps1 dev
  .\switch-bepinex.ps1 stable
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('dev', 'stable', 'status')]
    [string]$Mode = 'status',

    [string]$MTGARoot
)

$ErrorActionPreference = 'Stop'

function Find-MTGARoot {
    if ($MTGARoot) { return $MTGARoot }
    $candidates = @(
        "$env:ProgramFiles\Wizards of the Coast\MTGA",
        "${env:ProgramFiles(x86)}\Wizards of the Coast\MTGA",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\MTGA",
        "$env:ProgramFiles\Steam\steamapps\common\MTGA",
        "C:\SteamLibrary\steamapps\common\MTGA",
        "D:\SteamLibrary\steamapps\common\MTGA"
    )
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'doorstop_config.ini')) { return $c } }
    throw "Installazione MTGA non trovata. Usa -MTGARoot 'C:\percorso\MTGA'."
}

$root = Find-MTGARoot
$ini  = Join-Path $root 'doorstop_config.ini'
$bak  = "$ini.original.bak"

$targets = @{
    stable = 'BepInEx\core\BepInEx.Preloader.dll'
    dev    = 'BepInEx_Dev\core\BepInEx.Preloader.dll'
}

# --- stato corrente ---
$lines   = Get-Content $ini
$current = ($lines | Where-Object { $_ -match '^\s*target_assembly\s*=' }) -replace '^\s*target_assembly\s*=\s*', ''
if (-not $current) { throw "Riga 'target_assembly' non trovata in $ini" }

$currentName = 'sconosciuto'
foreach ($k in $targets.Keys) { if ($current -eq $targets[$k]) { $currentName = $k } }

if ($Mode -eq 'status') {
    Write-Host ""
    Write-Host "  MTGA      : $root"
    Write-Host "  Attivo    : $currentName" -ForegroundColor Cyan
    Write-Host "  target    : $current"
    Write-Host ""
    foreach ($k in 'stable', 'dev') {
        $exists = Test-Path (Join-Path $root $targets[$k])
        $mark = '   disponibile'
        if (-not $exists) { $mark = '   MANCANTE' }
        Write-Host ("  {0,-7}{1}" -f $k, $mark)
    }
    Write-Host ""
    return
}

# --- controlli di sicurezza ---
$proc = Get-Process -Name 'MTGA' -ErrorAction SilentlyContinue
if ($proc) { throw "MTGA e' in esecuzione (PID $($proc.Id -join ', ')). Chiudi il gioco prima di cambiare." }

$targetRel  = $targets[$Mode]
$targetFull = Join-Path $root $targetRel
if (-not (Test-Path $targetFull)) { throw "Target inesistente: $targetFull" }

if ($currentName -eq $Mode) {
    Write-Host "Gia' su '$Mode'. Nessuna modifica." -ForegroundColor Yellow
    return
}

# --- backup una tantum dell'originale ---
if (-not (Test-Path $bak)) {
    Copy-Item $ini $bak
    Write-Host "Backup creato: $bak" -ForegroundColor DarkGray
}

# --- scrittura ---
$new = $lines -replace '^\s*target_assembly\s*=.*', "target_assembly=$targetRel"
Set-Content -Path $ini -Value $new -Encoding UTF8

Write-Host ""
Write-Host "  $currentName  ->  $Mode" -ForegroundColor Green
Write-Host "  target_assembly=$targetRel"
Write-Host ""
if ($Mode -eq 'dev') { Write-Host "  Per tornare:  .\switch-bepinex.ps1 stable" -ForegroundColor DarkGray }
