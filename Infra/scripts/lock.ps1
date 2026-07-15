#!/usr/bin/env pwsh
# Infra/scripts/lock.ps1 <service>
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
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('chatterbox','qwen3','voxcpm2','minilm-l6','mpnet-base-v2')]
  [string]$Service
)
$ErrorActionPreference = 'Stop'

$UV        = '0.11.28'
$InfraRoot = Split-Path -Parent $PSScriptRoot   # Infra/

# Digest-pinned base per service — MUST match the FROM lines (spec §6.1).
$Bases = @{
  'chatterbox'    = 'pytorch/pytorch:2.6.0-cuda12.6-cudnn9-runtime@sha256:f894dae26e1ee8557c544f9cfdb9dc011b1552bf3c1e656b422f2e221d380e40'
  'qwen3'         = 'pytorch/pytorch:2.11.0-cuda13.0-cudnn9-runtime@sha256:bfbb4a2b4fdba0fefdb428ea737e626d61bb3daf74a16e1ff935bdb03aa7c3f0'
  'voxcpm2'       = 'pytorch/pytorch:2.11.0-cuda13.0-cudnn9-runtime@sha256:bfbb4a2b4fdba0fefdb428ea737e626d61bb3daf74a16e1ff935bdb03aa7c3f0'
  'minilm-l6'     = 'python:3.13-slim@sha256:bffeb7bd6a85767587059c6ba23e1e9122078e3aa3fa836099171b9bb5a9bb00'
  'mpnet-base-v2' = 'python:3.13-slim@sha256:bffeb7bd6a85767587059c6ba23e1e9122078e3aa3fa836099171b9bb5a9bb00'
}

$base   = $Bases[$Service]
$svcDir = Join-Path $InfraRoot $Service
if (-not (Test-Path (Join-Path $svcDir 'requirements.in'))) {
  throw "No requirements.in in $svcDir"
}

# voxcpm2 declares a knowingly-inconsistent constraint (datasets 3.6.0) via overrides.txt
# (spec §6.2 constraint 3). Pass it only when present.
$overrides = ''
if (Test-Path (Join-Path $svcDir 'overrides.txt')) { $overrides = '--overrides overrides.txt' }

$compile = "uv pip compile requirements.in --generate-hashes $overrides -o requirements.txt"
$install = 'pip install --no-cache-dir --require-hashes -r requirements.txt'
$script  = "set -e; pip install --no-cache-dir uv==$UV; $compile; $install"

Write-Host "==> lock $Service" -ForegroundColor Cyan
Write-Host "    base $base"
docker run --rm -e PIP_BREAK_SYSTEM_PACKAGES=1 -v "${svcDir}:/w" -w /w $base sh -c $script
if ($LASTEXITCODE -ne 0) { throw "lock/build FAILED for $Service (exit $LASTEXITCODE)" }
Write-Host "==> $Service — requirements.txt regenerated and proven to install" -ForegroundColor Green
