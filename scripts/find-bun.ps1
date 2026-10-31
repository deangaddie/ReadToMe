# Dot-source this, then call Find-Bun: the Bun executable whose major.minor matches the "packageManager" pin
# ("bun@1.4.2") in src/Read2Me.Web/package.json. Bun is looked up on PATH first, then at ~/.bun/bin/bun.exe
# (where the Windows installer puts it without adding it to PATH). Throws when it is missing or the wrong version.

function Find-Bun {
    param([Parameter(Mandatory)][string]$WebDir)

    $installHint = 'Install Bun: `powershell -c "irm bun.sh/install.ps1 | iex"` (https://bun.sh), then try again.'
    $bun = $null
    if ($found = Get-Command bun -ErrorAction SilentlyContinue) { $bun = $found.Source }
    if (-not $bun) {
        $homeBun = Join-Path $HOME '.bun/bin/bun.exe'
        if (Test-Path $homeBun) { $bun = $homeBun }
    }
    if (-not $bun) {
        throw "Bun is neither on PATH nor at ~/.bun/bin/bun.exe. $installHint"
    }

    # The packageManager pin is the only version pin; CI's setup-bun reads the same field.
    $package = Get-Content -Raw (Join-Path $WebDir 'package.json') | ConvertFrom-Json
    $pin = [string]$package.packageManager
    if ($pin -notmatch '^bun@(\d+)\.(\d+)') {
        throw "package.json ""packageManager"" is '$pin'; expected bun@<major>.<minor>.<patch>."
    }
    $pinned = "$($Matches[1]).$($Matches[2])"

    $installed = [string](& $bun --version)
    if ($LASTEXITCODE -ne 0) { throw "bun --version failed ($LASTEXITCODE) using $bun" }
    $installed = $installed.Trim()
    if ($installed -notmatch '^(\d+)\.(\d+)') {
        throw "Could not parse Bun version '$installed' from $bun."
    }
    $actual = "$($Matches[1]).$($Matches[2])"
    if ($actual -ne $pinned) {
        throw "Bun $installed at $bun does not match the package.json pin $pin (major.minor $pinned). Install that version (bun upgrade, or https://bun.sh)."
    }
    return $bun
}
