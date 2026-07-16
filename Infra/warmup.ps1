# Paths to your compose files
$warmupComposeFile = "docker-compose.warmup.yml"

# Get list of service/container names from docker-compose.yaml
$containerNames = docker compose -f $warmupComposeFile config --services

if (-not $containerNames) {
    Write-Host "No services found in $warmupComposeFile"
    exit 1
}

Write-Host "Found containers/services:"
$containerNames | ForEach-Object { Write-Host " - $_" }

foreach ($name in $containerNames) {
    Write-Host "`n=== Warming up container: $name ==="

    Write-Host "Running: docker compose -f $warmupComposeFile run --rm $name"

    # Run the warmup command and wait for it to finish
    $process = Start-Process `
        -FilePath "docker" `
        -ArgumentList "compose", "-f", $warmupComposeFile, "run", "--rm", $name `
        -NoNewWindow `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        Write-Host "Warmup failed for: $name (exit code $($process.ExitCode))"
        # Uncomment if you want to stop on first failure:
        # exit $process.ExitCode
    }
    else {
        Write-Host "Completed warmup for: $name"
    }
}

Write-Host "`nAll containers warmed up (or attempted)."
