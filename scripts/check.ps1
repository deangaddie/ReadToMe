<#
.SYNOPSIS
The PR gate: runs the stages CI runs, in the same order, and stops at the first failure.

.DESCRIPTION
Stages, in order (each one is a separate named step in .github/workflows/ci.yml):

  1. build-native.ps1 -Check   lint + typecheck + api:check + icons:check + bun tests + build into wwwroot/app2
  2. build-web.ps1             a plain Angular build into wwwroot/app (the Angular E2E tests still need the bundle)
  3. dotnet build              the whole solution
  4. unit tests                src/Read2Me.Tests
  5. E2E in Chromium           src/Read2Me.E2eTests with R2M_E2E_BROWSER=chromium
  6. E2E in Firefox            src/Read2Me.E2eTests with R2M_E2E_BROWSER=firefox (Angular classes skip, native run)

Before the E2E stages it installs any Playwright browser the driver wants that is not in the Playwright cache
yet (Firefox on first use; Chromium too on a fresh machine). The cache is PLAYWRIGHT_BROWSERS_PATH when set,
else Playwright's per-OS default.

Two deliberate differences from CI: the Angular stage here is a plain build, while CI keeps `npm run check` for
Angular until the native cutover; and this builds Debug by default (what the IDE builds) while CI builds Release.
Pass -Configuration Release to match CI exactly.

Pass -SkipInstall to skip `bun install` and `npm ci` when node_modules is already current (local loops).

.EXAMPLE
pwsh scripts/check.ps1                 # before opening a PR

.EXAMPLE
pwsh scripts/check.ps1 -SkipInstall    # the local loop, dependencies already installed
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'src/Read2Me.slnx'
$unitTests = Join-Path $repoRoot 'src/Read2Me.Tests'
$e2eTests = Join-Path $repoRoot 'src/Read2Me.E2eTests'
$e2eBin = Join-Path $e2eTests "bin/$Configuration/net10.0"
$playwrightDriver = Join-Path $e2eBin 'playwright.ps1'
$browsersManifest = Join-Path $e2eBin '.playwright/package/browsers.json'

$stages = @()
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# Runs one named stage. The scriptblock either throws or leaves $LASTEXITCODE set by the native command it ran;
# both end the gate here, naming the stage.
function Invoke-Stage {
    param([string]$Name, [scriptblock]$Body)
    $n = $script:stages.Count + 1
    Write-Host ''
    Write-Host "== [$n] $Name" -ForegroundColor Cyan
    $started = $script:stopwatch.Elapsed
    $global:LASTEXITCODE = 0
    $reason = $null
    try {
        & $Body
        if ($LASTEXITCODE -ne 0) { $reason = "exit $LASTEXITCODE" }
    }
    catch {
        $reason = $_.Exception.Message
    }
    $elapsed = ($script:stopwatch.Elapsed - $started).ToString('mm\:ss')
    if ($reason) { throw "Stage [$n] $Name failed after ${elapsed}: $reason" }
    $script:stages += "[$n] $Name ($elapsed)"
}

# Where Playwright keeps its browsers: the override, or the per-OS default the driver uses.
function Get-PlaywrightBrowsersPath {
    if ($env:PLAYWRIGHT_BROWSERS_PATH) { return $env:PLAYWRIGHT_BROWSERS_PATH }
    if ($IsWindows) { return Join-Path $env:LOCALAPPDATA 'ms-playwright' }
    if ($IsMacOS) { return Join-Path $HOME 'Library/Caches/ms-playwright' }
    return Join-Path $HOME '.cache/ms-playwright'
}

# Installs every browser the gate launches whose revision folder is missing from the cache. The revision
# comes from the driver's own browsers.json, so a Microsoft.Playwright upgrade re-installs on its first run.
function Install-MissingPlaywrightBrowsers {
    param([string[]]$Browsers)
    foreach ($file in $playwrightDriver, $browsersManifest) {
        if (-not (Test-Path $file)) { throw "$file is missing; the dotnet build stage should have produced it." }
    }
    $manifest = Get-Content -Raw $browsersManifest | ConvertFrom-Json
    $cache = Get-PlaywrightBrowsersPath
    $missing = @()
    foreach ($name in $Browsers) {
        $entry = $manifest.browsers | Where-Object name -eq $name
        if (-not $entry) { throw "browsers.json has no '$name' entry: $browsersManifest" }
        if (-not (Test-Path (Join-Path $cache "$name-$($entry.revision)"))) { $missing += $name }
    }
    if ($missing.Count -eq 0) {
        Write-Host "Playwright browsers present in $cache ($($Browsers -join ', '))."
        return
    }
    Write-Host ">> playwright install $($missing -join ' ')  (first use; into $cache)"
    & $playwrightDriver install @missing
    if ($LASTEXITCODE -ne 0) { throw "playwright install $($missing -join ' ') failed ($LASTEXITCODE)" }
}

function Invoke-E2e {
    param([string]$Browser)
    $previous = $env:R2M_E2E_BROWSER
    try {
        $env:R2M_E2E_BROWSER = $Browser
        dotnet test $e2eTests --configuration $Configuration --no-build
    }
    finally {
        $env:R2M_E2E_BROWSER = $previous
    }
}

$skip = @{}
if ($SkipInstall) { $skip.SkipInstall = $true }

try {
    Invoke-Stage 'Native check and build (build-native.ps1 -Check)' {
        & (Join-Path $PSScriptRoot 'build-native.ps1') -Check @skip
    }
    Invoke-Stage 'Web build (build-web.ps1)' {
        & (Join-Path $PSScriptRoot 'build-web.ps1') @skip
    }
    Invoke-Stage "dotnet build ($Configuration)" {
        dotnet build $solution --configuration $Configuration
    }
    Invoke-Stage 'Unit tests' {
        dotnet test $unitTests --configuration $Configuration --no-build
    }
    Invoke-Stage 'Playwright browsers' {
        Install-MissingPlaywrightBrowsers -Browsers @('chromium', 'firefox')
    }
    Invoke-Stage 'E2E tests (Chromium)' { Invoke-E2e 'chromium' }
    Invoke-Stage 'E2E tests (Firefox)' { Invoke-E2e 'firefox' }
}
catch {
    Write-Host ''
    Write-Host "PR gate FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($stages.Count -gt 0) { Write-Host "Passed: $($stages -join '; ')" -ForegroundColor DarkGray }
    exit 1
}

Write-Host ''
Write-Host "PR gate passed in $($stopwatch.Elapsed.ToString('mm\:ss')):" -ForegroundColor Green
$stages | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
