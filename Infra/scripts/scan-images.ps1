#!/usr/bin/env pwsh
# Infra/scripts/scan-images.ps1
#
# The repeatable image scan for a security sweep. Run by hand, never a gate: it needs built
# images, network for the vulnerability DB, and minutes per image.
#
# Each compose image is `docker save`d to a tar, and a pinned Trivy scans the tar mounted
# read-only. No Docker socket goes into the container. One image at a time, because the
# shared `trivy-cache` volume locks.
#
# Infra/scan-accepted.yaml (Trivy's .trivyignore.yaml format) lists accepted findings. They
# still print, in their own section. Exit 1 only when a CRITICAL/HIGH finding has a fix and
# isn't accepted.
#
#   ./scan-images.ps1                              # every compose image, CRITICAL + HIGH
#   ./scan-images.ps1 -Image read2me-llama         # one image (repository, or the full ref)
#   ./scan-images.ps1 -Severity CRITICAL,HIGH,MEDIUM
#   ./scan-images.ps1 -All                         # ignore the accept list
#   ./scan-images.ps1 -Compare ../.artifacts/scans/2026-10-26   # new / fixed against an earlier run
[CmdletBinding()]
param(
  [string]$Image,
  [ValidateSet('CRITICAL','HIGH','MEDIUM','LOW','UNKNOWN')]
  [string[]]$Severity = @('CRITICAL','HIGH'),
  [string]$Compare,
  [switch]$All
)
$ErrorActionPreference = 'Stop'

# Tag bumps are a hand edit; refresh-pins.ps1 refreshes the digest.
$Trivy = 'aquasec/trivy:0.76.1@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c'

$InfraRoot = Split-Path -Parent $PSScriptRoot   # Infra/
$RepoRoot  = Split-Path -Parent $InfraRoot
$Accepted  = Join-Path $InfraRoot 'scan-accepted.yaml'
$OutDir    = Join-Path $RepoRoot ".artifacts/scans/$(Get-Date -Format 'yyyy-MM-dd')"
$TarDir    = Join-Path $RepoRoot '.artifacts/scans/tar'

# `ghcr.io/x/audio.cpp:tag@sha256:...` -> `ghcr.io/x/audio.cpp`, so a moved pin still compares.
function Get-Repository([string]$ref) {
  $ref = ($ref -split '@')[0]
  if ($ref -match '^(.*):[^/:]+$') { $ref = $Matches[1] }
  $ref
}
function Get-Slug([string]$ref) { (Get-Repository $ref) -replace '[/:]', '_' }

# Flatten one Trivy JSON report into rows; suppressed findings come back marked Accepted.
function Read-Findings([string]$path, [string]$image) {
  $report = Get-Content -Raw $path | ConvertFrom-Json
  foreach ($result in @($report.Results)) {
    # Findings name the binary package; the package list maps it to its source (util-linux).
    $source = @{}
    foreach ($p in @($result.Packages)) { if ($p -and $p.SrcName) { $source[$p.Name] = $p.SrcName } }
    $rows = @(@($result.Vulnerabilities) | Where-Object { $_ } | ForEach-Object { @{ V = $_; Accepted = $false; Statement = '' } })
    $rows += @(@($result.ExperimentalModifiedFindings) | Where-Object { $_ -and $_.Status -eq 'ignored' } |
      ForEach-Object { @{ V = $_.Finding; Accepted = $true; Statement = $_.Statement } })
    foreach ($r in $rows) {
      $v = $r.V
      [pscustomobject]@{
        Image     = $image
        Id        = $v.VulnerabilityID
        Package   = if ($source[$v.PkgName]) { $source[$v.PkgName] } else { $v.PkgName }
        Binary    = $v.PkgName
        Installed = $v.InstalledVersion
        Fixed     = $v.FixedVersion
        Severity  = $v.Severity
        Accepted  = $r.Accepted
        Statement = $r.Statement
      }
    }
  }
}

# Earlier runs are read back with the slug as their image, so key on the slug.
function Get-Key($f) { "$(Get-Slug $f.Image)|$($f.Id)|$($f.Binary)" }

function Write-Section([string]$title, [object[]]$rows, [ConsoleColor]$color) {
  if (-not $rows) { return }
  Write-Host "  $title ($(@($rows).Count))" -ForegroundColor $color
  $said = [System.Collections.Generic.HashSet[string]]::new()
  $rows | Group-Object Package, Installed, Fixed | Sort-Object Name | ForEach-Object {
    $f = $_.Group[0]
    $bins = @($_.Group.Binary | Sort-Object -Unique)
    $pkg = if ($bins.Count -gt 1 -or $bins[0] -ne $f.Package) { "$($f.Package) [$($bins -join ', ')]" } else { $f.Package }
    $ver = if ($f.Fixed) { "$($f.Installed) -> $($f.Fixed)" } else { $f.Installed }
    $ids = ($_.Group | Sort-Object Id -Unique | ForEach-Object { "$($_.Id) ($($_.Severity.Substring(0,1)))" }) -join ', '
    Write-Host "    $pkg  $ver"
    Write-Host "      $ids" -ForegroundColor DarkGray
    # Print each reason once per section; the rows after it share it.
    foreach ($s in @($_.Group.Statement | Where-Object { $_ } | Sort-Object -Unique)) {
      if ($said.Add($s)) { Write-Host "      accepted: $s" -ForegroundColor DarkGray }
    }
  }
}

$images = @(docker compose -f (Join-Path $InfraRoot 'docker-compose.yml') config --images)
if ($LASTEXITCODE -ne 0) { throw 'docker compose config --images failed' }
if ($Image) {
  $images = @($images | Where-Object { $_ -eq $Image -or (Get-Repository $_) -eq $Image })
  if (-not $images) { throw "$Image is not a compose image" }
}

New-Item -ItemType Directory -Force -Path $OutDir, $TarDir | Out-Null
$trivyArgs = @('image', '--input', '/in/image.tar', '--scanners', 'vuln', '--timeout', '30m',
               '--severity', ($Severity -join ','), '--format', 'json', '--quiet')
$mounts = @('-v', "${TarDir}:/in:ro", '-v', "${OutDir}:/out", '-v', 'trivy-cache:/root/.cache')
# Without --ignorefile Trivy looks for /.trivyignore, which isn't there.
if (-not $All) {
  $mounts += @('-v', "${Accepted}:/cfg/scan-accepted.yaml:ro")
  $trivyArgs += @('--ignorefile', '/cfg/scan-accepted.yaml', '--show-suppressed')
}

$findings = @()
$failed = @()
foreach ($ref in $images) {
  Write-Host "==> $ref" -ForegroundColor Cyan
  # A bare repository makes docker save write every local tag, and Trivy reads one image per tar.
  if ($ref -notmatch '@' -and $ref -eq (Get-Repository $ref)) { $ref = "${ref}:latest" }
  docker image inspect $ref *> $null
  if ($LASTEXITCODE -ne 0) { Write-Warning "  $ref is neither built nor pulled; skipped"; continue }

  $slug = Get-Slug $ref
  $tar = Join-Path $TarDir 'image.tar'
  try {
    docker save $ref -o $tar
    if ($LASTEXITCODE -ne 0) { throw "docker save failed for $ref" }
    docker run --rm @mounts $Trivy @trivyArgs --output "/out/$slug.json"
    if ($LASTEXITCODE -ne 0) { throw "trivy failed for $ref (exit $LASTEXITCODE)" }
    $findings += @(Read-Findings (Join-Path $OutDir "$slug.json") (Get-Repository $ref))
  }
  catch { Write-Warning "  $_"; $failed += Get-Repository $ref }
  finally { Remove-Item -Force -ErrorAction SilentlyContinue $tar }
}

Write-Host ''
foreach ($group in $findings | Group-Object Image) {
  Write-Host $group.Name -ForegroundColor Cyan
  Write-Section 'fix available'    @($group.Group | Where-Object { -not $_.Accepted -and $_.Fixed })     Red
  Write-Section 'no fix upstream'  @($group.Group | Where-Object { -not $_.Accepted -and -not $_.Fixed }) Yellow
  Write-Section 'accepted'         @($group.Group | Where-Object { $_.Accepted })                         DarkGreen
}
$clean = @($images | ForEach-Object { Get-Repository $_ } | Where-Object { $_ -notin $findings.Image -and $_ -notin $failed })
foreach ($repo in $clean) { Write-Host "${repo}: no findings" -ForegroundColor Green }

if ($Compare) {
  if (-not (Test-Path $Compare)) { throw "no earlier run at $Compare" }
  $scanned = @($images | ForEach-Object { Get-Slug $_ })
  $before = @(Get-ChildItem $Compare -Filter *.json | Where-Object { $_.BaseName -in $scanned } |
    ForEach-Object { Read-Findings $_.FullName ($_.BaseName) })
  $beforeKeys = @($before | ForEach-Object { Get-Key $_ })
  $nowKeys = @($findings | ForEach-Object { Get-Key $_ })
  Write-Host ''
  Write-Host "Against $Compare" -ForegroundColor Magenta
  $new = @($findings | Where-Object { (Get-Key $_) -notin $beforeKeys })
  $gone = @($before | Where-Object { (Get-Key $_) -notin $nowKeys })
  foreach ($f in $new | Sort-Object Image, Package, Id -Unique) { Write-Host "  new    $($f.Image)  $($f.Package)  $($f.Id) ($($f.Severity))" -ForegroundColor Red }
  foreach ($f in $gone | Sort-Object Image, Package, Id -Unique) { Write-Host "  fixed  $($f.Image)  $($f.Package)  $($f.Id) ($($f.Severity))" -ForegroundColor Green }
  if (-not $new -and -not $gone) { Write-Host '  no change' }
}

Write-Host ''
Write-Host "JSON: $OutDir"
$gate = @($findings | Where-Object { -not $_.Accepted -and $_.Fixed -and $_.Severity -in 'CRITICAL', 'HIGH' })
if ($gate) { Write-Host "$(@($gate).Count) CRITICAL/HIGH finding(s) have a fix and aren't accepted." -ForegroundColor Red }
if ($failed) { Write-Host "Not scanned: $($failed -join ', ')" -ForegroundColor Red }
if ($gate -or $failed) { exit 1 }
exit 0
