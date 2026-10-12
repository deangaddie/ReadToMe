<#
.SYNOPSIS
The PR gate: runs the stages CI runs, in the same order, and stops at the first failure.

.DESCRIPTION
Stages, in order (each one is a separate named step in .github/workflows/ci.yml):

  1. build-web.ps1 -Check      lint + typecheck + api:check + icons:check + bun tests + build into wwwroot/app
  2. dotnet build              the whole solution
  3. unit tests                src/Read2Me.Tests
  4. E2E in Chromium           src/Read2Me.E2eTests with R2M_E2E_BROWSER=chromium, the whole suite
  5. E2E in Firefox            the browser tests only (Tests/Web) with R2M_E2E_BROWSER=firefox; the API and
                               host tests never open a browser, so Chromium's pass already covers them

Before the E2E stages it installs any Playwright browser the driver wants that is not in the Playwright cache
yet (Firefox on first use; Chromium too on a fresh machine). The cache is PLAYWRIGHT_BROWSERS_PATH when set,
else Playwright's per-OS default.

One deliberate difference from CI: this builds Debug by default (what the IDE builds) while CI builds Release.
Pass -Configuration Release to match CI exactly.

Pass -SkipInstall to skip `bun install` when node_modules is already current (local loops).

When an E2E stage fails, the traces and screenshots its failing tests left under the test bin's
artifacts/ folder are copied to .artifacts/e2e/<yyyyMMdd-HHmmss>-<browser>/ at the repo root (git-ignored), and
the path is printed. The copies outlive later test runs and builds; only the newest 10 run folders are kept.

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
$e2eBinArtifacts = Join-Path $e2eBin 'artifacts'
$keptE2eRuns = Join-Path $repoRoot '.artifacts/e2e'
$keptRunLimit = 10

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

# Copies the artifact files a failed E2E run wrote to a timestamped run folder under .artifacts/e2e, keeping each
# test's folder name, then prunes to the newest $keptRunLimit run folders. Only files written since the run started
# go: only a passing test deletes its folder, so one that failed in an earlier run and didn't run this time (the
# Firefox stage runs only the browser tests) still has its old trace and screenshot there.
function Save-E2eArtifacts {
    param([string]$Browser, [datetime]$Since)
    $runFiles = @(Get-ChildItem $e2eBinArtifacts -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object LastWriteTime -ge $Since)
    if ($runFiles.Count -eq 0) {
        Write-Host "No E2E artifacts to keep: nothing under $e2eBinArtifacts from this run." -ForegroundColor Yellow
        return
    }
    $run = Join-Path $keptE2eRuns "$($Since.ToString('yyyyMMdd-HHmmss'))-$Browser"
    foreach ($file in $runFiles) {
        $target = Join-Path $run ([System.IO.Path]::GetRelativePath($e2eBinArtifacts, $file.FullName))
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Copy-Item $file.FullName $target -Force
    }
    Write-Host "E2E failure artifacts (trace/screenshot) kept in: $run" -ForegroundColor Yellow
    # Run folders are named by start time, so name order is age order. Anything else in there is left alone.
    Get-ChildItem $keptE2eRuns -Directory | Where-Object Name -match '^\d{8}-\d{6}-' |
        Sort-Object Name -Descending | Select-Object -Skip $keptRunLimit | Remove-Item -Recurse -Force
}

# Runs the E2E suite in one browser; with -Filter (dotnet test --filter syntax), only the tests it selects.
function Invoke-E2e {
    param([string]$Browser, [string]$Filter)
    $previous = $env:R2M_E2E_BROWSER
    $started = Get-Date
    $exitCode = 0
    try {
        $env:R2M_E2E_BROWSER = $Browser
        $filterArgs = if ($Filter) { @('--filter', $Filter) } else { @() }
        dotnet test $e2eTests --configuration $Configuration --no-build @filterArgs
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:R2M_E2E_BROWSER = $previous
    }
    if ($exitCode -ne 0) {
        # A failed save only warns, so the stage still fails on the test exit code, not on the copy.
        try { Save-E2eArtifacts -Browser $Browser -Since $started }
        catch { Write-Host "Could not keep the E2E artifacts: $($_.Exception.Message)" -ForegroundColor Yellow }
        $global:LASTEXITCODE = $exitCode
    }
}

$skip = @{}
if ($SkipInstall) { $skip.SkipInstall = $true }

try {
    Invoke-Stage 'Web check and build (build-web.ps1 -Check)' {
        & (Join-Path $PSScriptRoot 'build-web.ps1') -Check @skip
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
    # The trailing dot keeps Tests.WebHostingTests (no browser) out of the filter.
    Invoke-Stage 'E2E browser tests (Firefox)' { Invoke-E2e 'firefox' -Filter 'FullyQualifiedName~Tests.Web.' }
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
