<#
.SYNOPSIS
Builds the Angular web front end into src/Read2Me.App/wwwroot/app so the .NET host can serve it at /app
and the browser E2E tests (src/Read2Me.E2eTests/Tests/Web) run instead of skipping.

.DESCRIPTION
Runs `npm ci` (exactly what package-lock.json says) and `npm run build` in src/Read2Me.Web. Pass -Check to
run `npm run check` (lint + typecheck + unit tests + build) instead of a bare build, which is what a PR gate
wants. Pass -SkipInstall when node_modules is already current (local loops).

Requires Node 24 / npm 11 (pinned in src/Read2Me.Web/package.json "engines").

.EXAMPLE
pwsh scripts/build-web.ps1
dotnet test src/Read2Me.E2eTests            # web tests now run

.EXAMPLE
pwsh scripts/build-web.ps1 -Check           # CI: everything the web project gates on
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repoRoot 'src/Read2Me.Web'
$bundle = Join-Path $repoRoot 'src/Read2Me.App/wwwroot/app/index.html'

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'npm is not on PATH. Install Node 24 (https://nodejs.org) and try again.'
}

Push-Location $web
try {
    if (-not $SkipInstall) {
        Write-Host '>> npm ci' -ForegroundColor Cyan
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
    }

    $script = if ($Check) { 'check' } else { 'build' }
    Write-Host ">> npm run $script" -ForegroundColor Cyan
    npm run $script
    if ($LASTEXITCODE -ne 0) { throw "npm run $script failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $bundle)) {
    throw "Build finished but $bundle is missing — check angular.json outputPath."
}
Write-Host "Bundle at $(Split-Path -Parent $bundle); the host serves it at /app and the web E2E tests will run." -ForegroundColor Green
