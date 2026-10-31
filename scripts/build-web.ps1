<#
.SYNOPSIS
Builds the web front end (src/Read2Me.Web, Bun) into src/Read2Me.App/wwwroot/app so the .NET host
can serve it at /app and the browser E2E tests run instead of skipping.

.DESCRIPTION
Runs `bun install --frozen-lockfile` (exactly what bun.lock says) and `bun run build` in src/Read2Me.Web.
Pass -Check to run `bun run check` (format:check + lint + typecheck + icons:check + help:check + tests + build) instead of a
bare build, which is what a PR gate wants. Pass -SkipInstall when node_modules is already current (local loops).

Bun is looked up on PATH first, then at ~/.bun/bin/bun.exe (where the Windows installer puts it without adding
it to PATH). The installed major.minor must match the "packageManager" pin in src/Read2Me.Web/package.json.

.EXAMPLE
pwsh scripts/build-web.ps1
dotnet test src/Read2Me.E2eTests         # browser tests (Tests/Web) now run; set R2M_E2E_BROWSER=firefox for Firefox

.EXAMPLE
pwsh scripts/build-web.ps1 -Check        # CI: everything the web project gates on
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repoRoot 'src/Read2Me.Web'
$bundle = Join-Path $repoRoot 'src/Read2Me.App/wwwroot/app/index.html'
. (Join-Path $PSScriptRoot 'find-bun.ps1')
$bun = Find-Bun -WebDir $web

Push-Location $web
try {
    if (-not $SkipInstall) {
        Write-Host '>> bun install --frozen-lockfile' -ForegroundColor Cyan
        & $bun install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "bun install --frozen-lockfile failed ($LASTEXITCODE)" }
    }

    $script = if ($Check) { 'check' } else { 'build' }
    Write-Host ">> bun run $script" -ForegroundColor Cyan
    & $bun run $script
    if ($LASTEXITCODE -ne 0) { throw "bun run $script failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $bundle)) {
    throw "Build finished but $bundle is missing — check BASE in src/Read2Me.Web/base.ts and build.ts."
}
Write-Host "Bundle at $(Split-Path -Parent $bundle); the host serves it at /app and the browser E2E tests will run." -ForegroundColor Green
