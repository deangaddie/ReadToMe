<#
.SYNOPSIS
Reviews every dependency change between two git refs before the gate builds it, and prints a Markdown report.

.DESCRIPTION
A dependency bump runs its new code on the host during `bun run check`, `npm run check` or `dotnet build`, with the
developer's identity. Run this first, on the PR's base and head, and read the report before running anything else.

It never installs, builds or runs the new code. It reads the git diff, asks the npm registry for publish dates and
provenance, downloads the old and new tarballs and reads them in memory (nothing is unpacked to disk), and runs
`bun pm diff` / `npm diff`, which only download and compare tarballs. Credential-looking environment variables are
removed for the run (scripts/credential-variables.ps1, the list check.ps1 uses), and a package or version the registry
no longer has is a STOP.

The lockfiles are compared at the merge base of the two refs and at <Head>, so every package whose version changed or
that appeared is reviewed, transitive ones included. A wrapper bump such as oxlint-tsgolint is reviewed through its
native packages too, since those hold the code that runs.

Reviewed files (any folder): bun.lock (Bun) and package-lock.json (npm). NuGet, Docker and uv.lock checks plug into
$Reviewers below and add their findings to the same report.

Each finding is STOP or NOTE. The exit code is 1 on any STOP, 0 otherwise, and 2 when the review itself could not run
(bad ref, registry unreachable, tool missing); then no report is printed.

STOP, per changed package:
  - a version published less than 7 days before -Now (backs up the release-age guards in bunfig.toml and .npmrc)
  - a package name not in the base tree, direct or transitive
  - a new or changed preinstall/install/postinstall script, a new binding.gyp (npm builds it on install), a new
    hasInstallScript in package-lock.json, or a new or changed bin entry
  - provenance attestation on the old version but not on the new
  - a tarball whose hash does not match the lockfile, a non-registry source, or a diff tool that failed
NOTE:
  - more child_process, fs or network-module imports, or more eval / new Function / fetch / process.env use, counted
    across the whole old and new package (so renamed, hashed bundle files do not count as new code)
  - changed binary files, which are not diffable and were not inspected
  - an age STOP cleared by -AllowYoung

The progress log goes to stderr, the report to stdout: `pwsh scripts/review-deps.ps1 origin/main pr > review.md`.

.PARAMETER Base
The ref the change goes into, usually origin/main.

.PARAMETER Head
The ref holding the change, usually the PR branch.

.PARAMETER AllowYoung
Package names whose age STOP is cleared, one name per entry: -AllowYoung a, b. Wildcards are refused; there is no
blanket skip. Only the human uses it, for a security fix that cannot wait 7 days. The report says what it cleared.

.PARAMETER Now
The time ages are measured against (default: now). For replaying an old review and for the tests.

.EXAMPLE
pwsh scripts/review-deps.ps1 origin/main origin/dependabot/bun/src/Read2Me.Web/web-1234

.EXAMPLE
pwsh scripts/review-deps.ps1 origin/main HEAD -AllowYoung undici
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Base,
    [Parameter(Mandatory, Position = 1)][string]$Head,
    [string[]]$AllowYoung = @(),
    [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$minAgeDays = 7
$registry = 'https://registry.npmjs.org'

$findings = [System.Collections.Generic.List[object]]::new()
$clearedYoung = [System.Collections.Generic.HashSet[string]]::new()

# One finding in the report. $Subject is the package@version (or file) it is about, $Source the file that brought it.
function Add-Finding {
    param([ValidateSet('STOP', 'NOTE')][string]$Kind, [string]$Subject, [string]$Source, [string]$Text)
    $findings.Add([pscustomobject]@{ Kind = $Kind; Subject = $Subject; Source = $Source; Text = $Text })
}

function Write-Log { param([string]$Text) [Console]::Error.WriteLine($Text) }

# Runs git and returns its stdout lines; stderr stays out of them (a warning must not end up in a lockfile's text).
function Invoke-Git {
    $all = & git -C $repoRoot @args 2>&1
    $code = $LASTEXITCODE
    $errors = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    if ($code -ne 0) {
        $detail = if ($errors.Count) { $errors -join ' ' } else { "exit code $code" }
        throw "git $($args -join ' ') failed: $detail"
    }
    return @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
}

# The file's text at a commit, or $null when it does not exist there.
function Get-FileAt {
    param([string]$Commit, [string]$Path)
    & git -C $repoRoot cat-file -e "${Commit}:$Path" 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (Invoke-Git show "${Commit}:$Path") -join "`n"
}

#region Lockfiles

# A lockfile entry, the same shape for every ecosystem: Key is the lockfile's own key (an alias or node_modules path),
# Name the registry package name. Registry is false for git, file, workspace and other non-registry sources.
function New-Entry {
    param($Key, $Name, $Version, $Integrity, [bool]$HasInstallScript, [bool]$Registry)
    [pscustomobject]@{
        Key = $Key; Name = $Name; Version = $Version; Integrity = $Integrity
        HasInstallScript = $HasInstallScript; Registry = $Registry
    }
}

# bun.lock (text format, JSON with trailing commas): "key": ["name@version", "registry", { meta }, "integrity"].
function Read-BunLock {
    param([string]$Text)
    if (-not $Text) { return @() }
    $lock = $Text | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($key in $lock.packages.Keys) {
        $tuple = $lock.packages[$key]
        $spec = [string]$tuple[0]
        if ($spec -match '^(@?[^@]+)@(\d+\.\d+\.\d+[^@]*)$' -and [string]::IsNullOrEmpty([string]$tuple[1])) {
            New-Entry $key $Matches[1] $Matches[2] ([string]$tuple[-1]) $false $true
        } else {
            $name = if ($spec -match '^(@?[^@]+)@') { $Matches[1] } else { $spec }
            New-Entry $key $name $spec $null $false $false
        }
    }
}

# package-lock.json v2/v3: "packages": { "node_modules/a/node_modules/b": { version, resolved, integrity, ... } }.
function Read-NpmLock {
    param([string]$Text)
    if (-not $Text) { return @() }
    $lock = $Text | ConvertFrom-Json -AsHashtable -DateKind String
    if (-not $lock.packages) { throw "package-lock.json has no ""packages"" (lockfileVersion $($lock.lockfileVersion)); v2 or v3 is needed." }
    foreach ($key in $lock.packages.Keys) {
        if ($key -eq '' -or $key -notlike '*node_modules/*') { continue }   # the root project and workspace folders
        $p = $lock.packages[$key]
        if ($p.link) { continue }
        $name = if ($p.name) { $p.name } else { $key.Substring($key.LastIndexOf('node_modules/') + 'node_modules/'.Length) }
        $fromRegistry = [string]$p.resolved -like "$registry/*"
        New-Entry $key $name ([string]$p.version) ([string]$p.integrity) ([bool]$p.hasInstallScript) $fromRegistry
    }
}

# What changed between two lockfiles: one item per name/old/new triple. Old is $null for a name not in the base tree.
function Get-LockChanges {
    param([object[]]$BaseEntries, [object[]]$HeadEntries)
    $baseByKey = @{}
    $baseByName = @{}
    foreach ($e in $BaseEntries) {
        $baseByKey[$e.Key] = $e
        if (-not $baseByName.ContainsKey($e.Name)) { $baseByName[$e.Name] = [System.Collections.Generic.List[object]]::new() }
        $baseByName[$e.Name].Add($e)
    }
    $seen = @{}
    foreach ($new in $HeadEntries) {
        $old = $baseByKey[$new.Key]
        if ($old -and $old.Name -eq $new.Name -and $old.Version -eq $new.Version) { continue }
        if (-not $old -or $old.Name -ne $new.Name) {
            $old = $null
            if ($baseByName.ContainsKey($new.Name)) {
                if (@($baseByName[$new.Name] | Where-Object Version -eq $new.Version).Count) { continue }   # moved, not changed
                $old = $baseByName[$new.Name] | Where-Object Registry | Sort-Object { [version]($_.Version -replace '[-+].*', '') } | Select-Object -Last 1
                if (-not $old) { $old = $baseByName[$new.Name][0] }
            }
        }
        $id = "$($new.Name)|$($old.Version)|$($new.Version)"
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        [pscustomobject]@{ Name = $new.Name; Old = $old; New = $new; NewName = -not $baseByName.ContainsKey($new.Name) }
    }
}

#endregion

#region Registry and tarballs

# A package's full registry document (publish times, every version's manifest), or $null when the registry has no
# such package (404). Any other failure throws: the review cannot run without the registry.
$packuments = @{}
function Get-Packument {
    param([string]$Name)
    if (-not $packuments.ContainsKey($Name)) {
        Write-Log "  registry: $Name"
        $url = "$registry/$($Name -replace '/', '%2f')"
        try {
            $response = Invoke-WebRequest $url -Headers @{ Accept = 'application/json' }
            $packuments[$Name] = $response.Content | ConvertFrom-Json -AsHashtable -DateKind String
        } catch {
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $packuments[$Name] = $null }
            else { throw }
        }
    }
    return $packuments[$Name]
}

function Get-IntegrityMatch {
    param([byte[]]$Bytes, [string]$Integrity)
    if (-not $Integrity) { return $null }
    foreach ($sri in $Integrity -split '\s+') {
        if ($sri -notmatch '^(sha512|sha384|sha256|sha1)-(.+)$') { continue }
        $hash = switch ($Matches[1]) {
            'sha512' { [System.Security.Cryptography.SHA512]::HashData($Bytes) }
            'sha384' { [System.Security.Cryptography.SHA384]::HashData($Bytes) }
            'sha256' { [System.Security.Cryptography.SHA256]::HashData($Bytes) }
            'sha1' { [System.Security.Cryptography.SHA1]::HashData($Bytes) }
        }
        return [Convert]::ToBase64String($hash) -eq $Matches[2]
    }
    return $null
}

$codeExtensions = '.js', '.mjs', '.cjs', '.jsx', '.ts', '.mts', '.cts', '.tsx'
$binaryExtensions = '.node', '.exe', '.dll', '.so', '.dylib', '.wasm', '.bin', '.a', '.lib', '.pdb'
# require('x'), import('x'), import 'x' and ... from 'x', with or without the node: prefix.
$importOf = { param($Modules) "(?:require\s*\(\s*|\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)['""](?:node:)?(?:$Modules)['""]" }
$riskPatterns = [ordered]@{
    'child_process imports' = & $importOf 'child_process'
    'fs imports'            = & $importOf 'fs|fs/promises'
    'network imports'       = & $importOf 'net|http|https|http2|dgram|tls|dns'
    'eval calls'            = '(?<![.\w$])eval\s*\(|\bnew\s+Function\s*\('
    'fetch calls'           = '(?<![.\w$])fetch\s*\('
    'process.env reads'     = '\bprocess\.env\b'
}
$riskRegexes = @{}
foreach ($k in $riskPatterns.Keys) { $riskRegexes[$k] = [regex]::new($riskPatterns[$k], 'Compiled') }

# Downloads a version's tarball and reads it in memory: its package.json, a hash per file, which files are binary, and
# the risky-pattern counts over all code files. Nothing is written to disk.
function Read-Tarball {
    param([string]$Name, [string]$Version, [string]$LockIntegrity)
    $manifest = (Get-Packument $Name).versions[$Version]
    Write-Log "  tarball: $Name@$Version"
    $bytes = (Invoke-WebRequest $manifest.dist.tarball).Content
    if ($bytes -is [string]) { throw "Downloading $($manifest.dist.tarball) returned text, not a tarball." }

    $files = @{}
    $binary = [System.Collections.Generic.HashSet[string]]::new()
    $counts = [ordered]@{}
    foreach ($k in $riskPatterns.Keys) { $counts[$k] = 0 }
    $packageJson = $null
    $gzip = [System.IO.Compression.GZipStream]::new([System.IO.MemoryStream]::new($bytes), [System.IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    try {
        while ($entry = $tar.GetNextEntry()) {
            if (-not $entry.DataStream) { continue }
            $path = $entry.Name -replace '^[^/]+/', ''   # npm tarballs nest everything under one folder, usually package/
            $buffer = [System.IO.MemoryStream]::new()
            $entry.DataStream.CopyTo($buffer)
            $data = $buffer.ToArray()
            $files[$path] = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($data))
            $ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
            $hasNul = $data.Length -and [Array]::IndexOf($data, [byte]0, 0, [math]::Min($data.Length, 8000)) -ge 0
            if ($ext -in $binaryExtensions -or $hasNul) { [void]$binary.Add($path); continue }
            if ($path -eq 'package.json') { $packageJson = [System.Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json -AsHashtable -DateKind String }
            $isDeclaration = $path -match '\.d\.[mc]?ts$'
            if (($ext -in $codeExtensions -or ($ext -eq '' -and $path -like 'bin/*')) -and -not $isDeclaration) {
                $text = [System.Text.Encoding]::UTF8.GetString($data)
                foreach ($k in $riskPatterns.Keys) { $counts[$k] += $riskRegexes[$k].Matches($text).Count }
            }
        }
    } finally { $tar.Dispose(); $gzip.Dispose() }

    [pscustomobject]@{
        Manifest = $manifest
        PackageJson = if ($packageJson) { $packageJson } else { @{} }
        Files = $files
        Binary = $binary
        Counts = $counts
        RegistryIntegrity = Get-IntegrityMatch $bytes $manifest.dist.integrity
        LockIntegrity = Get-IntegrityMatch $bytes $LockIntegrity
    }
}

# A package's bin entries as name -> target. "bin": "./cli.js" names the command after the package.
function Get-Bins {
    param($PackageJson, [string]$Name)
    $bin = $PackageJson.bin
    $result = @{}
    if ($bin -is [string]) { $result[($Name -replace '^@[^/]+/', '')] = $bin -replace '^\./', '' }
    elseif ($bin -is [System.Collections.IDictionary]) { foreach ($k in $bin.Keys) { $result[$k] = [string]$bin[$k] -replace '^\./', '' } }
    return $result
}

function Format-Date { param($Iso) if ($Iso) { ([DateTimeOffset]::Parse($Iso)).ToUniversalTime().ToString('yyyy-MM-dd') } else { '?' } }

#endregion

#region Diff tools

# bun pm diff and npm diff run in an empty folder, so no project config (bunfig.toml, .npmrc) changes what they fetch.
$toolDir = Join-Path ([System.IO.Path]::GetTempPath()) "review-deps-$PID"
$bunExe = $null
$npmExe = $null

function Invoke-Tool {
    param([string]$Exe, [string[]]$Arguments)
    Push-Location $toolDir
    try {
        $out = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    return @{ Output = @($out); ExitCode = $code }
}

# `bun pm diff` is not in `bun pm --help` and its exit codes are undocumented: success is its summary line, not exit 0.
# Always both versions: a bare name compares the locked version against latest.
function Invoke-BunDiff {
    param([string]$Name, [string]$Old, [string]$New)
    if (-not $script:bunExe) {
        . (Join-Path $PSScriptRoot 'find-bun.ps1')
        $script:bunExe = Find-Bun -WebDir (Join-Path $repoRoot 'src/Read2Me.Web')
    }
    $run = Invoke-Tool $script:bunExe @('pm', 'diff', "$Name@$Old..$New", '--stat')
    $ok = @($run.Output | Where-Object { $_ -match '^\d+ files? changed' }).Count -gt 0
    return @{ Title = "bun pm diff ``$Name@$Old..$New`` --stat"; Full = "``bun pm diff $Name@$Old..$New``"; Output = $run.Output; Ok = $ok }
}

function Invoke-NpmDiff {
    param([string]$Name, [string]$Old, [string]$New)
    if (-not $script:npmExe) {
        $script:npmExe = (Get-Command npm -ErrorAction SilentlyContinue).Source
        if (-not $script:npmExe) { throw 'npm is not on PATH; it is needed to diff package-lock.json changes.' }
    }
    $run = Invoke-Tool $script:npmExe @('diff', "--diff=$Name@$Old", "--diff=$Name@$New", '--diff-name-only')
    $ok = $run.ExitCode -eq 0 -and -not @($run.Output | Where-Object { $_ -match '^npm (error|ERR!)' }).Count
    return @{ Title = "npm diff ``$Name@$Old`` ``$Name@$New`` --diff-name-only"; Full = "``npm diff --diff=$Name@$Old --diff=$Name@$New``"; Output = $run.Output; Ok = $ok }
}

#endregion

#region Package review

# Reviews one changed package (Bun or npm, both on the npm registry). Adds its findings and returns a table row and
# the diff tool's output for the report.
function Invoke-PackageReview {
    param($Change, [string]$Source, [scriptblock]$Diff)
    $name = $Change.Name
    $new = $Change.New
    $old = $Change.Old
    $subject = "$name@$($new.Version)"
    Write-Log "$Source  $name $(if ($old) { $old.Version } else { '(new)' }) -> $($new.Version)"

    if (-not $new.Registry) {
        Add-Finding STOP $name $Source "comes from a non-registry source ($($new.Version)); review it by hand."
        return @{ Row = "| ``$name`` | $(if ($old) { $old.Version } else { '-' }) | $($new.Version) | - | - | - | - |"; Diff = $null }
    }
    if ($Change.NewName) {
        Add-Finding STOP $subject $Source 'is a package name not in the base tree; check why it was added and who publishes it.'
    }
    if ($old -and -not $old.Registry) {
        Add-Finding STOP $subject $Source "replaces a non-registry source ($($old.Version)); review it by hand."
        $old = $null
    }

    # A package or version gone from the registry (unpublished, often because it was malicious) cannot be reviewed.
    $packument = Get-Packument $name
    if (-not $packument -or -not $packument.versions[$new.Version]) {
        Add-Finding STOP $subject $Source 'is not on the registry (unpublished or never published); review it by hand.'
        return @{ Row = "| ``$name`` | $(if ($old) { $old.Version } else { '-' }) | $($new.Version) | - | - | - | - |"; Diff = $null }
    }
    if ($old -and -not $packument.versions[$old.Version]) {
        Add-Finding STOP $subject $Source "replaces $($old.Version), which is no longer on the registry, so there is nothing to compare against."
        $old = $null
    }

    # Age
    $published = $packument.time[$new.Version]
    $ageText = '?'
    if (-not $published) {
        Add-Finding STOP $subject $Source 'has no publish date on the registry.'
    } else {
        $age = ($Now - [DateTimeOffset]::Parse($published)).TotalDays
        $ageText = '{0:0.0}' -f $age
        if ($age -lt $minAgeDays) {
            $text = "was published $(Format-Date $published), $ageText days old; the minimum is $minAgeDays days."
            if ($AllowYoung -contains $name) {
                [void]$clearedYoung.Add($name)
                Add-Finding NOTE $subject $Source "$text The age STOP is cleared by -AllowYoung."
            } else {
                Add-Finding STOP $subject $Source $text
            }
        }
    }

    # Tarballs
    $newPkg = Read-Tarball $name $new.Version $new.Integrity
    $oldPkg = if ($old) { Read-Tarball $name $old.Version $old.Integrity } else { $null }
    foreach ($p in @(@{ Pkg = $newPkg; Version = $new.Version }, @{ Pkg = $oldPkg; Version = $old.Version })) {
        if (-not $p.Pkg) { continue }
        if ($p.Pkg.LockIntegrity -eq $false) { Add-Finding STOP "$name@$($p.Version)" $Source 'tarball does not match the lockfile integrity hash.' }
        if ($p.Pkg.RegistryIntegrity -eq $false) { Add-Finding STOP "$name@$($p.Version)" $Source 'tarball does not match the registry integrity hash.' }
    }

    # Provenance
    $hasProvenance = { param($pkg) [bool]($pkg -and $pkg.Manifest.dist.attestations -and $pkg.Manifest.dist.attestations.provenance) }
    $newProvenance = & $hasProvenance $newPkg
    $oldProvenance = & $hasProvenance $oldPkg
    if ($oldProvenance -and -not $newProvenance) {
        Add-Finding STOP $subject $Source "has no provenance attestation, though $($old.Version) had one."
    }
    $provenanceText = if ($newProvenance) { 'yes' } elseif ($oldProvenance) { '**lost**' } else { 'no' }

    # Install scripts and bin
    foreach ($hook in 'preinstall', 'install', 'postinstall') {
        $current = if ($newPkg.PackageJson.scripts) { [string]$newPkg.PackageJson.scripts[$hook] } else { '' }
        $previous = if ($oldPkg -and $oldPkg.PackageJson.scripts) { [string]$oldPkg.PackageJson.scripts[$hook] } else { '' }
        if ($current -and $current -ne $previous) {
            $what = if ($previous) { 'changed' } else { 'new' }
            Add-Finding STOP $subject $Source "has a $what ``$hook`` script: ``$current``."
        }
    }
    if ($newPkg.Files.ContainsKey('binding.gyp') -and -not ($oldPkg -and $oldPkg.Files.ContainsKey('binding.gyp'))) {
        Add-Finding STOP $subject $Source 'has a new binding.gyp, which npm builds on install.'
    }
    if ($new.HasInstallScript -and -not ($old -and $old.HasInstallScript)) {
        Add-Finding STOP $subject $Source 'is newly marked hasInstallScript in the lockfile.'
    }
    $newBins = Get-Bins $newPkg.PackageJson $name
    $oldBins = if ($oldPkg) { Get-Bins $oldPkg.PackageJson $name } else { @{} }
    foreach ($bin in $newBins.Keys) {
        if (-not $oldBins.ContainsKey($bin)) { Add-Finding STOP $subject $Source "has a new bin ``$bin`` -> ``$($newBins[$bin])``." }
        elseif ($oldBins[$bin] -ne $newBins[$bin]) { Add-Finding STOP $subject $Source "bin ``$bin`` now points at ``$($newBins[$bin])`` (was ``$($oldBins[$bin])``)." }
    }

    # Risky patterns, net over the whole package
    foreach ($k in $riskPatterns.Keys) {
        $was = if ($oldPkg) { $oldPkg.Counts[$k] } else { 0 }
        $current = $newPkg.Counts[$k]
        if ($current -gt $was) { Add-Finding NOTE $subject $Source "has more $k across the package: $was -> $current." }
    }

    # Files
    $oldFiles = if ($oldPkg) { $oldPkg.Files } else { @{} }
    $added = @($newPkg.Files.Keys | Where-Object { -not $oldFiles.ContainsKey($_) })
    $removed = @($oldFiles.Keys | Where-Object { -not $newPkg.Files.ContainsKey($_) })
    $changed = @($newPkg.Files.Keys | Where-Object { $oldFiles.ContainsKey($_) -and $oldFiles[$_] -ne $newPkg.Files[$_] })
    $binaryTouched = @(($added + $changed) | Where-Object { $newPkg.Binary.Contains($_) } | Sort-Object)
    if ($binaryTouched.Count) {
        $list = ($binaryTouched | Select-Object -First 5 | ForEach-Object { "``$_``" }) -join ', '
        if ($binaryTouched.Count -gt 5) { $list += " and $($binaryTouched.Count - 5) more" }
        Add-Finding NOTE $subject $Source "binary files are not diffable and were not inspected: $list."
    }

    # The ecosystem's own diff
    $diffResult = $null
    if ($oldPkg) {
        $diffResult = & $Diff $name $old.Version $new.Version
        if (-not $diffResult.Ok) { Add-Finding STOP $subject $Source "the diff tool failed: $($diffResult.Title)." }
    }

    $oldText = if ($old) { $old.Version } else { '-' }
    $row = "| ``$name`` | $oldText | $($new.Version) | $(Format-Date $published) | $ageText | $provenanceText | $($changed.Count) / $($added.Count) / $($removed.Count) |"
    return @{ Row = $row; Diff = $diffResult }
}

# Reviews one lockfile and returns its report section.
function Invoke-LockfileReview {
    param([string]$Path, [scriptblock]$Read, [scriptblock]$Diff)
    $baseEntries = @(& $Read (Get-FileAt $script:mergeBase $Path))
    $headEntries = @(& $Read (Get-FileAt $script:headCommit $Path))
    $changes = @(Get-LockChanges $baseEntries $headEntries | Sort-Object Name)
    $section = [System.Collections.Generic.List[string]]::new()
    $section.Add("## $Path")
    $section.Add('')
    if (-not $changes.Count) {
        $section.Add('No package version changed or appeared.')
        return $section
    }
    $section.Add('| Package | Old | New | Published | Age (days) | Provenance | Files changed / added / removed |')
    $section.Add('| --- | --- | --- | --- | --- | --- | --- |')
    $diffs = [System.Collections.Generic.List[object]]::new()
    foreach ($change in $changes) {
        $result = Invoke-PackageReview $change $Path $Diff
        $section.Add($result.Row)
        if ($result.Diff) { $diffs.Add($result.Diff) }
    }
    foreach ($d in $diffs) {
        $lines = @($d.Output)
        $limit = 200
        $section.Add('')
        $section.Add("<details><summary>$($d.Title)</summary>")
        $section.Add('')
        $section.Add('```text')
        $lines | Select-Object -First $limit | ForEach-Object { $section.Add($_) }
        if ($lines.Count -gt $limit) { $section.Add("... $($lines.Count - $limit) more lines") }
        $section.Add('```')
        $section.Add('')
        $section.Add("Full diff: $($d.Full)")
        $section.Add('')
        $section.Add('</details>')
    }
    return $section
}

#endregion

# Extension point. Each reviewer picks its files from the changed paths (Match) and reviews one of them (Review),
# adding findings with Add-Finding and returning its report section as lines. NuGet, Docker and uv.lock add an entry.
$Reviewers = @(
    @{
        Name = 'Bun'
        Match = { param($Path) (Split-Path -Leaf $Path) -eq 'bun.lock' }
        Review = { param($Path) Invoke-LockfileReview $Path ${function:Read-BunLock} ${function:Invoke-BunDiff} }
    }
    @{
        Name = 'npm'
        Match = { param($Path) (Split-Path -Leaf $Path) -eq 'package-lock.json' }
        Review = { param($Path) Invoke-LockfileReview $Path ${function:Read-NpmLock} ${function:Invoke-NpmDiff} }
    }
)

try {
    $AllowYoung = @($AllowYoung | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($name in $AllowYoung) {
        if ($name -notmatch '^(@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*$') {
            throw "-AllowYoung takes package names, one per entry, no wildcards: '$name'."
        }
    }

    $baseCommit = (Invoke-Git rev-parse --verify --quiet "$Base^{commit}") | Select-Object -First 1
    $script:headCommit = (Invoke-Git rev-parse --verify --quiet "$Head^{commit}") | Select-Object -First 1
    $script:mergeBase = (Invoke-Git merge-base $baseCommit $script:headCommit) | Select-Object -First 1
    $changedPaths = @(Invoke-Git diff --name-only --no-renames $script:mergeBase $script:headCommit |
        Where-Object { $_ -notmatch '(^|/)node_modules/' })

    $sections = [System.Collections.Generic.List[string]]::new()
    $reviewed = 0
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    # The review downloads public packages only, so bun pm diff and npm diff get no credential-looking variables.
    . (Join-Path $PSScriptRoot 'credential-variables.ps1')
    $removedVariables = Remove-CredentialVariables
    try {
        foreach ($path in $changedPaths) {
            foreach ($reviewer in $Reviewers) {
                if (-not (& $reviewer.Match $path)) { continue }
                $reviewed++
                Write-Log "== $($reviewer.Name): $path"
                $sections.Add('')
                foreach ($line in (& $reviewer.Review $path)) { $sections.Add($line) }
            }
        }
    } finally {
        Restore-CredentialVariables $removedVariables
        Remove-Item -Recurse -Force $toolDir -ErrorAction SilentlyContinue
    }

    foreach ($name in $AllowYoung) {
        if (-not $clearedYoung.Contains($name)) {
            Add-Finding NOTE $name '-AllowYoung' 'was named in -AllowYoung but had no age STOP to clear.'
        }
    }
} catch {
    Write-Error -ErrorAction Continue "review-deps: $($_.Exception.Message)"
    exit 2
}

$stops = @($findings | Where-Object Kind -eq 'STOP')
$notes = @($findings | Where-Object Kind -eq 'NOTE')
$report = [System.Collections.Generic.List[string]]::new()
$report.Add('# Dependency review')
$report.Add('')
$report.Add("``$Base`` (merge base ``$($script:mergeBase.Substring(0, 8))``) to ``$Head`` (``$($script:headCommit.Substring(0, 8))``), ages as of $($Now.ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC, minimum age $minAgeDays days.")
$report.Add('')
if ($reviewed -eq 0) {
    $report.Add('No dependency file changed.')
    $report.Add('')
}
$result = if ($stops.Count) { '**Result: STOP**' } else { '**Result: clean**' }
$report.Add("$result, $($stops.Count) STOP and $($notes.Count) NOTE.")
$report.Add('')
$report.Add('## Findings')
$report.Add('')
if (-not $findings.Count) { $report.Add('None.') }
foreach ($f in @($stops) + @($notes)) { $report.Add("- **$($f.Kind)** ``$($f.Subject)`` ($($f.Source)): $($f.Text)") }
foreach ($line in $sections) { $report.Add($line) }

$report -join "`n"
exit ($stops.Count ? 1 : 0)
