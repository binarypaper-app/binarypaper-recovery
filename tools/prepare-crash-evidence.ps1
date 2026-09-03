param(
    [string]$Checkpoint = 'artifacts/fuzz/image-fuzz-checkpoint.json',
    [string]$Results = 'artifacts/test-results',
    [string]$Destination = 'artifacts/crash-evidence'
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Destination) { throw 'Crash packet directory must be new' }
New-Item -ItemType Directory -Path $Destination | Out-Null
$metadata = [ordered]@{
    commit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; attempt = $env:GITHUB_RUN_ATTEMPT
    os = $env:RUNNER_OS; architecture = $env:RUNNER_ARCH
    sdk = (& dotnet --version); iterations = $env:BP_FUZZ_ITERATIONS
}
if (Test-Path -LiteralPath $Checkpoint) {
    if ((Get-Item -LiteralPath $Checkpoint).Length -gt 16KB) { throw 'Checkpoint is unexpectedly large' }
    $text = Get-Content -LiteralPath $Checkpoint -Raw
    $metadata.checkpoint = $text | ConvertFrom-Json
    Copy-Item -LiteralPath $Checkpoint -Destination $Destination
    $inputPath = "$Checkpoint.input.bin"
    if ((Test-Path -LiteralPath $inputPath) -and (Get-Item -LiteralPath $inputPath).Length -le 512KB) {
        if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -eq $metadata.checkpoint.inputSha256) {
            Copy-Item -LiteralPath $inputPath -Destination $Destination
        } else { Write-Host 'Input snapshot does not match the checkpoint; use the recorded seed and iteration.' }
    }
}
# Remains in the job log and summary even when artifact admission/upload is unavailable.
$json = $metadata | ConvertTo-Json -Depth 8
Write-Host 'Crash reproduction metadata:'
Write-Host $json
if ($env:GITHUB_STEP_SUMMARY) {
    @('### Crash reproduction metadata', '```json', $json, '```') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
$json | Set-Content -LiteralPath (Join-Path $Destination 'reproduction.json') -Encoding utf8NoBOM
if (Test-Path -LiteralPath $Results) {
    $sequences = @(Get-ChildItem -LiteralPath $Results -Filter 'Sequence_*.xml' -File -Recurse | Sort-Object FullName)
    foreach ($file in $sequences | Select-Object -First 8) {
        if ($file.Length -le 32KB) { Copy-Item -LiteralPath $file.FullName -Destination $Destination }
    }
}
