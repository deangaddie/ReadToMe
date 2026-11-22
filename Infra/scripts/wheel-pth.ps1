# Infra/scripts/wheel-pth.ps1, dot-sourced by lock.ps1 and scripts/review-deps.ps1.
#
# Finds the Python .pth files a lock change brings in, without installing or running anything.
# site.py runs every import line in a site-packages *.pth on every interpreter start, so a
# .pth runs code where install-script defences never look, and neither uv nor pip can refuse it.
#
# Get-LockPthChanges compares two requirements.txt texts. For each pin that changed or
# appeared, it lists the entries of every wheel the pin's hashes name, for every platform, by
# reading only the zip's central directory over HTTP range requests (torch is ~190 MB, its
# directory under 2 MB). A wheel's .pth files are its top-level *.pth and the *.pth that
# <name>.data/ installs straight into site-packages (purelib, platlib, data/lib/.../site-packages).
# A .pth nested inside a package is not one to site.py and is skipped.
#
# A .pth the pin's previous version did not ship, or shipped with other contents (the zip's
# CRC-32 differs), is new. The pin's hashes are matched against PyPI's file digests, so only
# files the lock would accept are read, and a hash that names no PyPI file fails the check too.

$script:WheelPthHttp = $null
$script:WheelPthPypi = @{}

function Get-WheelPthHttp {
  if (-not $script:WheelPthHttp) {
    $script:WheelPthHttp = [System.Net.Http.HttpClient]::new()
    $script:WheelPthHttp.Timeout = [TimeSpan]::FromMinutes(2)
    $script:WheelPthHttp.DefaultRequestHeaders.UserAgent.ParseAdd('ReadToMe-wheel-pth/1')
  }
  return $script:WheelPthHttp
}

# A PEP 503 normalised name: lower case, runs of -_. as one -.
function ConvertTo-PythonName { param([string]$Name) ($Name.ToLowerInvariant() -replace '[-_.]+', '-') }

# A requirements.txt (pip/uv format, --generate-hashes) as one pin per package:
# Name (normalised), Version or Url, Hashes (sha256 hex, sorted), Label (what the report shows).
function Read-PythonLock {
  param([string]$Text)
  if (-not $Text) { return @() }
  $joined = ($Text -replace "\r", '') -replace "\\\n", ' '
  foreach ($line in $joined -split "\n") {
    $line = ($line -replace '(^|\s)#.*$', '').Trim()
    if (-not $line -or $line.StartsWith('-')) { continue }   # comments and options (-r, --index-url)
    if ($line -notmatch '^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(\[[^\]]*\])?\s*(==\s*(?<ver>[^\s;]+)|@\s*(?<url>[^\s;]+))?') {
      throw "Cannot read the requirement line: $line"
    }
    $hashes = @([regex]::Matches($line, '--hash=sha256:([0-9a-fA-F]{64})') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
    $version = $Matches['ver']
    $url = $Matches['url']
    [pscustomobject]@{
      Name = ConvertTo-PythonName $Matches['name']
      Version = $version
      Url = $url
      Hashes = $hashes
      Label = if ($version) { $version } elseif ($url) { $url } else { '(unpinned)' }
    }
  }
}

function Copy-Bytes {
  param([byte[]]$Bytes, [long]$Start, [long]$Count)
  $copy = [byte[]]::new($Count)
  [Array]::Copy($Bytes, $Start, $copy, 0, $Count)
  return ,$copy
}

# Bytes [Start, Start+Count) of a URL or local file; Start < 0 reads the last -Start bytes.
# Returns the bytes and where they start, and the total length.
function Read-ByteRange {
  param([string]$Source, [long]$Start, [long]$Count)
  $local = $null
  if ($Source -match '^file:') { $local = ([uri]$Source).LocalPath }
  elseif ($Source -notmatch '^https?://') { $local = $Source }
  if ($local) {
    $stream = [System.IO.File]::OpenRead($local)
    try {
      $total = $stream.Length
      if ($Start -lt 0) { $Start = [math]::Max(0, $total + $Start); $Count = $total - $Start }
      $Count = [math]::Min($Count, $total - $Start)
      $buffer = [byte[]]::new($Count)
      $stream.Position = $Start
      $read = 0
      while ($read -lt $Count) { $n = $stream.Read($buffer, $read, $Count - $read); if ($n -le 0) { break }; $read += $n }
      return @{ Bytes = $buffer; Start = $Start; Total = $total }
    } finally { $stream.Dispose() }
  }
  $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Source)
  $request.Headers.Range = if ($Start -lt 0) {
    [System.Net.Http.Headers.RangeHeaderValue]::new($null, -$Start)
  } else {
    [System.Net.Http.Headers.RangeHeaderValue]::new($Start, $Start + $Count - 1)
  }
  $response = (Get-WheelPthHttp).SendAsync($request).GetAwaiter().GetResult()
  if ([int]$response.StatusCode -eq 416 -and $Start -lt 0) {
    # Some hosts (files.pythonhosted.org) refuse a tail longer than the file: the file is small, so take all of it.
    $response.Dispose()
    $response = (Get-WheelPthHttp).GetAsync($Source).GetAwaiter().GetResult()
  }
  try {
    if (-not $response.IsSuccessStatusCode) { throw "GET $Source returned $([int]$response.StatusCode)." }
    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $range = $response.Content.Headers.ContentRange
    if ([int]$response.StatusCode -eq 206 -and $range) { return @{ Bytes = $bytes; Start = [long]$range.From; Total = [long]$range.Length } }
    # 200: the server ignored the range and sent the whole file.
    if ($Start -lt 0) { $Start = [math]::Max(0, $bytes.Length + $Start); $Count = $bytes.Length - $Start }
    return @{ Bytes = (Copy-Bytes $bytes $Start $Count); Start = $Start; Total = $bytes.Length }
  } finally { $response.Dispose() }
}

# Every entry in a zip (a wheel) as Name and Crc (CRC-32), read from its central directory only.
function Get-ZipEntries {
  param([string]$Source)
  $tail = Read-ByteRange $Source -65536 0
  $b = $tail.Bytes
  $eocd = -1
  for ($i = $b.Length - 22; $i -ge 0; $i--) {
    if ($b[$i] -eq 0x50 -and $b[$i + 1] -eq 0x4b -and $b[$i + 2] -eq 0x05 -and $b[$i + 3] -eq 0x06) { $eocd = $i; break }
  }
  if ($eocd -lt 0) { throw "$Source is not a zip file (no end of central directory)." }
  [long]$cdSize = [BitConverter]::ToUInt32($b, $eocd + 12)
  [long]$cdOffset = [BitConverter]::ToUInt32($b, $eocd + 16)
  if ($cdOffset -eq [uint32]::MaxValue -or $cdSize -eq [uint32]::MaxValue) {
    # Zip64: the locator sits just before the end record and points at the zip64 end record.
    $locator = $eocd - 20
    if ($locator -lt 0 -or [BitConverter]::ToUInt32($b, $locator) -ne 0x07064b50) { throw "$Source has a broken zip64 directory." }
    $record = Read-ByteRange $Source ([long][BitConverter]::ToUInt64($b, $locator + 8)) 56
    $cdSize = [long][BitConverter]::ToUInt64($record.Bytes, 40)
    $cdOffset = [long][BitConverter]::ToUInt64($record.Bytes, 48)
  }
  [byte[]]$cd = if ($cdOffset -ge $tail.Start -and $cdOffset + $cdSize -le $tail.Start + $b.Length) {
    Copy-Bytes $b ($cdOffset - $tail.Start) $cdSize
  } else {
    (Read-ByteRange $Source $cdOffset $cdSize).Bytes
  }
  $entries = [System.Collections.Generic.List[object]]::new()
  $pos = 0
  while ($pos + 46 -le $cd.Length -and [BitConverter]::ToUInt32($cd, $pos) -eq 0x02014b50) {
    $nameLength = [BitConverter]::ToUInt16($cd, $pos + 28)
    $extraLength = [BitConverter]::ToUInt16($cd, $pos + 30)
    $commentLength = [BitConverter]::ToUInt16($cd, $pos + 32)
    $name = [System.Text.Encoding]::UTF8.GetString($cd, $pos + 46, $nameLength).Replace([char]92, [char]47)   # \ to /
    $entries.Add([pscustomobject]@{ Name = $name; Crc = [BitConverter]::ToUInt32($cd, $pos + 16) })
    $pos += 46 + $nameLength + $extraLength + $commentLength
  }
  if ($pos -ne $cd.Length) { throw "$Source has an unreadable central directory." }
  return $entries
}

# The entries of a wheel that site.py would run as .pth files once installed: top-level *.pth, and *.pth that
# <name>.data/ installs straight into site-packages (purelib, platlib, or data/lib/.../site-packages).
function Get-WheelPthEntries {
  param([string]$Source)
  @(Get-ZipEntries $Source | Where-Object {
      $_.Name -match '^[^/]+\.pth$' -or
      $_.Name -match '^[^/]+\.data/(purelib|platlib)/[^/]+\.pth$' -or
      $_.Name -match '^[^/]+\.data/data/(.+/)?site-packages/[^/]+\.pth$'
    } | ForEach-Object { [pscustomobject]@{ Path = $_.Name; File = $_.Name.Split('/')[-1].ToLowerInvariant(); Crc = $_.Crc } })
}

# The wheels a pin's hashes name, as Filename/Url; hashes that name no PyPI file (Unmatched), and what is not a
# wheel and so not inspected (Skipped).
function Get-PinWheels {
  param($Pin)
  $wheels = [System.Collections.Generic.List[object]]::new()
  $unmatched = [System.Collections.Generic.List[string]]::new()
  $skipped = [System.Collections.Generic.List[string]]::new()
  $result = @{ Wheels = $wheels; Unmatched = $unmatched; Skipped = $skipped }
  if ($Pin.Url) {
    $file = [uri]::UnescapeDataString(($Pin.Url -split '[?#]')[0].Split('/')[-1])
    if ($file -match '\.whl$') { $wheels.Add([pscustomobject]@{ Filename = $file; Url = $Pin.Url }) }
    else { $skipped.Add("$file is not a wheel; it is built from source on install, and was not inspected") }
    return $result
  }
  if (-not $Pin.Version) {
    $skipped.Add('the pin has no version or URL, so there is nothing to inspect')
    return $result
  }
  $key = "$($Pin.Name)/$($Pin.Version)"
  if (-not $script:WheelPthPypi.ContainsKey($key)) {
    $json = (Get-WheelPthHttp).GetStringAsync("https://pypi.org/pypi/$($Pin.Name)/$($Pin.Version)/json").GetAwaiter().GetResult()
    $script:WheelPthPypi[$key] = $json | ConvertFrom-Json -AsHashtable -DateKind String
  }
  $files = @($script:WheelPthPypi[$key].urls)
  foreach ($hash in $Pin.Hashes) {
    $file = $files | Where-Object { $_.digests.sha256 -eq $hash } | Select-Object -First 1
    if (-not $file) { $unmatched.Add("hash $($hash.Substring(0, 12)) is not a PyPI file of $key, so the file it accepts could not be read"); continue }
    if ($file.filename -match '\.whl$') { $wheels.Add([pscustomobject]@{ Filename = $file.filename; Url = $file.url }) }
  }
  if (-not $wheels.Count -and -not $unmatched.Count) { $skipped.Add("$key has no wheel in the lock; its sdist is built on install and was not inspected") }
  return $result
}

# One result per pin that changed or appeared between two requirements.txt texts:
#   Name, Old and New (labels; Old is $null for a new package), Wheels (count inspected),
#   NewPth     'wheel: entry' for each .pth the old version did not ship, or shipped with other contents
#              (by the zip's CRC-32),
#   KeptPth    'wheel: entry' for each .pth the old version shipped unchanged,
#   Unmatched  new-pin hashes that name no PyPI file: the check could not read what the lock accepts,
#   Skipped    what is not a wheel and so was not inspected.
# Both NewPth and Unmatched fail the check. $Log receives one progress line per wheel.
function Get-LockPthChanges {
  param([string]$OldText, [string]$NewText, [scriptblock]$Log = {})
  $oldPins = @{}
  foreach ($pin in @(Read-PythonLock $OldText)) { $oldPins[$pin.Name] = $pin }
  foreach ($new in @(Read-PythonLock $NewText | Sort-Object Name)) {
    $old = $oldPins[$new.Name]
    if ($old -and $old.Label -eq $new.Label -and ($old.Hashes -join ',') -eq ($new.Hashes -join ',')) { continue }

    # file name -> the CRC-32s the old version shipped it with
    $oldPth = @{}
    if ($old) {
      foreach ($wheel in (Get-PinWheels $old).Wheels) {
        & $Log "  wheel: $($wheel.Filename) (previous)"
        foreach ($entry in Get-WheelPthEntries $wheel.Url) {
          if (-not $oldPth.ContainsKey($entry.File)) { $oldPth[$entry.File] = [System.Collections.Generic.HashSet[uint32]]::new() }
          [void]$oldPth[$entry.File].Add($entry.Crc)
        }
      }
    }
    $found = Get-PinWheels $new
    $newPth = [System.Collections.Generic.List[string]]::new()
    $keptPth = [System.Collections.Generic.List[string]]::new()
    foreach ($wheel in $found.Wheels) {
      & $Log "  wheel: $($wheel.Filename)"
      foreach ($entry in Get-WheelPthEntries $wheel.Url) {
        $label = "$($wheel.Filename): $($entry.Path)"
        if (-not $oldPth.ContainsKey($entry.File)) { $newPth.Add($label) }
        elseif (-not $oldPth[$entry.File].Contains($entry.Crc)) { $newPth.Add("$label (contents changed)") }
        else { $keptPth.Add($label) }
      }
    }
    [pscustomobject]@{
      Name = $new.Name
      Old = if ($old) { $old.Label } else { $null }
      New = $new.Label
      Wheels = $found.Wheels.Count
      NewPth = @($newPth)
      KeptPth = @($keptPth)
      Unmatched = @($found.Unmatched)
      Skipped = @($found.Skipped)
    }
  }
}
