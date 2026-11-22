# Dot-source this, then call Get-DependencyFileWarning: it compares the working tree (committed, staged, unstaged and
# untracked files) with the merge base of HEAD and a base branch, and returns warning lines naming every dependency
# file that changed, or nothing when none did. check.ps1 prints them before its first stage (#420): the pre-merge
# review (review-deps.ps1) must read a dependency change before the gate builds and runs it.
#
# A warning only, never a block: a marker file or a hard stop could be faked, and the point is to catch the honest
# mistake of forgetting the review.
#
# Dependency files, in any folder (names compared case-insensitively, node_modules ignored): Directory.Packages.props,
# package.json, bun.lock, package-lock.json, bunfig.toml, .npmrc, nuget.config, requirements.txt, requirements.in,
# uv.lock; and a Dockerfile, Dockerfile.* or docker-compose*.yml only when a line holding an @sha256: digest changed.

$script:DependencyFileNames = @('Directory.Packages.props', 'package.json', 'bun.lock', 'package-lock.json',
    'bunfig.toml', '.npmrc', 'nuget.config', 'requirements.txt', 'requirements.in', 'uv.lock')
$script:DigestFilePattern = '^(Dockerfile(\..+)?|docker-compose.*\.ya?ml)$'
$script:DigestPattern = '@sha256:[0-9a-f]{64}'

# Runs git in $Repo and returns its stdout lines, or $null when it fails (stderr dropped).
function Invoke-DependencyGit {
    param([string]$Repo)
    $output = & git -C $Repo @args 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return @($output)
}

# The first of $Base and origin/$Base that names a commit, or $null.
function Resolve-DependencyBase {
    param([string]$Repo, [string]$Base)
    foreach ($ref in $Base, "origin/$Base") {
        $commit = Invoke-DependencyGit $Repo rev-parse --verify --quiet "$ref^{commit}"
        if ($commit) { return $ref }
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
        if (-not $path -or $path -match '(^|/)node_modules/') { continue }
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

function Get-DependencyFileWarning {
    param([string]$RepoRoot, [string]$Base = 'main')
    $ref = Resolve-DependencyBase $RepoRoot $Base
    $mergeBase = if ($ref) { Invoke-DependencyGit $RepoRoot merge-base $ref HEAD | Select-Object -First 1 }
    if (-not $mergeBase) {
        return "Dependency-file check skipped: no merge base with '$Base' or 'origin/$Base' to compare with."
    }
    $changed = @(Get-ChangedDependencyFiles $RepoRoot $mergeBase)
    if ($changed.Count -eq 0) { return @() }
    $lines = @(
        "WARNING: dependency files changed since the merge base with $ref ($($mergeBase.Substring(0, 8))):"
        $changed | ForEach-Object { "  - $_" }
        "Run the pre-merge review first, before this gate builds and runs the new code (it compares commits, so"
        "commit the change first), and read the report:"
        "  pwsh scripts/review-deps.ps1 $ref HEAD > review.md"
        "Carrying on with the gate."
    )
    return $lines
}
