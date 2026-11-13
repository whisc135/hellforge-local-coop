#Requires -Version 5.1
# Fast checks: installer scripts parse, FEATURES / Apply hashes look consistent.
[CmdletBinding()]
param(
  [string]$RepoRoot = ""
)

$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)

function Ok([string]$m) { Write-Host "[+] $m" -ForegroundColor Green }
function Fail([string]$m) { throw $m }

$apply = Join-Path $RepoRoot 'install\Apply-LocalCoop.ps1'
$install = Join-Path $RepoRoot 'install\INSTALL.cmd'
$features = Join-Path $RepoRoot 'FEATURES.txt'
$lock = Join-Path $RepoRoot 'UPSTREAM.lock'

foreach ($p in @($apply, $install, $features, $lock)) {
  if (-not (Test-Path $p)) { Fail "Missing required file: $p" }
}
Ok "Required files present"

$tokens = $null
$errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($apply, [ref]$tokens, [ref]$errs)
if ($errs -and $errs.Count -gt 0) {
  Fail ("Apply-LocalCoop.ps1 parse errors: " + ($errs | ForEach-Object { $_.ToString() } | Select-Object -First 5) -join '; ')
}
if (-not $ast) { Fail "Apply-LocalCoop.ps1 produced no AST" }
Ok "Apply-LocalCoop.ps1 parses"

$applyText = Get-Content $apply -Raw
$expect = @{}
foreach ($name in @('expectExe', 'expectDll', 'expectWrapper', 'expectSpacewar', 'expectValve')) {
  if ($applyText -notmatch ("\`$$name\s*=\s*'([A-Fa-f0-9]{64})'")) {
    Fail "Apply-LocalCoop.ps1 missing $name SHA-256"
  }
  $expect[$name] = $Matches[1].ToUpperInvariant()
}
Ok "Apply-LocalCoop.ps1 has five SHA-256 pins"
$valve = Join-Path $RepoRoot 'install\steam_api64.valve.dll'
if (-not (Test-Path $valve)) { Fail "Missing install\steam_api64.valve.dll" }
$valveHash = (Get-FileHash -Algorithm SHA256 $valve).Hash.ToUpperInvariant()
if ($valveHash -ne $expect['expectValve']) {
  Fail "install\steam_api64.valve.dll hash ($valveHash) != expectValve ($($expect['expectValve']))"
}
Ok "steam_api64.valve.dll matches expectValve"

$feat = Get-Content $features -Raw
$featHashes = [regex]::Matches($feat, '(?im)SHA256[^\r\n]*=\s*([A-Fa-f0-9]{64})') |
  ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() }
if ($featHashes.Count -lt 3) {
  Fail "FEATURES.txt should list at least 3 SHA256 pins (exe, PartyWin, wrapper)"
}
Ok ("FEATURES.txt lists " + $featHashes.Count + " SHA-256 pin(s)")

# FEATURES pins for exe/dll/wrapper should match Apply when present
$map = @{
  'HellForge.LocalBackend.real.exe' = 'expectExe'
  'PartyWin.dll' = 'expectDll'
  'HellForge.LocalBackend.wrapper.exe' = 'expectWrapper'
}
foreach ($key in $map.Keys) {
  if ($feat -match [regex]::Escape($key)) {
    # find SHA after that name within a short window
    $m = [regex]::Match($feat, [regex]::Escape($key) + '[\s\S]{0,120}?([A-Fa-f0-9]{64})')
    if ($m.Success) {
      $h = $m.Groups[1].Value.ToUpperInvariant()
      $pin = $expect[$map[$key]]
      if ($h -ne $pin) {
        Fail "$key hash in FEATURES ($h) != Apply $($map[$key]) ($pin)"
      }
      Ok "$key hash matches Apply+$($map[$key])"
    }
  }
}

foreach ($key in @('HELLFORGE_UPSTREAM', 'HELLFORGE_REF', 'PATCH_VERSION')) {
  if (-not (Select-String -Path $lock -Pattern ("^\s*" + $key + "\s*=") -Quiet)) {
    Fail "UPSTREAM.lock missing $key"
  }
}
Ok "UPSTREAM.lock keys present"
Ok "Validate OK"
