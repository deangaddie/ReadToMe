<#
.SYNOPSIS
Regenerates the HTTP contract (ADR 0017): the host's OpenAPI document into src/Read2Me.Web/src/app/api/openapi.json,
then src/app/api/schema.d.ts from it.

.DESCRIPTION
Runs the one explicit E2E test that writes the document (OpenApiContractTests.Write_the_contract). It boots the host
in-proc on the E2E fixture's own temp Workspace, so no real Workspace is read or migrated. The test writes canonical
JSON: object keys sorted at every depth, `servers` dropped, array order kept, so one host always gives the same bytes.
Then it runs `bun run api:types` (openapi-typescript over the checked-in openapi.json).

When either file changed, it says so: commit both, and fix whatever web code `bun run typecheck` then points at.
It doesn't fail on a change; the gate's later typecheck is the comparison.

The PR gate (check.ps1) runs this as its API contract step, after dotnet build, with -NoBuild.

Pass -NoBuild when the E2E project is already built; -SkipInstall to skip `bun install` when node_modules is current.

.EXAMPLE
pwsh scripts/api-types.ps1                 # after changing a DTO or endpoint on the host
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$SkipInstall,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repoRoot 'src/Read2Me.Web'
$e2eTests = Join-Path $repoRoot 'src/Read2Me.E2eTests'
$contract = Join-Path $web 'src/app/api/openapi.json'
$schema = Join-Path $web 'src/app/api/schema.d.ts'
$writer = 'Read2Me.E2eTests.Tests.OpenApiContractTests.Write_the_contract'

. (Join-Path $PSScriptRoot 'find-bun.ps1')
$bun = Find-Bun -WebDir $web

function Get-Fingerprint {
    param([string]$Path)
    if (Test-Path $Path) { (Get-FileHash $Path -Algorithm SHA256).Hash } else { '' }
}
$before = @{ $contract = Get-Fingerprint $contract; $schema = Get-Fingerprint $schema }

$testArgs = @('test', $e2eTests, '--configuration', $Configuration, '--filter-method', $writer, '--explicit', 'only')
if ($NoBuild) { $testArgs += '--no-build' }
Write-Host ">> dotnet test (writes openapi.json): $writer" -ForegroundColor Cyan
& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw "Writing openapi.json failed (dotnet test exit $LASTEXITCODE)" }
if (-not (Test-Path $contract)) { throw "dotnet test passed but $contract is missing; did the filter match no test?" }

Push-Location $web
try {
    if (-not $SkipInstall) {
        Write-Host '>> bun install --frozen-lockfile' -ForegroundColor Cyan
        & $bun install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw "bun install --frozen-lockfile failed ($LASTEXITCODE)" }
    }
    Write-Host '>> bun run api:types' -ForegroundColor Cyan
    & $bun run api:types
    if ($LASTEXITCODE -ne 0) { throw "bun run api:types failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$changed = @($contract, $schema | Where-Object { (Get-Fingerprint $_) -ne $before[$_] } |
    ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_).Replace('\', '/') })
if ($changed.Count -gt 0) {
    Write-Host "The API contract changed: $($changed -join ', '). Commit openapi.json and schema.d.ts together." -ForegroundColor Yellow
}
else {
    Write-Host 'The API contract is unchanged (openapi.json and schema.d.ts).' -ForegroundColor Green
}
