#!/usr/bin/env pwsh
# Infra/scripts/egress-check.ps1
#
# Proves the runtime services have no egress (Infra/README.md, "Container egress"). For each
# running compose service except the proxy, a throwaway probe joins that container's network
# namespace and tries a TCP connection to public IP literals, so DNS plays no part. Any
# connection that succeeds fails the check. Re-run it after any compose change.
#
# The probe is the proxy's pinned image (busybox nc), run hardened. Because it shares the
# service's namespace, it sees the same routes the service does, whatever the service's image
# ships. The same probe is run first from the proxy, which keeps egress: if that one cannot get
# out either, the machine is offline and a pass would prove nothing.
#
#   ./egress-check.ps1                      # every running service
#   ./egress-check.ps1 -Target 9.9.9.9:443  # other IP:port targets
#
# Exit 0: every running service is blocked. 1: a service got out. 2: inconclusive (no service
# running, the proxy is down, or the proxy itself cannot get out).
[CmdletBinding()]
param(
  [string[]]$Target = @('1.1.1.1:443', '8.8.8.8:53'),
  [int]$TimeoutSeconds = 5
)
$ErrorActionPreference = 'Stop'

$InfraRoot = Split-Path -Parent $PSScriptRoot   # Infra/
$Compose = Join-Path $InfraRoot 'docker-compose.yml'
$ProxyService = 'proxy'

$images = @(docker compose -f $Compose config --images)
if ($LASTEXITCODE -ne 0) { throw 'docker compose config failed' }
$probeImage = @($images | Where-Object { $_ -like 'nginxinc/nginx-unprivileged:*' })[0]
if (-not $probeImage) { throw "no proxy image in $Compose" }

# True when any target accepts a TCP connection from inside the container's network namespace.
function Test-Egress([string]$container) {
  $got = $false
  foreach ($t in $Target) {
    $ip, $port = $t -split ':'
    docker run --rm --network "container:$container" --read-only --cap-drop ALL `
      --security-opt no-new-privileges:true --entrypoint sh $probeImage `
      -c "nc -n -w $TimeoutSeconds $ip $port </dev/null" *> $null
    $open = $LASTEXITCODE -eq 0
    Write-Host ("    {0,-18} {1}" -f $t, $(if ($open) { 'CONNECTED' } else { 'blocked' })) `
      -ForegroundColor $(if ($open) { 'Red' } else { 'DarkGray' })
    $got = $got -or $open
  }
  $got
}

function Get-Container([string]$service) {
  $id = (docker compose -f $Compose ps -q $service | Select-Object -First 1)
  if (-not $id) { return $null }
  (docker inspect -f '{{.Name}}' $id).TrimStart('/')
}

$running = @(docker compose -f $Compose ps --services --status running)
if ($LASTEXITCODE -ne 0) { throw 'docker compose ps failed' }

if ($ProxyService -notin $running) {
  Write-Host 'The proxy is not running; nothing can be compared against. Start it first.' -ForegroundColor Yellow
  exit 2
}
Write-Host "==> $ProxyService (control: must connect)" -ForegroundColor Cyan
if (-not (Test-Egress (Get-Container $ProxyService))) {
  Write-Host 'Inconclusive: the proxy cannot get out either, so the machine looks offline.' -ForegroundColor Yellow
  exit 2
}

$services = @($running | Where-Object { $_ -ne $ProxyService } | Sort-Object)
if (-not $services) {
  Write-Host 'Inconclusive: no service is running besides the proxy.' -ForegroundColor Yellow
  exit 2
}

$leaks = @()
foreach ($service in $services) {
  Write-Host "==> $service" -ForegroundColor Cyan
  if (Test-Egress (Get-Container $service)) { $leaks += $service }
}

Write-Host ''
$stopped = @(docker compose -f $Compose config --services | Where-Object { $_ -notin $running })
if ($stopped) { Write-Host "Not running, not checked: $($stopped -join ', ')" -ForegroundColor DarkGray }
if ($leaks) {
  Write-Host "Egress open from: $($leaks -join ', ')" -ForegroundColor Red
  exit 1
}
Write-Host "No egress from: $($services -join ', ')" -ForegroundColor Green
exit 0
