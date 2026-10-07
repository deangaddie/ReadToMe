<#
.SYNOPSIS
Builds the native web front end (src/Read2Me.Native, Bun) into src/Read2Me.App/wwwroot/app2 so the .NET host
can serve it at /app2 and the native browser E2E tests run instead of skipping.

.DESCRIPTION
Runs `bun install --frozen-lockfile` (exactly what bun.lock says) and `bun run build` in src/Read2Me.Native.
Pass -Check to run `bun run check` (lint + typecheck + api:check + icons:check + tests + build) instead of a
bare build, which is what a PR gate wants. Pass -SkipInstall when node_modules is already current (local loops).

Bun is looked up on PATH first, then at ~/.bun/bin/bun.exe (where the Windows installer puts it without adding
it to PATH). The installed major.minor must match the "packageManager" pin in src/Read2Me.Native/package.json.

.EXAMPLE
pwsh scripts/build-native.ps1
dotnet test src/Read2Me.E2eTests            # native web tests (Tests/Native) now run; set R2M_E2E_BROWSER=firefox for Firefox

.EXAMPLE
pwsh scripts/build-native.ps1 -Check        # CI: everything the native project gates on
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$native = Join-Path $repoRoot 'src/Read2Me.Native'
$bundle = Join-Path $repoRoot 'src/Read2Me.App/wwwroot/app2/index.html'
$installHint = 'Install Bun: `powershell -c "irm bun.sh/install.ps1 | iex"` (https://bun.sh), then try again.'

# Find Bun: PATH first, then the installer's default location.
$bun = $null
if ($found = Get-Command bun -ErrorAction SilentlyContinue) { $bun = $found.Source }
if (-not $bun) {
    $homeBun = Join-Path $HOME '.bun/bin/bun.exe'
    if (Test-Path $homeBun) { $bun = $homeBun }
}
if (-not $bun) {
    throw "Bun is neither on PATH nor at ~/.bun/bin/bun.exe. $installHint"
}

# The packageManager pin ("bun@1.4.2") is the only version pin; CI's setup-bun reads the same field.
$package = Get-Content -Raw (Join-Path $native 'package.json') | ConvertFrom-Json
$pin = [string]$package.packageManager
if ($pin -notmatch '^bun@(\d+)\.(\d+)') {
    throw "package.json ""packageManager"" is '$pin'; expected bun@<major>.<minor>.<patch>."
}
$pinned = "$($Matches[1]).$($Matches[2])"

$installed = [string](& $bun --version)
if ($LASTEXITCODE -ne 0) { throw "bun --version failed ($LASTEXITCODE) using $bun" }
$installed = $installed.Trim()
if ($installed -notmatch '^(\d+)\.(\d+)') {
    throw "Could not parse Bun version '$installed' from $bun."
}
$actual = "$($Matches[1]).$($Matches[2])"
if ($actual -ne $pinned) {
    throw "Bun $installed at $bun does not match the package.json pin $pin (major.minor $pinned). Install that version (bun upgrade, or https://bun.sh)."
}

Push-Location $native
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
    throw "Build finished but $bundle is missing — check BASE in src/Read2Me.Native/base.ts and build.ts."
}
Write-Host "Bundle at $(Split-Path -Parent $bundle); the host serves it at /app2 and the native web E2E tests will run." -ForegroundColor Green
