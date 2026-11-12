#Requires -Version 5.1
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI unless a UTF-8 BOM is present.
# Cyrillic inside quoted strings then turns into stray quotes and the script will not parse.
[CmdletBinding()]
param(
  [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
$expectExe = '3CC72BC7EDA3F70160821A95D3CEE108114B32134A19696FD476A9D4FE6FEF23'
$expectDll = '5F1012F8EB3A285ECABFABD6C618EC54769732CF342447F5B8DF36CA486A6417'
$expectWrapper = '1D93A4290191ECB62176C7BF2970CEE5270EEA60C6CD35DA7DFC9F2D05E3F853'
$expectSpacewar = '66F50AAB82AA728C16F9410F211CE53EF6264D9842BBB171A56AD4AC9C1571AC'

function Write-Step([string]$m) { Write-Host "[*] $m" -ForegroundColor Cyan }
function Write-Ok([string]$m) { Write-Host "[+] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[!] $m" -ForegroundColor Yellow }
function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -Path $path).Hash }
function Set-EngineHash([string]$json, [string]$key, [string]$hash) {
  $pattern = '"' + [regex]::Escape($key) + '"\s*:\s*"[A-Fa-f0-9]+"'
  $replacement = '"' + $key + '":  "' + $hash + '"'
  $next = [regex]::Replace($json, $pattern, $replacement, 1)
  if ($next -eq $json) { Write-Warn "engine.json has no $key entry. The session does not check that file." }
  return $next
}

$patchExe = Join-Path $PSScriptRoot 'HellForge.LocalBackend.real.exe'
$patchDll = Join-Path $PSScriptRoot 'PartyWin.dll'
$patchWrapper = Join-Path $PSScriptRoot 'HellForge.LocalBackend.wrapper.exe'
$patchSpacewar = Join-Path $PSScriptRoot 'steam_api64.spacewar.dll'
$patchSkip = Join-Path $PSScriptRoot 'skip-steam-check.enabled'
$patchOwnership = Join-Path $PSScriptRoot 'Test-SteamOwnership.ps1'
$patchPortableCmd = Join-Path $PSScriptRoot 'HellForge-Portable.cmd'

foreach ($need in @($patchExe, $patchDll, $patchWrapper, $patchSpacewar)) {
  if (-not (Test-Path $need)) {
    throw "Patch files are missing next to this script. Unzip the archive fully and run Apply-LocalCoop.ps1 from that folder."
  }
}
if ((Get-Sha256 $patchExe) -ne $expectExe) { throw "HellForge.LocalBackend.real.exe in the archive is damaged." }
if ((Get-Sha256 $patchDll) -ne $expectDll) { throw "PartyWin.dll in the archive is damaged." }
if ((Get-Sha256 $patchWrapper) -ne $expectWrapper) { throw "HellForge.LocalBackend.wrapper.exe in the archive is damaged." }
if ((Get-Sha256 $patchSpacewar) -ne $expectSpacewar) { throw "steam_api64.spacewar.dll in the archive is damaged." }

function Find-InstallRoot([string]$Given) {
  if ($Given) {
    $full = [IO.Path]::GetFullPath($Given)
    if (Test-Path (Join-Path $full 'HellForge.exe')) { return $full }
    throw "No HellForge.exe in this folder: $full"
  }
  $dir = $PSScriptRoot
  for ($i = 0; $i -lt 3; $i++) {
    if (Test-Path (Join-Path $dir 'HellForge.exe')) { return $dir }
    $parent = Split-Path -Parent $dir
    if (-not $parent -or $parent -eq $dir) { break }
    $dir = $parent
  }
  Write-Step "HellForge.exe was not next to the archive. Pick the folder that contains it."
  Add-Type -AssemblyName System.Windows.Forms
  $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
  $dialog.Description = "Select the folder that contains HellForge.exe"
  $dialog.ShowNewFolderButton = $false
  while ($true) {
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK -or -not $dialog.SelectedPath) {
      throw "No folder selected."
    }
    $picked = [IO.Path]::GetFullPath($dialog.SelectedPath)
    if (Test-Path (Join-Path $picked 'HellForge.exe')) { return $picked }
    Write-Warn "No HellForge.exe in $picked. Need the install root, not game\ and not app\."
  }
}

$root = Find-InstallRoot $InstallRoot
$backend = Join-Path $root 'app\backend'
$real = Join-Path $backend 'HellForge.LocalBackend.real.exe'
$wrapper = Join-Path $backend 'HellForge.LocalBackend.exe'
$spacewar = Join-Path $backend 'steam_api64.spacewar.dll'
$shims = Join-Path $root 'app\sdk-shims'
$shim = Join-Path $shims 'PartyWin.dll'
$buildJson = Join-Path $shims 'build.json'
$engineJson = Join-Path $root 'app\engine.json'
$gameDll = Join-Path $root 'game\bin\PartyWin.dll'
$scripts = Join-Path $root 'app\scripts'

Write-Step "Game folder: $root"
if (-not (Test-Path (Join-Path $root 'HellForge.exe'))) {
  throw "No HellForge.exe in this folder. Point at the install root, not game\ and not app\."
}
if (-not (Test-Path $backend)) { throw "Missing app\backend. This is not a HellForge install." }
if (-not (Test-Path $shim) -or -not (Test-Path $buildJson)) {
  throw "Missing app\sdk-shims\PartyWin.dll or build.json. Need a HellForge install that already has sdk-shims."
}

$busy = Get-Process -ErrorAction SilentlyContinue |
  Where-Object { $_.ProcessName -match '^(Helldivers2|helldivers2|HellForge\.LocalBackend)$' }
if ($busy) {
  throw "Close the game and HellForge first. Still running: $($busy.ProcessName -join ', ')"
}

# Stock 0.2.0: one large LocalBackend.exe, no .real. Portable 0.4: tiny wrapper + .real.
$hasReal = Test-Path $real
$hasWrapperPath = Test-Path $wrapper
$wrapperIsSmall = $hasWrapperPath -and ((Get-Item $wrapper).Length -lt 1MB)
$stockIsBackend = $hasWrapperPath -and -not $wrapperIsSmall

if (-not $hasReal -and -not $hasWrapperPath) {
  throw "Missing app\backend\HellForge.LocalBackend.exe. Cannot install."
}
if (-not $hasReal -and $wrapperIsSmall) {
  throw "HellForge.LocalBackend.exe is already a tiny wrapper, but .real.exe is missing. Restore the backend first."
}

$spacewarOk = (Test-Path $spacewar) -and ((Get-Sha256 $spacewar) -eq $expectSpacewar)
$already = $hasReal -and $wrapperIsSmall -and
  ((Get-Sha256 $real) -eq $expectExe) -and
  ((Get-Sha256 $wrapper) -eq $expectWrapper) -and
  ((Get-Sha256 $shim) -eq $expectDll) -and
  $spacewarOk
if ($already) {
  $bj = Get-Content $buildJson -Raw | ConvertFrom-Json
  if ([string]$bj.standIns.'PartyWin.dll' -eq $expectDll) {
    Write-Ok "Patch already installed. Nothing to copy."
    exit 0
  }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $backend "backups\party-voice-$stamp"
New-Item -ItemType Directory -Force -Path $backup | Out-Null
if ($hasReal) { Copy-Item $real (Join-Path $backup 'HellForge.LocalBackend.real.exe') -Force }
if ($hasWrapperPath) { Copy-Item $wrapper (Join-Path $backup 'HellForge.LocalBackend.exe') -Force }
if (Test-Path $spacewar) { Copy-Item $spacewar (Join-Path $backup 'steam_api64.spacewar.dll') -Force }
Copy-Item $shim (Join-Path $backup 'PartyWin.dll') -Force
Copy-Item $buildJson (Join-Path $backup 'build.json') -Force
if (Test-Path $engineJson) { Copy-Item $engineJson (Join-Path $backup 'engine.json') -Force }
$rollback = @"
Files from before the party-voice install.
Copy them back to undo:
  HellForge.LocalBackend.real.exe -> app\backend\   (if present in this backup)
  HellForge.LocalBackend.exe -> app\backend\
  steam_api64.spacewar.dll -> app\backend\          (if present)
  PartyWin.dll -> app\sdk-shims\
  build.json -> app\sdk-shims\
  engine.json -> app\
"@
Set-Content -Path (Join-Path $backup 'ROLLBACK.txt') -Encoding ASCII -Value $rollback
Write-Ok "Old files: $backup"

if ($stockIsBackend) {
  Write-Step "Stock backend layout (no .real). Keeping a copy, then installing wrapper + patched real."
  if (-not $hasReal) {
    Copy-Item $wrapper (Join-Path $backup 'HellForge.LocalBackend.stock.exe') -Force
  }
}

Copy-Item $patchExe $real -Force
Copy-Item $patchWrapper $wrapper -Force
Copy-Item $patchSpacewar $spacewar -Force
Set-Content -Path (Join-Path $backend 'steam_appid.txt') -Value '480' -Encoding ASCII
Copy-Item $patchDll $shim -Force
Write-Ok "Backend real + Spacewar wrapper + voice PartyWin are in place."

if ((Test-Path $patchSkip) -and (Test-Path $scripts)) {
  Copy-Item $patchSkip (Join-Path $scripts 'skip-steam-check.enabled') -Force
  if (Test-Path $patchOwnership) {
    Copy-Item $patchOwnership (Join-Path $scripts 'Test-SteamOwnership.ps1') -Force
  }
  Write-Ok "Ownership skip installed under app\scripts."
}
if (Test-Path $patchPortableCmd) {
  Copy-Item $patchPortableCmd (Join-Path $root 'HellForge-Portable.cmd') -Force
  Write-Ok "HellForge-Portable.cmd written (SteamAppId 480). Prefer this over bare HellForge.exe without HD2."
}

$bj = Get-Content $buildJson -Raw | ConvertFrom-Json
$originalPin = [string]$bj.originals.'PartyWin.dll'
$bj.standIns.'PartyWin.dll' = $expectDll
($bj | ConvertTo-Json -Depth 6) | Set-Content -Path $buildJson -Encoding utf8
if ([string]((Get-Content $buildJson -Raw | ConvertFrom-Json).standIns.'PartyWin.dll') -ne $expectDll) {
  throw "Could not write the PartyWin.dll hash into build.json."
}
Write-Ok "build.json stand-in hash updated."

if ($originalPin -and (Test-Path $gameDll)) {
  $gameHash = Get-Sha256 $gameDll
  if ($gameHash -ne $originalPin) {
    $saved = Join-Path $root 'temp\sdk-shim-originals\PartyWin.dll'
    if ((Test-Path $saved) -and (Get-Sha256 $saved) -eq $originalPin) {
      Copy-Item $saved $gameDll -Force
      Write-Ok "game\bin still had the last session stand-in. Restored the original PartyWin.dll."
    } else {
      Write-Warn "game\bin\PartyWin.dll hash is not the pin in build.json ($originalPin). If PLAY refuses the stand-in, restore game\bin\PartyWin.dll from a clean install of this same HellForge build."
    }
  }
}

if (Test-Path $engineJson) {
  $ej = Get-Content $engineJson -Raw
  $ej = Set-EngineHash $ej 'backend/HellForge.LocalBackend.exe' $expectWrapper
  $ej = Set-EngineHash $ej 'sdk-shims/PartyWin.dll' $expectDll
  $ej = Set-EngineHash $ej 'sdk-shims/build.json' (Get-Sha256 $buildJson)
  if ((Test-Path $patchOwnership) -and (Test-Path (Join-Path $scripts 'Test-SteamOwnership.ps1'))) {
    $ej = Set-EngineHash $ej 'scripts/Test-SteamOwnership.ps1' (Get-Sha256 (Join-Path $scripts 'Test-SteamOwnership.ps1'))
  }
  Set-Content -Path $engineJson -Value $ej.TrimEnd() -Encoding utf8
}

Write-Host ""
Write-Ok "Done."
Write-Host "  Start with HellForge-Portable.cmd when you have no HD2 license (Spacewar 480)."
Write-Host "  Voice is the in-game button. Every squad member needs this same archive."
Write-Host "  Rollback: $backup"