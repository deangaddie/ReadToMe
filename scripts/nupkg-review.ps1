# Dot-source this for the .nupkg checks review-deps.ps1 runs on every changed NuGet package (#417): Read-Nupkg reads a
# package's bytes in memory (nothing is unpacked to disk) and Compare-Nupkg compares an old and a new one.
#
# A package's build/*.props and *.targets, and its analyzers, run inside `dotnet build` as the developer, so a new file
# in a folder MSBuild or the compiler loads from is a STOP. That restore alone does not import them, and so that a
# package can be read safely between restore and the first build, is inferred from the NuGet docs, not tested with a
# hostile package (#400).

# The folders whose files run, or are copied into the project, at build time. content* covers content/ and contentFiles/.
$script:NupkgBuildTimeFolder = '^(build|buildTransitive|buildMultiTargeting|analyzers|tools|content[^/]*)/'
# The packaging files every .nupkg has, which are not the package's content.
$script:NupkgPackagingFile = '^(_rels/|package/services/|\[Content_Types\]\.xml$|\.signature\.p7s$|[^/]+\.nuspec$)'

# A .nupkg's content: Files (path -> SHA-256 hex, packaging files left out), Dependencies (the nuspec's, one
# 'tfm: id range' string each), and AuthorSigner (the author signature's certificate subject, or $null when it has
# only nuget.org's repository signature or none).
function Read-Nupkg {
    param([byte[]]$Bytes)
    $zip = [System.IO.Compression.ZipArchive]::new([System.IO.MemoryStream]::new($Bytes))
    try {
        $files = @{}
        $nuspec = $null
        $signature = $null
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $path = [uri]::UnescapeDataString($entry.FullName)
            $buffer = [System.IO.MemoryStream]::new()
            $stream = $entry.Open()
            try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
            $data = $buffer.ToArray()
            if ($path -match '^[^/]+\.nuspec$') { $nuspec = [System.Text.Encoding]::UTF8.GetString($data).TrimStart([char]0xFEFF) }
            if ($path -eq '.signature.p7s') { $signature = $data }
            if ($path -match $script:NupkgPackagingFile) { continue }
            $files[$path] = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($data))
        }
    } finally { $zip.Dispose() }
    [pscustomobject]@{
        Files = $files
        Dependencies = @(Get-NuspecDependencies $nuspec)
        AuthorSigner = Get-AuthorSigner $signature
    }
}

# The nuspec's dependencies as 'tfm: id range' strings ('any' for a group with no target framework).
function Get-NuspecDependencies {
    param([string]$Nuspec)
    if (-not $Nuspec) { return }
    $xml = [xml]$Nuspec
    $deps = $xml.SelectNodes("//*[local-name()='metadata']/*[local-name()='dependencies']")
    foreach ($node in $deps) {
        foreach ($child in $node.ChildNodes) {
            if ($child.LocalName -eq 'dependency') { "any: $($child.id) $($child.version)" }
            elseif ($child.LocalName -eq 'group') {
                $tfm = if ($child.targetFramework) { $child.targetFramework } else { 'any' }
                foreach ($d in $child.ChildNodes) { if ($d.LocalName -eq 'dependency') { "${tfm}: $($d.id) $($d.version)" } }
            }
        }
    }
}

# The author signature's certificate subject, or $null. The primary signature of a .signature.p7s is the author's when
# its commitment-type-indication is proof of origin (1.2.840.113549.1.9.16.6.1); nuget.org's own repository signature
# says proof of receipt (...6.2), as the primary signature or as a countersignature on an author one.
function Get-AuthorSigner {
    param([byte[]]$Signature)
    if (-not $Signature) { return $null }
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($Signature)
    $signer = $cms.SignerInfos[0]
    foreach ($attribute in $signer.SignedAttributes) {
        if ($attribute.Oid.Value -ne '1.2.840.113549.1.9.16.2.16') { continue }
        foreach ($value in $attribute.Values) {
            $reader = [System.Formats.Asn1.AsnReader]::new($value.RawData, [System.Formats.Asn1.AsnEncodingRules]::DER)
            $commitment = $reader.ReadSequence().ReadObjectIdentifier()
            if ($commitment -eq '1.2.840.113549.1.9.16.6.1') { return $signer.Certificate.Subject }
        }
    }
    return $null
}

# Compares two Read-Nupkg results and returns findings as @{ Kind = 'STOP' | 'NOTE'; Text }. $Old is $null for a
# package new to the tree, when every file counts as new.
function Compare-Nupkg {
    param($Old, $New)
    $oldFiles = if ($Old) { $Old.Files } else { @{} }
    $added = @($New.Files.Keys | Where-Object { -not $oldFiles.ContainsKey($_) } | Sort-Object)
    $newBuildTime = @($added | Where-Object { $_ -match $script:NupkgBuildTimeFolder })
    if ($newBuildTime.Count) {
        @{ Kind = 'STOP'; Text = "has new files that run or are copied in at build time: $(Format-NupkgList $newBuildTime)." }
    }
    if (-not $Old) { return }   # a package new to the tree is a STOP already; the rest compares two versions

    $newOther = @($added | Where-Object { $_ -notmatch $script:NupkgBuildTimeFolder })
    if ($newOther.Count) { @{ Kind = 'NOTE'; Text = "has new files: $(Format-NupkgList $newOther)." } }

    # A dependency is its 'tfm: id'; a range that moves with the package version is reviewed as its own restore change.
    $depKey = { param($d) ($d -split ' ')[0..1] -join ' ' }
    $oldKeys = @($Old.Dependencies | ForEach-Object { & $depKey $_ })
    $newKeys = @($New.Dependencies | ForEach-Object { & $depKey $_ })
    $addedDeps = @($New.Dependencies | Where-Object { (& $depKey $_) -notin $oldKeys })
    $removedDeps = @($Old.Dependencies | Where-Object { (& $depKey $_) -notin $newKeys })
    if ($addedDeps.Count -or $removedDeps.Count) {
        $parts = @()
        if ($addedDeps.Count) { $parts += "added $(Format-NupkgList $addedDeps)" }
        if ($removedDeps.Count) { $parts += "removed $(Format-NupkgList $removedDeps)" }
        @{ Kind = 'NOTE'; Text = "changed its nuspec dependencies: $($parts -join '; ')." }
    }

    if ($Old.AuthorSigner -and -not $New.AuthorSigner) {
        @{ Kind = 'STOP'; Text = "has no author signature, though the old version was signed by ``$($Old.AuthorSigner)``." }
    } elseif ($Old.AuthorSigner -and (Get-SubjectKey $New.AuthorSigner) -ne (Get-SubjectKey $Old.AuthorSigner)) {
        @{ Kind = 'NOTE'; Text = "is author-signed by ``$($New.AuthorSigner)``; the old version was signed by ``$($Old.AuthorSigner)``." }
    }
}

# The build-time files both versions ship with different contents, sorted. Not a finding: Microsoft's packages
# change their buildTransitive .targets and analyzer DLLs on every patch. The report lists them for reading.
function Get-ChangedBuildTimeFiles {
    param($Old, $New)
    if (-not $Old) { return }
    $New.Files.Keys | Where-Object {
        $_ -match $script:NupkgBuildTimeFolder -and $Old.Files.ContainsKey($_) -and $Old.Files[$_] -ne $New.Files[$_]
    } | Sort-Object
}

# A certificate subject's name parts, sorted: a renewed certificate may list the same parts in another order.
function Get-SubjectKey {
    param([string]$Subject)
    if (-not $Subject) { return '' }
    return (($Subject -split ',\s*(?=[A-Za-z0-9.]+=)') | Sort-Object) -join ', '
}

# Up to 5 items as `a`, `b` and N more.
function Format-NupkgList {
    param([string[]]$Items)
    $list = ($Items | Select-Object -First 5 | ForEach-Object { "``$_``" }) -join ', '
    if ($Items.Count -gt 5) { $list += " and $($Items.Count - 5) more" }
    return $list
}
