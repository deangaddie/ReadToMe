<#
.SYNOPSIS
Script-level tests for dependency-files.ps1, the check behind check.ps1's "dependency files changed" warning.

.DESCRIPTION
Each test builds a throwaway git repository in the temp folder with a `main` branch holding a few dependency files,
changes it (a commit on a branch, a staged or unstaged edit, an untracked file), and checks whether
Get-DependencyFileWarning returns a warning, and which files it names. Offline; exits 1 on any failure.

.EXAMPLE
pwsh scripts/dependency-files.tests.ps1
#>
#Requires -Version 7
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dependency-files.ps1')
$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0

$digestA = 'a' * 64
$digestB = 'b' * 64

function Invoke-FixtureGit {
    param([string]$Repo)
    $output = & git -C $Repo -c user.name=test -c user.email=test@example.invalid -c commit.gpgsign=false @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $output" }
}

function Set-FixtureFile {
    param([string]$Repo, [string]$Path, [string]$Text)
    $full = Join-Path $Repo $Path
    New-Item -ItemType Directory -Force (Split-Path -Parent $full) | Out-Null
    Set-Content -LiteralPath $full -Value $Text -NoNewline
}

# A repository on branch `work`, forked from a `main` that holds a few dependency files and a README.
function New-FixtureRepo {
    $repo = Join-Path ([System.IO.Path]::GetTempPath()) "r2m-depfiles-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory $repo | Out-Null
    Invoke-FixtureGit $repo init --quiet --initial-branch=main
    Set-FixtureFile $repo 'README.md' "readme`n"
    Set-FixtureFile $repo 'src/Web/package.json' "{ `"name`": `"web`" }`n"
    Set-FixtureFile $repo 'src/Web/bun.lock' "{}`n"
    Set-FixtureFile $repo 'Infra/Dockerfile.app' "# app image`nFROM python:3.13-slim@sha256:$digestA AS deps`nRUN echo hi`n"
    Set-FixtureFile $repo 'Infra/docker-compose.yml' "services:`n  app:`n    image: ghcr.io/o/app:1@sha256:$digestA`n"
    Invoke-FixtureGit $repo add -A
    Invoke-FixtureGit $repo commit --quiet -m base
    Invoke-FixtureGit $repo checkout --quiet -b work
    return $repo
}

# $Expected: the files the warning must name, none for no warning, or 'NOTE' for the one-line skipped note.
function Test-Case {
    param([string]$Name, [scriptblock]$Arrange, [string[]]$Expected)
    $repo = New-FixtureRepo
    try {
        & $Arrange $repo
        $warning = @(Get-DependencyFileWarning -RepoRoot $repo -Base main)
        $text = $warning -join "`n"
        if ($Expected -eq 'NOTE') {
            if ($warning.Count -ne 1 -or $text -notmatch 'skipped') { throw "expected the skipped note, got:`n$text" }
        }
        elseif ($Expected.Count -eq 0) {
            if ($warning.Count -ne 0) { throw "expected no warning, got:`n$text" }
        }
        else {
            if ($text -notmatch 'review-deps\.ps1') { throw "the warning does not point at review-deps.ps1:`n$text" }
            foreach ($path in $Expected) {
                if ($text -notmatch [regex]::Escape($path)) { throw "the warning does not name $path`:`n$text" }
            }
            $named = @($warning | Where-Object { $_ -match '^\s+- ' })
            if ($named.Count -ne $Expected.Count) { throw "expected $($Expected.Count) file(s) named, got $($named.Count):`n$text" }
        }
        $script:passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    catch {
        $script:failures.Add("$Name`n        $($_.Exception.Message)")
        Write-Host "  FAIL  $Name`n        $($_.Exception.Message)" -ForegroundColor Red
    }
    finally {
        Remove-Item -Recurse -Force $repo -ErrorAction SilentlyContinue
    }
}

Test-Case 'no change: no warning' { param($r) } @()

Test-Case 'a docs-only commit: no warning' {
    param($r)
    Set-FixtureFile $r 'README.md' "readme, edited`n"
    Invoke-FixtureGit $r commit --quiet -am docs
} @()

Test-Case 'a committed bun.lock change warns' {
    param($r)
    Set-FixtureFile $r 'src/Web/bun.lock' "{ `"changed`": true }`n"
    Invoke-FixtureGit $r commit --quiet -am bump
} @('src/Web/bun.lock')

Test-Case 'an unstaged package.json edit warns' {
    param($r)
    Set-FixtureFile $r 'src/Web/package.json' "{ `"name`": `"web`", `"dependencies`": {} }`n"
} @('src/Web/package.json')

Test-Case 'a staged Directory.Packages.props warns' {
    param($r)
    Set-FixtureFile $r 'src/Directory.Packages.props' "<Project />`n"
    Invoke-FixtureGit $r add -A
} @('src/Directory.Packages.props')

Test-Case 'untracked requirements.txt, .npmrc and NuGet.Config warn' {
    param($r)
    Set-FixtureFile $r 'Infra/svc/requirements.txt' "certifi==1`n"
    Set-FixtureFile $r 'tools/x/.npmrc' "min-release-age=7`n"
    Set-FixtureFile $r 'NuGet.Config' "<configuration />`n"
} @('Infra/svc/requirements.txt', 'tools/x/.npmrc', 'NuGet.Config')

Test-Case 'bunfig.toml, package-lock.json and uv.lock warn' {
    param($r)
    Set-FixtureFile $r 'src/Web/bunfig.toml' "[install]`n"
    Set-FixtureFile $r 'Infra/h/package-lock.json' "{}`n"
    Set-FixtureFile $r 'Infra/svc/uv.lock' "version = 1`n"
    Invoke-FixtureGit $r add -A
    Invoke-FixtureGit $r commit --quiet -m more
} @('src/Web/bunfig.toml', 'Infra/h/package-lock.json', 'Infra/svc/uv.lock')

Test-Case 'a Dockerfile digest bump warns' {
    param($r)
    Set-FixtureFile $r 'Infra/Dockerfile.app' "# app image`nFROM python:3.13-slim@sha256:$digestB AS deps`nRUN echo hi`n"
    Invoke-FixtureGit $r commit --quiet -am digest
} @('Infra/Dockerfile.app')

Test-Case 'a docker-compose.yml digest bump warns' {
    param($r)
    Set-FixtureFile $r 'Infra/docker-compose.yml' "services:`n  app:`n    image: ghcr.io/o/app:1@sha256:$digestB`n"
} @('Infra/docker-compose.yml')

Test-Case 'a Dockerfile or compose edit that leaves the digests alone: no warning' {
    param($r)
    Set-FixtureFile $r 'Infra/Dockerfile.app' "# app image, edited`nFROM python:3.13-slim@sha256:$digestA AS deps`nRUN echo bye`n"
    Set-FixtureFile $r 'Infra/docker-compose.yml' "services:`n  app:`n    image: ghcr.io/o/app:1@sha256:$digestA`n    ports: [`"80:80`"]`n"
} @()

Test-Case 'a new Dockerfile with a digest warns' {
    param($r)
    Set-FixtureFile $r 'Infra/Dockerfile.new' "FROM ubuntu:24.04@sha256:$digestB`n"
} @('Infra/Dockerfile.new')

Test-Case 'files under node_modules: no warning' {
    param($r)
    Set-FixtureFile $r 'src/Web/node_modules/left-pad/package.json' "{}`n"
} @()

Test-Case 'on main itself, an uncommitted bun.lock edit warns' {
    param($r)
    Invoke-FixtureGit $r checkout --quiet main
    Set-FixtureFile $r 'src/Web/bun.lock' "{ `"changed`": true }`n"
} @('src/Web/bun.lock')

Test-Case 'a missing base ref: a note, not a failure' {
    param($r)
    Invoke-FixtureGit $r branch --quiet -m main trunk
} 'NOTE'

Write-Host ''
if ($failures.Count) {
    Write-Host "$($failures.Count) failed, $passed passed." -ForegroundColor Red
    exit 1
}
Write-Host "All $passed passed." -ForegroundColor Green
