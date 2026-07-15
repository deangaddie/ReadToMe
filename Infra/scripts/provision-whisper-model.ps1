<#
Downloads the one model consumed by the hardened Whisper.CPP service.
The service itself deliberately has no network route or writable model cache.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$infraRoot = Split-Path -Parent $PSScriptRoot
$modelsRoot = Join-Path $infraRoot 'models'
$manifestPath = Join-Path $modelsRoot 'whisper-models.sha256'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Whisper model manifest is missing: $manifestPath"
}

$manifest = @{}
foreach ($line in Get-Content -LiteralPath $manifestPath) {
    $line = $line.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    $pair = $line.Split('=', 2)
    if ($pair.Count -ne 2 -or -not $pair[0] -or -not $pair[1]) {
        throw "Invalid manifest entry: $line"
    }
    if ($manifest.ContainsKey($pair[0])) { throw "Duplicate manifest key: $($pair[0])" }
    $manifest[$pair[0]] = $pair[1]
}

$required = 'Filename', 'SourceRevision', 'SourceUrl', 'SHA256', 'ByteLength'
foreach ($key in $required) {
    if (-not $manifest.ContainsKey($key)) { throw "Manifest is missing $key" }
}
if ($manifest.Filename -ne 'ggml-base.en.bin') { throw 'Manifest filename must be ggml-base.en.bin.' }
if ($manifest.SourceUrl -notmatch [regex]::Escape($manifest.SourceRevision)) {
    throw 'Manifest source URL must contain its immutable source revision.'
}
if ($manifest.SHA256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Manifest SHA256 must be a 64-character hexadecimal digest.' }
[long]$expectedLength = 0
if (-not [long]::TryParse($manifest.ByteLength, [ref]$expectedLength) -or $expectedLength -le 0) {
    throw 'Manifest ByteLength must be a positive integer.'
}

New-Item -ItemType Directory -Force -Path $modelsRoot | Out-Null
$destination = Join-Path $modelsRoot $manifest.Filename

function Test-ModelArtifact([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $expectedLength) { return $false }
    return ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $manifest.SHA256.ToUpperInvariant())
}

if (Test-ModelArtifact $destination) {
    Write-Host "Verified existing Whisper model: $destination"
    exit 0
}

$temporary = Join-Path $modelsRoot ('.' + $manifest.Filename + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    Write-Host "Downloading Whisper model revision $($manifest.SourceRevision)..."
    Invoke-WebRequest -Uri $manifest.SourceUrl -OutFile $temporary
    if (-not (Test-ModelArtifact $temporary)) {
        throw 'Downloaded Whisper model did not match the committed SHA-256 and byte length.'
    }

    # The temporary file is a sibling, so this is a same-volume atomic replacement.
    [System.IO.File]::Move($temporary, $destination, $true)
    Write-Host "Provisioned verified Whisper model: $destination"
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
