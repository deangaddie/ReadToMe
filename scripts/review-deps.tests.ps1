<#
.SYNOPSIS
Script-level tests for review-deps.ps1, run against real merged commits of this repo.

.DESCRIPTION
Each test runs review-deps.ps1 in a child pwsh on a base/head commit pair and checks its Markdown report and exit
code. The commits are fixed points in main's history:

  2b39c23d  Bump @preact/signals-core from 1.14.4 to 1.14.5 (Bun)         a clean bump
  ee05965b  Bump oxlint-tsgolint from 7.0.2006 to 7.0.2007 (Bun)          a wrapper and its six native packages
  108ed9c5  Bump vite from 8.1.6 to 8.1.7 (npm, Infra/ContainerHealth)    20 transitive version changes
  353fccd2  TypeScript 7 (Bun)                                            new transitive package names
  94a55ecd  Docs only                                                     no dependency file changed
  9f7e2278  Bump python in /Infra (Docker Hub digest, two Dockerfiles)    a digest pushed 2026-11-08
  02489eae  Move the audio.cpp pin (ghcr.io digest, docker-compose.yml)   no registry push date: the created field
  20cd5e05  Similarity locks take certifi, filelock, setuptools fixes     setuptools ships a .pth in both versions

Ages are pinned with -Now, so the young-version tests keep passing as the releases get older. The python digest's age
comes from Docker Hub only while 3.13-slim still points at it, then from its created field (2026-11-05), so the dates
the tests pin sit on the same side of 7 days either way. The .pth check's fixture tests are in
Infra/scripts/wheel-pth.tests.ps1.

Every test but the docs-only one reads the npm registry, Docker Hub, ghcr.io or PyPI. Without it they are skipped and reported as such, and the run
still fails if any test that ran failed. Exits 1 on any failure.

.EXAMPLE
pwsh scripts/review-deps.tests.ps1
#>
#Requires -Version 7
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'review-deps.ps1'
$failures = [System.Collections.Generic.List[string]]::new()
$skipped = 0
$passed = 0

$online = $true
try { $null = Invoke-WebRequest 'https://registry.npmjs.org/-/ping' -TimeoutSec 10 } catch { $online = $false }
if (-not $online) { Write-Host 'npm registry unreachable: network tests are skipped.' -ForegroundColor Yellow }

$runs = @{}
# Runs review-deps.ps1 once per argument list (several tests read the same run) and returns its report and exit code.
function Invoke-Review {
    param([string[]]$Arguments)
    $key = $Arguments -join ' '
    if (-not $runs.ContainsKey($key)) {
        Push-Location $repoRoot
        try {
            $report = & pwsh -NoProfile -File $script @Arguments 2>$null
            $runs[$key] = [pscustomobject]@{ Report = ($report -join "`n"); ExitCode = $LASTEXITCODE }
        } finally { Pop-Location }
    }
    return $runs[$key]
}

function Test-Case {
    param([string]$Name, [switch]$Network, [scriptblock]$Body)
    if ($Network -and -not $online) {
        $script:skipped++
        Write-Host "  SKIP  $Name"
        return
    }
    try {
        & $Body
        $script:passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } catch {
        $script:failures.Add("$Name`n        $($_.Exception.Message)")
        Write-Host "  FAIL  $Name`n        $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Match {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) { throw "$Message (pattern: $Pattern)" }
}

function Assert-NoMatch {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -match $Pattern) { throw "$Message (matched: $($Matches[0]))" }
}

# The finding lines of a report: "- **STOP** ..." or "- **NOTE** ...".
function Get-Findings {
    param([string]$Report, [ValidateSet('STOP', 'NOTE')][string]$Kind)
    @($Report -split "`n" | Where-Object { $_.StartsWith("- **$Kind** ") })
}

# The lines that contain every one of the given substrings (plain text: -like would read the backticks as escapes).
function Select-Line {
    param([string[]]$Lines, [string[]]$Contains)
    @($Lines | Where-Object { $line = $_; -not @($Contains | Where-Object { -not $line.Contains($_) }).Count })
}

$tsgolintNatives = 'darwin-arm64', 'darwin-x64', 'linux-arm64', 'linux-x64', 'win32-arm64', 'win32-x64' |
    ForEach-Object { "@oxlint-tsgolint/$_" }

Write-Host 'review-deps.ps1'

Test-Case 'a diff with no dependency file reports nothing to review and exits 0' {
    $run = Invoke-Review '94a55ecd~1', '94a55ecd'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)"
    Assert-Match $run.Report '(?m)^# Dependency review' 'report title'
    Assert-Match $run.Report 'No dependency file changed' 'says nothing changed'
}

Test-Case 'an unknown ref fails with exit code 2 and no report' {
    $run = Invoke-Review 'no-such-ref-416', 'HEAD'
    Assert-True ($run.ExitCode -eq 2) "exit code $($run.ExitCode)"
    Assert-True ([string]::IsNullOrWhiteSpace($run.Report)) 'printed a report'
}

Test-Case 'a wildcard -AllowYoung is refused' {
    $run = Invoke-Review '94a55ecd~1', '94a55ecd', '-AllowYoung', '*'
    Assert-True ($run.ExitCode -eq 2) "exit code $($run.ExitCode)"
}

Test-Case 'an image name -AllowYoung is accepted, and noted when it clears nothing' {
    $run = Invoke-Review '94a55ecd~1', '94a55ecd', '-AllowYoung', 'ghcr.io/0xshug0/audio.cpp'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)"
    Assert-True ((Select-Line (Get-Findings $run.Report NOTE) '`ghcr.io/0xshug0/audio.cpp`', 'no age STOP').Count -eq 1) 'no NOTE'
}

Test-Case -Network 'a clean Bun bump has no STOP and exits 0' {
    $run = Invoke-Review '2b39c23d~1', '2b39c23d'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$($run.Report)"
    Assert-True ((Get-Findings $run.Report STOP).Count -eq 0) "STOP findings:`n$((Get-Findings $run.Report STOP) -join "`n")"
    Assert-Match $run.Report '(?m)^## src/Read2Me\.Web/bun\.lock' 'a section for the lockfile'
    Assert-Match $run.Report '(?m)^\| `@preact/signals-core` \| 1\.14\.4 \| 1\.14\.5 \|' 'a table row for the bump'
    Assert-Match $run.Report 'bun pm diff `@preact/signals-core@1\.14\.4\.\.1\.14\.5`' 'the bun pm diff output'
    Assert-Match $run.Report '\*\*Result: clean\*\*' 'the result line'
}

Test-Case -Network 'the report marks every finding STOP or NOTE and ends with a result line' {
    $run = Invoke-Review 'ee05965b~1', 'ee05965b', '-Now', '2026-11-15'
    $lines = $run.Report -split "`n"
    Assert-True ($lines[0] -eq '# Dependency review') "stdout starts with something else: $($lines[0])"
    $start = [array]::IndexOf($lines, '## Findings')
    Assert-True ($start -ge 0) 'a Findings section'
    $section = $lines[($start + 1)..($lines.Count - 1)] | ForEach-Object -Begin { $go = $true } -Process {
        if ($_ -like '## *') { $go = $false }
        if ($go) { $_ }
    }
    $items = @($section | Where-Object { $_ -like '- *' })
    Assert-True ($items.Count -gt 0) 'no findings listed'
    foreach ($item in $items) {
        Assert-Match $item '^- \*\*(STOP|NOTE)\*\* `[^`]+` \([^)]+\): \S' "finding format: $item"
    }
    Assert-Match $run.Report '\*\*Result: STOP\*\*' 'the result line'
}

Test-Case -Network 'a wrapper bump reviews every native package in the lockfile diff, each a binary NOTE' {
    $run = Invoke-Review 'ee05965b~1', 'ee05965b'
    foreach ($name in @('oxlint-tsgolint') + $tsgolintNatives) {
        Assert-Match $run.Report "(?m)^\| ``$([regex]::Escape($name))`` \| 7\.0\.2006 \| 7\.0\.2007 \|" "a row for $name"
    }
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`@oxlint-tsgolint/win32-x64@7.0.2007`', 'not diffable').Count -eq 1) "no binary NOTE for win32-x64:`n$($notes -join "`n")"
}

Test-Case -Network 'a version younger than 7 days is a STOP and exits 1' {
    $run = Invoke-Review 'ee05965b~1', 'ee05965b', '-Now', '2026-11-15'
    Assert-True ($run.ExitCode -eq 1) "exit code $($run.ExitCode)"
    $stops = Get-Findings $run.Report STOP
    foreach ($name in @('oxlint-tsgolint') + $tsgolintNatives) {
        Assert-True ((Select-Line $stops "``$name@7.0.2007``", 'days old').Count -eq 1) "no age STOP for $name`n$($stops -join "`n")"
    }
}

Test-Case -Network 'the same bump is clean once its versions are 7 days old' {
    $run = Invoke-Review 'ee05965b~1', 'ee05965b', '-Now', '2026-11-22'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
}

Test-Case -Network '-AllowYoung clears the age STOP for the named package only, and says so' {
    $run = Invoke-Review 'ee05965b~1', 'ee05965b', '-Now', '2026-11-15', '-AllowYoung', 'oxlint-tsgolint'
    Assert-True ($run.ExitCode -eq 1) "exit code $($run.ExitCode)"
    $stops = Get-Findings $run.Report STOP
    Assert-True ((Select-Line $stops '`oxlint-tsgolint@').Count -eq 0) "oxlint-tsgolint still STOPs:`n$($stops -join "`n")"
    Assert-True ($stops.Count -eq $tsgolintNatives.Count) "expected one STOP per native package:`n$($stops -join "`n")"
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`oxlint-tsgolint@7.0.2007`', '-AllowYoung').Count -eq 1) "no NOTE that -AllowYoung cleared it:`n$($notes -join "`n")"
}

Test-Case -Network '-AllowYoung naming every young package exits 0' {
    $run = Invoke-Review (@('ee05965b~1', 'ee05965b', '-Now', '2026-11-15', '-AllowYoung') + ((@('oxlint-tsgolint') + $tsgolintNatives) -join ','))
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
}

Test-Case -Network 'a package name new to the tree is a STOP' {
    $run = Invoke-Review '353fccd2~1', '353fccd2'
    Assert-True ($run.ExitCode -eq 1) "exit code $($run.ExitCode)"
    $stops = Get-Findings $run.Report STOP
    foreach ($name in '@typescript/typescript-win32-x64', '@typescript/typescript-linux-x64', '@typescript/typescript6') {
        Assert-True ((Select-Line $stops "``$name@", 'not in the base tree').Count -eq 1) "no new-package STOP for $name`n$($stops -join "`n")"
    }
    # typescript 6.0.3 stays in the tree under the alias @typescript/old, so the name typescript is not new.
    Assert-True ((Select-Line $stops '`typescript@', 'not in the base tree').Count -eq 0) 'typescript reported as a new name'
}

Test-Case -Network 'an npm lockfile bump reviews its transitive changes without false STOPs' {
    $run = Invoke-Review '108ed9c5~1', '108ed9c5', '-Now', '2026-12-31'
    Assert-Match $run.Report '(?m)^## Infra/ContainerHealth/package-lock\.json' 'a section for the lockfile'
    Assert-Match $run.Report '(?m)^\| `vite` \| 8\.1\.6 \| 8\.1\.7 \|' 'a row for vite'
    Assert-Match $run.Report 'npm diff `vite@8\.1\.6` `vite@8\.1\.7`' 'the npm diff output'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
    # rolldown 1.1.8 renamed its hashed bundles (binding-TuFFIE_J.mjs to binding-C5BEp1Z9.mjs); the same imports
    # moved files, so a whole-package count finds nothing new.
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`rolldown@1.1.8`').Count -eq 0) "false NOTE for rolldown:`n$($notes -join "`n")"
}

Test-Case -Network 'a Docker digest bump older than 7 days has no STOP and a row per Dockerfile' {
    $run = Invoke-Review '9f7e2278~1', '9f7e2278', '-Now', '2026-11-22'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
    foreach ($file in 'Dockerfile.minilm-l6', 'Dockerfile.mpnet-base-v2') {
        Assert-Match $run.Report "(?m)^## Infra/$([regex]::Escape($file))$" "a section for $file"
    }
    Assert-Match $run.Report '(?m)^\| `python:3\.13-slim` \| `d98cc29d3266` \| `e48f59cdba69` \|' 'a row for the python digest'
}

Test-Case -Network 'a Docker digest younger than 7 days is a STOP and exits 1' {
    $run = Invoke-Review '9f7e2278~1', '9f7e2278', '-Now', '2026-11-09'
    Assert-True ($run.ExitCode -eq 1) "exit code $($run.ExitCode)"
    $stops = Get-Findings $run.Report STOP
    Assert-True ((Select-Line $stops '`python:3.13-slim@e48f59cdba69`', 'days old').Count -eq 2) "no age STOP per Dockerfile:`n$($stops -join "`n")"
}

Test-Case -Network '-AllowYoung clears a Docker age STOP by image name' {
    $run = Invoke-Review '9f7e2278~1', '9f7e2278', '-Now', '2026-11-09', '-AllowYoung', 'python'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`python:3.13-slim@e48f59cdba69`', '-AllowYoung').Count -eq 2) "no -AllowYoung NOTE:`n$($notes -join "`n")"
}

Test-Case -Network 'a ghcr.io digest is aged by its created field, with a NOTE saying so' {
    $run = Invoke-Review '02489eae~1', '02489eae', '-Now', '2026-11-22'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
    Assert-Match $run.Report '(?m)^\| `ghcr\.io/0xshug0/audio\.cpp:full-cuda12` \| `5969cc86637c` \| `05a2e7de7ac4` \| 2026-10-27 \|' 'a row with the created date'
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`ghcr.io/0xshug0/audio.cpp:full-cuda12@05a2e7de7ac4`', '`created` field').Count -eq 1) "no created-field NOTE:`n$($notes -join "`n")"
}

Test-Case -Network 'a requirements.txt bump reads its wheels, and a .pth the old version shipped is a NOTE, not a STOP' {
    $run = Invoke-Review '20cd5e05~1', '20cd5e05'
    Assert-True ($run.ExitCode -eq 0) "exit code $($run.ExitCode)`n$((Get-Findings $run.Report STOP) -join "`n")"
    Assert-Match $run.Report '(?m)^## Infra/minilm-l6/requirements\.txt' 'a section for the lock'
    Assert-Match $run.Report '(?m)^\| `setuptools` \| 83\.0\.0 \| 84\.2\.1 \| 1 \| 1 kept \|' 'a row for setuptools'
    Assert-Match $run.Report '(?m)^\| `certifi` \| 2026\.6\.17 \| 2026\.10\.23 \| 1 \| none \|' 'a row for certifi'
    $notes = Get-Findings $run.Report NOTE
    Assert-True ((Select-Line $notes '`setuptools==84.2.1`', 'distutils-precedence.pth', 'as 83.0.0 did').Count -eq 2) "no kept-.pth NOTE per lock:`n$($notes -join "`n")"
}

Write-Host ''
Write-Host "$passed passed, $($failures.Count) failed, $skipped skipped"
if ($failures.Count -gt 0) { exit 1 }
