#Requires -Version 7.0
[CmdletBinding()]
param(
  [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
  [string]$UpstreamDir = "",
  # When set, reuse an existing .upstream checkout (GitHub Actions cache).
  [switch]$KeepUpstream,
  # Clone/apply overlay only (release pack job already ran tests).
  [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

function Read-Lock([string]$key) {
  $line = Get-Content (Join-Path $RepoRoot "UPSTREAM.lock") |
    Where-Object { $_ -match ("^\s*" + [regex]::Escape($key) + "\s*=") } |
    Select-Object -First 1
  if (-not $line) { throw "UPSTREAM.lock missing $key" }
  ($line -split "=", 2)[1].Trim()
}

$upstreamRepo = Read-Lock "HELLFORGE_UPSTREAM"
$upstreamRef = Read-Lock "HELLFORGE_REF"
if (-not $UpstreamDir) { $UpstreamDir = Join-Path $RepoRoot ".upstream\HellForge" }

Write-Host "==> HellForge $upstreamRepo @$upstreamRef"

$haveGit = Test-Path (Join-Path $UpstreamDir ".git")
if ($haveGit -and $KeepUpstream) {
  Write-Host "==> Reusing cached upstream at $UpstreamDir"
  Push-Location $UpstreamDir
  try {
    git remote set-url origin "https://github.com/$upstreamRepo.git"
    if ($upstreamRef -match "^[0-9a-fA-F]{40}$") {
      git fetch --depth 1 origin $upstreamRef
      git checkout --force $upstreamRef
      git reset --hard $upstreamRef
    } else {
      git fetch --depth 1 origin $upstreamRef
      git checkout --force "origin/$upstreamRef"
      git reset --hard "origin/$upstreamRef"
    }
  } finally { Pop-Location }
} else {
  if (Test-Path $UpstreamDir) { Remove-Item $UpstreamDir -Recurse -Force }
  New-Item -ItemType Directory -Force -Path (Split-Path $UpstreamDir) | Out-Null
  if ($upstreamRef -match "^[0-9a-fA-F]{40}$") {
    git clone "https://github.com/$upstreamRepo.git" $UpstreamDir
    Push-Location $UpstreamDir
    git checkout --force $upstreamRef
    Pop-Location
  } else {
    git clone --depth 1 --branch $upstreamRef "https://github.com/$upstreamRepo.git" $UpstreamDir
  }
}

$overlay = Join-Path $RepoRoot "src\overlay\HellForge.LocalBackend"
$dest = Join-Path $UpstreamDir "src\HellForge.LocalBackend"
Write-Host "==> Applying LocalBackend overlay"
Copy-Item -Path (Join-Path $overlay "*") -Destination $dest -Recurse -Force

$testsOverlay = Join-Path $RepoRoot "src\overlay\HellForge.LocalBackend.Tests"
$testsDest = Join-Path $UpstreamDir "tests\HellForge.LocalBackend.Tests"
if (Test-Path $testsOverlay) {
  Copy-Item -Path (Join-Path $testsOverlay "*") -Destination $testsDest -Recurse -Force
  Write-Host "==> Applied LocalBackend.Tests overlay"
}

if ($SkipTests) {
  Write-Host "==> SkipTests: overlay ready at $UpstreamDir"
  return
}

$testProj = Join-Path $UpstreamDir "tests\HellForge.LocalBackend.Tests\HellForge.LocalBackend.Tests.csproj"
Write-Host "==> dotnet run LocalBackend tests"
dotnet run --project $testProj -c Release --nologo -v q -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "LocalBackend tests failed with exit $LASTEXITCODE" }
Write-Host "==> OK"
