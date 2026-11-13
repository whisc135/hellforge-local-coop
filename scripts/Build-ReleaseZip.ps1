#Requires -Version 5.1
# Packs a release zip. PayloadDir must contain the binary files
# (publish LocalBackend AOT, PartyWin stand-in, wrapper, spacewar dll).
# Rewrites SHA pins inside Apply-LocalCoop.ps1 (and FEATURES.txt when present) to match payload.
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$PayloadDir,
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$RepoRoot = "",
  [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$PayloadDir = [IO.Path]::GetFullPath($PayloadDir)
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stage = Join-Path $OutDir ("stage-" + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$required = @(
  'HellForge.LocalBackend.real.exe',
  'HellForge.LocalBackend.wrapper.exe',
  'PartyWin.dll',
  'steam_api64.spacewar.dll',
  'steam_api64.valve.dll'
)
$hashes = @{}
foreach ($name in $required) {
  $p = Join-Path $PayloadDir $name
  if (-not (Test-Path $p)) {
    $fallback = Join-Path $RepoRoot "install\$name"
    if (($name -eq 'steam_api64.valve.dll') -and (Test-Path $fallback)) { $p = $fallback }
    else { throw "Payload missing $name" }
  }
  Copy-Item $p (Join-Path $stage $name) -Force
  $hashes[$name] = (Get-FileHash -Algorithm SHA256 -Path $p).Hash
}

$applySrc = Get-Content (Join-Path $RepoRoot 'install\Apply-LocalCoop.ps1') -Raw
$applySrc = [regex]::Replace($applySrc, "\`$expectExe = '[A-Fa-f0-9]{64}'", "`$expectExe = '$($hashes['HellForge.LocalBackend.real.exe'])'")
$applySrc = [regex]::Replace($applySrc, "\`$expectDll = '[A-Fa-f0-9]{64}'", "`$expectDll = '$($hashes['PartyWin.dll'])'")
$applySrc = [regex]::Replace($applySrc, "\`$expectWrapper = '[A-Fa-f0-9]{64}'", "`$expectWrapper = '$($hashes['HellForge.LocalBackend.wrapper.exe'])'")
$applySrc = [regex]::Replace($applySrc, "\`$expectSpacewar = '[A-Fa-f0-9]{64}'", "`$expectSpacewar = '$($hashes['steam_api64.spacewar.dll'])'")
$applySrc = [regex]::Replace($applySrc, "\`$expectValve = '[A-Fa-f0-9]{64}'", "`$expectValve = '$($hashes['steam_api64.valve.dll'])'")
$utf8bom = New-Object System.Text.UTF8Encoding $true
[IO.File]::WriteAllText((Join-Path $stage 'Apply-LocalCoop.ps1'), $applySrc, $utf8bom)

Copy-Item (Join-Path $RepoRoot 'install\INSTALL.cmd') (Join-Path $stage 'INSTALL.cmd') -Force
Copy-Item (Join-Path $RepoRoot 'install\HellForge-Portable.cmd') (Join-Path $stage 'HellForge-Portable.cmd') -Force
Copy-Item (Join-Path $RepoRoot 'install\skip-steam-check.enabled') (Join-Path $stage 'skip-steam-check.enabled') -Force
Copy-Item (Join-Path $RepoRoot 'install\Test-SteamOwnership.ps1') (Join-Path $stage 'Test-SteamOwnership.ps1') -Force

$feat = Get-Content (Join-Path $RepoRoot 'FEATURES.txt') -Raw
$feat = [regex]::Replace($feat, '(?m)^(Current:\s*)v\d+',('${1}' + $Version))
$feat = [regex]::Replace($feat, '(SHA256 HellForge\.LocalBackend\.real\.exe\s*=\s*\r?\n\s*)[A-Fa-f0-9]{64}',('${1}' + $hashes['HellForge.LocalBackend.real.exe']))
$feat = [regex]::Replace($feat, '(SHA256 PartyWin\.dll\s*=\s*\r?\n\s*)[A-Fa-f0-9]{64}',('${1}' + $hashes['PartyWin.dll']))
$feat = [regex]::Replace($feat, '(SHA256 HellForge\.LocalBackend\.wrapper\.exe\s*=\s*\r?\n\s*)[A-Fa-f0-9]{64}',('${1}' + $hashes['HellForge.LocalBackend.wrapper.exe']))
$feat = [regex]::Replace($feat, '(SHA256 steam_api64\.valve\.dll\s*=\s*\r?\n\s*)[A-Fa-f0-9]{64}',('${1}' + $hashes['steam_api64.valve.dll']))
[IO.File]::WriteAllText((Join-Path $stage 'FEATURES.txt'), $feat, $utf8bom)

$how = @"
HellForge Local Co-op $Version
=================================
1. Close the game and HellForge.
2. Run INSTALL.cmd and pick the folder with HellForge.exe if asked.
3. Prefer HellForge-Portable.cmd when using the Spacewar peer-link setup.
4. Every squad member needs this same $Version.

Source and docs: the GitHub repository that published this release.
"@
[IO.File]::WriteAllText((Join-Path $stage 'HOW-TO.txt'), $how, $utf8bom)

$zip = Join-Path $OutDir ("HellForge-LocalCoop_$Version.zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash
Write-Host "Wrote $zip"
Write-Host "SHA256 $hash"
Set-Content -Path ($zip + '.sha256') -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ASCII
