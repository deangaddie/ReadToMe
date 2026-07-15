#!/usr/bin/env pwsh
# Infra/scripts/refresh-pins.ps1
#
# The human-driven TAG-BUMP path. Dependabot only ever proposes DIGEST bumps within a
# fixed tag (spec §6.1); moving a tag is a compatibility decision and lives here.
#
# For each base image: re-resolve its tag to the current registry digest, rewrite the
# `image:tag@sha256:...` in every Dockerfile that uses it, then this is where the
# manual security sweep runs — `docker scout cves` and the no-compiler check (spec §11).
#
#   ./refresh-pins.ps1            # resolve + rewrite FROM digests, print diffs
#   ./refresh-pins.ps1 -Report    # resolve + print only, touch nothing
[CmdletBinding()]
param([switch]$Report)
$ErrorActionPreference = 'Stop'

$InfraRoot = Split-Path -Parent $PSScriptRoot   # Infra/

# tag -> Dockerfiles that FROM it (spec §6.1 table).
# `Ref` is the human tag resolved against the registry. `InFile` is the literal
# string the digest hangs off inside the Dockerfile — usually the same as `Ref`,
# but llama's FROM is ARG-interpolated (`nvidia/cuda:${CUDA_VERSION}-...`), so its
# ARG default carries the template, not the expanded tag. Without this the llama
# rewrite silently no-ops.
$Images = @(
  @{ Ref = 'pytorch/pytorch:2.6.0-cuda12.6-cudnn9-runtime';  Files = @('Dockerfile.chatterbox') },
  @{ Ref = 'pytorch/pytorch:2.11.0-cuda13.0-cudnn9-runtime'; Files = @('Dockerfile.qwen3','Dockerfile.voxcpm2') },
  @{ Ref = 'python:3.13-slim';                               Files = @('Dockerfile.minilm-l6','Dockerfile.mpnet-base-v2') },
  @{ Ref = 'nvidia/cuda:13.3.0-devel-ubuntu24.04';           Files = @('Dockerfile.llama'); InFile = 'nvidia/cuda:${CUDA_VERSION}-devel-ubuntu${UBUNTU_VERSION}' },
  @{ Ref = 'nvidia/cuda:13.3.0-runtime-ubuntu24.04';         Files = @('Dockerfile.llama'); InFile = 'nvidia/cuda:${CUDA_VERSION}-runtime-ubuntu${UBUNTU_VERSION}' }
)

foreach ($img in $Images) {
  $ref = $img.Ref
  $inFile = if ($img.InFile) { $img.InFile } else { $ref }
  Write-Host "==> $ref" -ForegroundColor Cyan
  $digest = (docker buildx imagetools inspect $ref --format '{{.Manifest.Digest}}').Trim()
  if ($LASTEXITCODE -ne 0 -or -not $digest) { throw "could not resolve digest for $ref" }
  Write-Host "    $digest"

  $pinned = "$inFile@$digest"
  # Match the in-file ref optionally already carrying a digest, and replace it.
  $pattern = [regex]::Escape($inFile) + '(@sha256:[0-9a-f]{64})?'

  foreach ($file in $img.Files) {
    $path = Join-Path $InfraRoot $file
    if (-not (Test-Path $path)) { Write-Warning "  missing $file"; continue }
    $text = Get-Content -Raw $path
    if ($text -notmatch [regex]::Escape($inFile)) { continue }
    $new = [regex]::Replace($text, $pattern, $pinned)
    if ($new -eq $text) { Write-Host "    $file — already current" ; continue }
    if ($Report) { Write-Host "    $file — WOULD update" -ForegroundColor Yellow }
    else { Set-Content -NoNewline -Path $path -Value $new; Write-Host "    $file — updated" -ForegroundColor Green }
  }
}

Write-Host ''
Write-Host 'Next (manual, at every pin refresh — spec §6.5 / §11):' -ForegroundColor Magenta
Write-Host '  docker scout cves <image>:<tag>        # info for the human, never a CI gate'
Write-Host '  docker run --rm --entrypoint sh <image> -c "command -v gcc g++ cc make git"  # must print nothing'
Write-Host '  re-run  ./lock.ps1 <service>  for any service whose base moved'
