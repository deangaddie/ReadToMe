# Dot-source this, then call Remove-CredentialVariables: it removes credential-looking environment variables (TOKEN,
# SECRET, GH_*, NPM_*, ...) from the current process, so nothing it starts afterwards inherits them, and returns them
# as an ordered name -> value map. Pass that map to Restore-CredentialVariables to put them back.
# Used by check.ps1 (before the gate's stages) and review-deps.ps1 (before bun pm diff and npm diff).

function Remove-CredentialVariables {
    # Matched case-insensitively against the name.
    $patterns = @('*TOKEN*', '*SECRET*', '*PASSWORD*', '*_KEY', '*APIKEY*', '*API_KEY*', 'GH_*', 'GITHUB_*', 'NPM_*',
        'NUGET_*', 'AZURE_*', 'AWS_*', 'OPENAI_*', 'ANTHROPIC_*', 'HF_*', 'HUGGING*')
    # Names a pattern catches that a stage may need, and which hold paths or URLs, not secrets: NuGet's folder
    # overrides, and the registry mirror Bun reads.
    $allowed = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH',
        'NPM_CONFIG_REGISTRY')
    $removed = [ordered]@{}
    Get-ChildItem env: | Where-Object {
        $name = $_.Name
        $name -notin $allowed -and ($patterns | Where-Object { $name -like $_ })
    } | Sort-Object Name | ForEach-Object { $removed[$_.Name] = $_.Value }
    foreach ($name in $removed.Keys) { Remove-Item -LiteralPath "env:$name" }
    return $removed
}

function Restore-CredentialVariables {
    param([System.Collections.IDictionary]$Removed)
    foreach ($entry in $Removed.GetEnumerator()) { Set-Item -LiteralPath "env:$($entry.Key)" $entry.Value }
}
