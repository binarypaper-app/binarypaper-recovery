# Exercise refusal paths without GitHub credentials, billing changes, or artifact uploads.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repoRoot 'tools/artifact-policy.ps1')
$scratch = Join-Path $repoRoot ('artifacts/policy-tests-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$saved = @{}
foreach ($key in @('GH_TOKEN', 'GITHUB_REPOSITORY', 'GITHUB_API_URL', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
function MustRefuse([string]$Name, [scriptblock]$Action) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw "Gate did not refuse: $Name" }
}
try {
    Assert-ArtifactAdmission crash 1MB 3 @() | Out-Null
    Assert-ArtifactAdmission release 256MB 20 @() | Out-Null
    MustRefuse 'oversized compact packet' { Assert-ArtifactAdmission crash (1MB + 1) 3 @() }
    MustRefuse 'oversized dump' { Assert-ArtifactAdmission dump (64MB + 1) 1 @() }
    MustRefuse 'oversized release' { Assert-ArtifactAdmission release (256MB + 1) 20 @() }
    MustRefuse 'unbounded file count' { Assert-ArtifactAdmission crash 100 65 @() }
    MustRefuse 'empty packet' { Assert-ArtifactAdmission crash 0 0 @() }
    MustRefuse 'missing inventory size' { Assert-ArtifactAdmission crash 10 1 @(@{ name = 'broken' }) }
    MustRefuse 'unknown policy' { Assert-ArtifactAdmission other 10 1 @() }
    # The full sibling reservation must fit, even if this particular packet is tiny.
    Assert-ArtifactAdmission crash 10 1 @(@{ size_in_bytes = 313MB }) | Out-Null
    MustRefuse 'concurrent CI siblings' { Assert-ArtifactAdmission crash 10 1 @(@{ size_in_bytes = 313MB + 1 }) }
    MustRefuse 'expired but still inventoried artifact' {
        Assert-ArtifactAdmission boundary 10 1 @(@{ size_in_bytes = 379MB; expired = $true })
    }

    # Mock only the HTTP boundary. Exercise real enumeration, pagination, staging, and outputs.
    $mockState = @{ Calls = 0; Fails = $false }
    function Invoke-RestMethod {
        param([string]$Uri, [hashtable]$Headers)
        $mockState.Calls++
        if ($mockState.Fails) { throw 'Synthetic inventory unavailable' }
        if ($Uri -match 'page=1$') {
            return @{ total_count = 101; artifacts = @(1..100 | ForEach-Object { @{ size_in_bytes = 0 } }) }
        }
        return @{ total_count = 101; artifacts = @(@{ size_in_bytes = 1MB }) }
    }
    $env:GH_TOKEN = 'synthetic-test-value'
    $env:GITHUB_REPOSITORY = 'example/recovery'
    $env:GITHUB_API_URL = 'https://example.invalid'
    $env:GITHUB_OUTPUT = Join-Path $scratch 'outputs'
    $env:GITHUB_STEP_SUMMARY = Join-Path $scratch 'summary'
    $inputPath = Join-Path $scratch 'checkpoint.json'
    '{"seed":20260824,"iteration":7}' | Set-Content -LiteralPath $inputPath
    & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind crash -Paths $inputPath -Destination (Join-Path $scratch 'accepted')
    if ($mockState.Calls -ne 2) { throw 'Inventory pagination was skipped' }
    if (-not (Test-Path (Join-Path $scratch 'accepted/checkpoint.json'))) { throw 'Accepted packet was not staged' }
    if ((Get-Content $env:GITHUB_OUTPUT) -notcontains 'allowed=true') { throw 'Accepted packet was not admitted' }

    $large = Join-Path $scratch 'large.dmp'
    $stream = [IO.File]::Create($large)
    $stream.SetLength(2MB); $stream.Dispose()
    MustRefuse 'large file before HTTP/copy' {
        & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind crash -Paths $large -Destination (Join-Path $scratch 'too-large')
    }
    if ($mockState.Calls -ne 2 -or (Test-Path (Join-Path $scratch 'too-large'))) { throw 'Large evidence reached inventory/copy' }
    $duplicateDirectory = Join-Path $scratch 'duplicate'
    New-Item -ItemType Directory -Path $duplicateDirectory | Out-Null
    Copy-Item $inputPath $duplicateDirectory
    MustRefuse 'ambiguous duplicate basenames' {
        & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind crash -Paths @($inputPath, $duplicateDirectory) -Destination (Join-Path $scratch 'duplicates')
    }
    $link = Join-Path $scratch 'linked'
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $link -Target $duplicateDirectory | Out-Null
    MustRefuse 'linked evidence directory' {
        & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind crash -Paths $link -Destination (Join-Path $scratch 'links')
    }
    Remove-Item -LiteralPath $link -Force
    $mockState.Fails = $true
    $env:GITHUB_OUTPUT = Join-Path $scratch 'refused-output'
    & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind crash -Paths $inputPath -Destination (Join-Path $scratch 'unavailable') -Optional
    if ((Get-Content $env:GITHUB_OUTPUT) -notcontains 'allowed=false') { throw 'Unavailable inventory did not close the gate' }
    if (Test-Path (Join-Path $scratch 'unavailable')) { throw 'Unavailable inventory still staged evidence' }
    MustRefuse 'required evidence with unavailable inventory' {
        & (Join-Path $repoRoot 'tools/prepare-artifact.ps1') -Kind release -Paths $inputPath -Destination (Join-Path $scratch 'required')
    }

    # A dump in the test results must never enter the automatic crash packet.
    $checkpoint = Join-Path $scratch 'image.json'
    $snapshot = "$checkpoint.input.bin"
    [IO.File]::WriteAllBytes($snapshot, [byte[]](1, 2, 3))
    @{ seed = 20260824; iteration = 7; inputSha256 = (Get-FileHash $snapshot).Hash } |
        ConvertTo-Json | Set-Content $checkpoint
    $packet = Join-Path $scratch 'compact'
    & (Join-Path $repoRoot 'tools/prepare-crash-evidence.ps1') -Checkpoint $checkpoint -Results $scratch -Destination $packet
    if (Get-ChildItem $packet -Filter '*.dmp') { throw 'A memory dump entered the automatic packet' }
    if (-not (Test-Path (Join-Path $packet 'image.json.input.bin'))) { throw 'Exact replay input was lost' }
    if (-not (Select-String -LiteralPath $env:GITHUB_STEP_SUMMARY -SimpleMatch '20260824')) { throw 'No log/summary fallback' }
    Write-Host 'PASS: artifact sizes, sibling reservations, inventory pagination/failure, staging, and compact crash replay'
}
finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    $prefix = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
