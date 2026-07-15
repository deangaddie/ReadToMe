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
$Images = @(
  @{ Ref = 'pytorch/pytorch:2.6.0-cuda12.6-cudnn9-runtime';  Files = @('Dockerfile.chatterbox') },
  @{ Ref = 'pytorch/pytorch:2.11.0-cuda13.0-cudnn9-runtime'; Files = @('Dockerfile.qwen3','Dockerfile.voxcpm2') },
  @{ Ref = 'python:3.13-slim';                               Files = @('Dockerfile.minilm-l6','Dockerfile.mpnet-base-v2') },
  @{ Ref = 'nvidia/cuda:13.3.0-devel-ubuntu24.04';           Files = @('Dockerfile.llama') },
  @{ Ref = 'nvidia/cuda:13.3.0-runtime-ubuntu24.04';         Files = @('Dockerfile.llama') }
)

foreach ($img in $Images) {
  $ref = $img.Ref
  Write-Host "==> $ref" -ForegroundColor Cyan
  $digest = (docker buildx imagetools inspect $ref --format '{{.Manifest.Digest}}').Trim()
  if ($LASTEXITCODE -ne 0 -or -not $digest) { throw "could not resolve digest for $ref" }
  Write-Host "    $digest"

  $pinned = "$ref@$digest"
  # Match the tag optionally already carrying a digest, and replace it.
  $pattern = [regex]::Escape($ref) + '(@sha256:[0-9a-f]{64})?'

  foreach ($file in $img.Files) {
    $path = Join-Path $InfraRoot $file
    if (-not (Test-Path $path)) { Write-Warning "  missing $file"; continue }
    $text = Get-Content -Raw $path
    if ($text -notmatch [regex]::Escape($ref)) { continue }
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
