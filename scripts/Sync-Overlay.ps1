#Requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$HellForgeRoot,
  [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$HellForgeRoot = [IO.Path]::GetFullPath($HellForgeRoot)
$lb = Join-Path $HellForgeRoot 'src\HellForge.LocalBackend'
$dest = Join-Path $RepoRoot 'src\overlay\HellForge.LocalBackend'
if (-not (Test-Path $lb)) { throw "Not a HellForge tree: $HellForgeRoot" }

$files = @(
  'HellForge.LocalBackend.csproj',
  'LocalBackend.cs',
  'LocalBackend.Requests.cs',
  'MissionService.cs',
  'MissionEndRelay.cs',
  'OwnershipFilter.cs',
  'PlayFabService.cs',
  'ProfileStore.cs',
  'Program.cs',
  'Purchases.cs',
  'StoreFronts.cs',
  'startup-fixtures.json',
  'mission-reward-entries.json',
  'Multiplayer\MissionEndShare.cs',
  'Multiplayer\MultiplayerHost.cs'
)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($rel in $files) {
  $from = Join-Path $lb $rel
  if (-not (Test-Path $from)) { Write-Warning "skip missing $rel"; continue }
  $to = Join-Path $dest $rel
  New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
  Copy-Item $from $to -Force
  Write-Host "synced $rel"
}

$testsFrom = Join-Path $HellForgeRoot 'tests\HellForge.LocalBackend.Tests\AccountSyncTests.cs'
$testsTo = Join-Path $RepoRoot 'src\overlay\HellForge.LocalBackend.Tests\AccountSyncTests.cs'
if (Test-Path $testsFrom) {
  New-Item -ItemType Directory -Force -Path (Split-Path $testsTo) | Out-Null
  Copy-Item $testsFrom $testsTo -Force
  Write-Host 'synced AccountSyncTests.cs'
}
Write-Host "Overlay updated under $dest"
