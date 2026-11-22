# Dot-source this, then call Write-DependencyFileWarning: it compares the working tree (committed, staged, unstaged and
# untracked files) with the merge base of HEAD and main (origin/main when there is no local main), and when a
# dependency file changed prints a prominent warning naming each one. check.ps1 calls it before its first stage
# (#420): the pre-merge review (review-deps.ps1) must read a dependency change before the gate builds and runs it.
#
# A warning only, never a block: a marker file or a hard stop could be faked, and the point is to catch the honest
# mistake of forgetting the review. A check that cannot run prints a one-line note and never throws.
#
# Dependency files, in any folder (names compared case-insensitively, node_modules ignored): Directory.Packages.props,
# package.json, bun.lock, package-lock.json, bunfig.toml, .npmrc, nuget.config, requirements.txt, requirements.in,
# uv.lock; and a Dockerfile, Dockerfile.* or docker-compose*.yml only when a line holding an @sha256: digest changed.

$script:DependencyFileNames = @('Directory.Packages.props', 'package.json', 'bun.lock', 'package-lock.json',
    'bunfig.toml', '.npmrc', 'nuget.config', 'requirements.txt', 'requirements.in', 'uv.lock')
$script:DigestFilePattern = '^(Dockerfile(\..+)?|docker-compose.*\.ya?ml)$'
$script:DigestPattern = '@sha256:[0-9a-f]{64}'

# Runs git in $Repo and returns its stdout lines; throws when it fails.
function Invoke-DependencyGit {
    param([string]$Repo)
    $output = & git -C $Repo @args 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
    return @($output | Where-Object { $_ })
}

# The first of $Base and origin/$Base that names a commit, or $null.
function Resolve-DependencyBase {
    param([string]$Repo, [string]$Base)
    foreach ($ref in $Base, "origin/$Base") {
        & git -C $Repo rev-parse --verify --quiet "$ref^{commit}" *> $null
        if ($LASTEXITCODE -eq 0) { return $ref }
    }
    return $null
}

# Repo-relative paths of the dependency files that differ between $MergeBase and the working tree, sorted.
function Get-ChangedDependencyFiles {
    param([string]$Repo, [string]$MergeBase)
    $tracked = @(Invoke-DependencyGit $Repo diff --name-only --no-renames $MergeBase)
    $untracked = @(Invoke-DependencyGit $Repo ls-files --others --exclude-standard)
    $changed = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $tracked + $untracked) {
        if ($path -match '(^|/)node_modules/') { continue }
        $leaf = Split-Path -Leaf $path
        if ($leaf -in $script:DependencyFileNames) { [void]$changed.Add($path); continue }
        if ($leaf -notmatch $script:DigestFilePattern) { continue }
        $digestChanged = if ($path -in $untracked) {
            (Get-Content -Raw -LiteralPath (Join-Path $Repo $path)) -match $script:DigestPattern
        }
        else {
            # The diff's own +++/--- header lines name files, not digests, so they never match.
            @(Invoke-DependencyGit $Repo diff -U0 --no-renames $MergeBase -- $path |
                Where-Object { $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---) ' -and $_ -match $script:DigestPattern }).Count -gt 0
        }
        if ($digestChanged) { [void]$changed.Add($path) }
    }
    return @($changed)
}

function Write-DependencyFileWarning {
    param([string]$RepoRoot, [string]$Base = 'main')
    try {
        $ref = Resolve-DependencyBase $RepoRoot $Base
        if (-not $ref) { throw "no '$Base' or 'origin/$Base' branch to compare with" }
        $mergeBase = Invoke-DependencyGit $RepoRoot merge-base $ref HEAD | Select-Object -First 1
        $changed = @(Get-ChangedDependencyFiles $RepoRoot $mergeBase)
    }
    catch {
        Write-Host "Dependency-file check skipped: $($_.Exception.Message)." -ForegroundColor DarkGray
        return
    }
    if ($changed.Count -eq 0) { return }
    $rule = '!' * 78
    Write-Host ''
    Write-Host $rule -ForegroundColor Yellow
    Write-Host "WARNING: dependency files changed since the merge base with $ref ($($mergeBase.Substring(0, 8))):" -ForegroundColor Yellow
    $changed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host 'Run the pre-merge review first, before this gate builds and runs the new code (it compares commits, so' -ForegroundColor Yellow
    Write-Host 'commit the change first), and read the report:' -ForegroundColor Yellow
    Write-Host "  pwsh scripts/review-deps.ps1 $ref HEAD > review.md" -ForegroundColor Yellow
    Write-Host 'Carrying on with the gate.' -ForegroundColor Yellow
    Write-Host $rule -ForegroundColor Yellow
    Write-Host ''
}
