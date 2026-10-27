#!/usr/bin/env pwsh
# Infra/scripts/lock.ps1 <service> [-UpgradePackage <name>[,<name>]]
#
# Compile a fully-hashed requirements.txt from requirements.in INSIDE the digest-pinned
# base image — resolution is python-version- and platform-specific, and there is no
# uv/pip-tools on this host — then PROVE THE LOCK INSTALLS in that same image.
#
# A lock that compiles is not a lock that builds: llvmlite 0.36.0 compiles fine and has
# no py3.10+ wheel (spec §6.2). So the install step is not optional — it is the point.
#
# PyPI is the ONLY index. torch/torchaudio enter via direct hashed download.pytorch.org
# wheel URLs in requirements.in — never --extra-index-url (spec §6.2 constraint 1).
#
# uv keeps every pin already in requirements.txt. To take a fix, a transitive security bump
# included, name the package with -UpgradePackage: only it, and whatever it forces, moves.
# A comma list (certifi,filelock) splits only when called from a PowerShell prompt.
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('minilm-l6','mpnet-base-v2')]
  [string]$Service,

  # Names go into a sh -c line, so only package-name characters pass.
  [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
  [string[]]$UpgradePackage = @()
)
$ErrorActionPreference = 'Stop'

$UV        = '0.11.28'
$InfraRoot = Split-Path -Parent $PSScriptRoot   # Infra/

# Digest-pinned base, read from the service's first FROM line (spec §6.1). A copy here went
# stale twice: Dependabot and refresh-pins.ps1 rewrite the Dockerfiles only.
$dockerfile = Join-Path $InfraRoot "Dockerfile.$Service"
$from = Select-String -Path $dockerfile -Pattern '^FROM\s+(\S+@sha256:[0-9a-f]{64})' | Select-Object -First 1
if (-not $from) { throw "No digest-pinned FROM line in $dockerfile" }
$base   = $from.Matches[0].Groups[1].Value
$svcDir = Join-Path $InfraRoot $Service
if (-not (Test-Path (Join-Path $svcDir 'requirements.in'))) {
  throw "No requirements.in in $svcDir"
}

# A service may declare knowingly-inconsistent constraints via overrides.txt
# (spec §6.2 constraint 3). Pass it only when present.
$overrides = ''
if (Test-Path (Join-Path $svcDir 'overrides.txt')) { $overrides = '--overrides overrides.txt' }

# uv treats a name that isn't in the lock as a no-op, so a typo would look like a fix.
$lock = Join-Path $svcDir 'requirements.txt'
foreach ($name in $UpgradePackage) {
  if (-not (Select-String -Path $lock -Pattern "^$([regex]::Escape($name))==" -Quiet)) {
    throw "$name is not pinned in $lock"
  }
}
$upgrade = ($UpgradePackage | ForEach-Object { "--upgrade-package $_" }) -join ' '

$compile = "uv pip compile requirements.in --generate-hashes $overrides $upgrade -o requirements.txt"
$install = 'pip install --no-cache-dir --require-hashes -r requirements.txt'
$script  = "set -e; pip install --no-cache-dir uv==$UV; $compile; $install"

Write-Host "==> lock $Service" -ForegroundColor Cyan
Write-Host "    base $base"
if ($UpgradePackage) { Write-Host "    upgrade $($UpgradePackage -join ', ')" }
docker run --rm -e PIP_BREAK_SYSTEM_PACKAGES=1 -v "${svcDir}:/w" -w /w $base sh -c $script
if ($LASTEXITCODE -ne 0) { throw "lock/build FAILED for $Service (exit $LASTEXITCODE)" }
Write-Host "==> $Service — requirements.txt regenerated and proven to install" -ForegroundColor Green
