#!/usr/bin/env pwsh
<#
.SYNOPSIS
    The offline recovery drill: recover a backup from page images using only a downloaded release.

.DESCRIPTION
    This is the acceptance test for the product promise. Everything else in this repository is
    machinery that supports one claim - that a printed backup can be recovered without BinaryPaper
    or a network, with recovery data processed locally. This drill is where that claim is either
    true or it is not.

    It deliberately uses only:

      - a release archive, verified against SHA256SUMS BEFORE anything is executed
      - page images
      - a scratch directory

    and nothing from this repository's build output, source tree, or tooling.

    Recovery runs in a Linux container with networking disabled and only the staged archive,
    images, and output mounted. The host network is unchanged. Docker must be running.

.PARAMETER ArtifactsDirectory
    Directory holding the release archives and SHA256SUMS.

.PARAMETER RuntimeIdentifier
    Which archive to drill.

.PARAMETER ImagesDirectory
    Page images to recover from.

.PARAMETER ExpectedSha256
    SHA-256 of the file that must come out.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$RuntimeIdentifier = 'linux-x64',
    [Parameter(Mandatory)][string]$ImagesDirectory,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [string]$ReportPath,
    [string]$ContainerImage = 'mcr.microsoft.com/dotnet/runtime-deps:10.0'
)

$ErrorActionPreference = 'Stop'
if ($RuntimeIdentifier -ne 'linux-x64') { throw 'The isolated drill requires the linux-x64 archive and Docker Linux containers' }

Write-Host 'BinaryPaper offline recovery drill'
Write-Host ('=' * 60)

$scratch = Join-Path ([IO.Path]::GetTempPath()) "bp-drill-$([guid]::NewGuid().ToString('n').Substring(0,8))"
New-Item -ItemType Directory -Path $scratch | Out-Null
$containerId = $null

try {
    # ------------------------------------------------------------- verify before executing
    Write-Host "`n1. Verify the archive BEFORE running it"

    $archive = Get-ChildItem $ArtifactsDirectory -Filter "*-$RuntimeIdentifier.zip" | Select-Object -First 1
    if (-not $archive) { throw "no archive for $RuntimeIdentifier in $ArtifactsDirectory" }

    $sumsPath = Join-Path $ArtifactsDirectory 'SHA256SUMS'
    if (-not (Test-Path $sumsPath)) { throw 'SHA256SUMS is missing; the archive cannot be verified' }

    $expectedLine = Get-Content $sumsPath | Where-Object { $_ -match [regex]::Escape($archive.Name) }
    if (-not $expectedLine) { throw "SHA256SUMS has no entry for $($archive.Name)" }

    $expectedHash = ($expectedLine -split '\s+')[0]
    $actualHash = (Get-FileHash $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $expectedHash) {
        throw "archive hash mismatch`n  expected $expectedHash`n  actual   $actualHash"
    }

    Write-Host "   $($archive.Name)"
    Write-Host "   sha256 $actualHash  verified against SHA256SUMS"

    # ------------------------------------------------------------- extract to scratch
    Write-Host "`n2. Extract to a scratch directory outside the repository"
    $toolDirectory = Join-Path $scratch 'tool'
    Expand-Archive -Path $archive.FullName -DestinationPath $toolDirectory

    $executable = Get-ChildItem $toolDirectory -Filter 'binarypaper*' |
        Where-Object { $_.Extension -in @('', '.exe') } | Select-Object -First 1
    if (-not $executable) { throw 'no binarypaper executable in the archive' }

    Write-Host "   $($executable.FullName)"

    foreach ($required in @('LICENSE', 'NOTICE', 'NOTICE.codecs', 'THIRD-PARTY-NOTICES')) {
        if (-not (Test-Path (Join-Path $toolDirectory $required))) {
            throw "the archive does not carry $required; someone downloading only this binary would not receive the terms"
        }
    }

    Write-Host '   LICENSE, NOTICE, NOTICE.codecs, and THIRD-PARTY-NOTICES present'

    # A zip does not carry the Unix executable bit, so on Linux and macOS the extracted binary
    # arrives without permission to run. This is the same step a real user takes, and doing it here
    # rather than hiding it is the point: the drill should walk the path the instructions describe.
    if (-not $IsWindows) {
        chmod +x $executable.FullName
        if ($LASTEXITCODE -ne 0) { throw "could not make $($executable.FullName) executable" }
        Write-Host '   made the binary executable (a zip does not carry that bit)'
    }

    # ------------------------------------------------------------- copy images out of the repo
    Write-Host "`n3. Copy page images to scratch (nothing else from the repository)"
    $imagesCopy = Join-Path $scratch 'pages'
    New-Item -ItemType Directory -Path $imagesCopy | Out-Null
    Get-ChildItem $ImagesDirectory -Include '*.png', '*.jpg', '*.jpeg' -Recurse | ForEach-Object {
        Copy-Item $_.FullName $imagesCopy
    }

    $imageCount = (Get-ChildItem $imagesCopy -File).Count
    if ($imageCount -eq 0) { throw "no page images found in $ImagesDirectory" }
    Write-Host "   $imageCount page image(s)"

    # ------------------------------------------------------------- recover
    Write-Host "`n4. Recover in a container with no network or source checkout"
    $outputDirectory = Join-Path $scratch 'recovered'
    # Pull first: no network is available once the recovery process starts.
    docker pull $ContainerImage | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Docker could not obtain the isolated runtime environment' }
    $containerId = docker create --network none --read-only --cap-drop ALL `
        --security-opt no-new-privileges --pids-limit 128 --memory 2g `
        --env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet-bundle `
        --tmpfs /tmp:rw,exec,size=512m --mount "type=bind,source=$scratch,target=/drill" `
        --workdir /drill --entrypoint /bin/sh $ContainerImage -c `
        'test -d /sys/class/net/lo || exit 97; for interface in /sys/class/net/*; do if [ -d "$interface" ] && [ "${interface##*/}" != lo ]; then exit 97; fi; done; chmod +x /drill/tool/binarypaper; /drill/tool/binarypaper --version; exec /drill/tool/binarypaper recover /drill/pages --output /drill/recovered'
    if ($LASTEXITCODE -ne 0) { throw 'Docker could not create the isolated recovery process' }
    $isolation = docker inspect $containerId --format '{{.HostConfig.NetworkMode}}'
    if ($LASTEXITCODE -ne 0 -or $isolation -ne 'none') { throw 'Networking was not disabled; refusing to run the drill' }
    $containerImageId = docker inspect $containerId --format '{{.Image}}'
    if ($LASTEXITCODE -ne 0) { throw 'Could not record the container image identity' }
    Write-Host '   Verified Docker NetworkMode=none; the child also requires loopback to be its only interface'
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $drillOutput = @(docker start --attach $containerId 2>&1)
    $drillExit = $LASTEXITCODE
    $stopwatch.Stop()
    $drillOutput | ForEach-Object { Write-Host "   $_" }
    if ($drillExit -ne 0) {
        throw "recovery failed with exit code $drillExit"
    }

    # ------------------------------------------------------------- verify the bytes
    Write-Host "`n5. Verify the recovered bytes"
    $recovered = Get-ChildItem $outputDirectory -File | Where-Object { $_.Name -ne 'RECOVERED.txt' } |
        Select-Object -First 1
    if (-not $recovered) { throw 'nothing was recovered' }

    $recoveredHash = (Get-FileHash $recovered.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($recoveredHash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "recovered bytes differ`n  expected $ExpectedSha256`n  actual   $recoveredHash"
    }

    Write-Host "   $($recovered.Name)"
    Write-Host "   sha256 $recoveredHash  matches the expected output"

    Write-Host "`n$('=' * 60)"
    Write-Host 'DRILL PASSED' -ForegroundColor Green
    Write-Host ''
    Write-Host "  archive      $($archive.Name)"
    $toolVersion = $drillOutput | Where-Object { "$_" -match '^binarypaper ' } | Select-Object -First 1
    Write-Host "  tool version $toolVersion"
    Write-Host "  images       $imageCount"
    Write-Host "  elapsed      $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"
    Write-Host ''
    Write-Host 'Recovered from page images using only a checksum-verified release archive, with'
    Write-Host 'nothing from this repository beyond the images themselves.'
    if ($ReportPath) {
        [ordered]@{
            schemaVersion = 1; result = 'passed'; archive = $archive.Name; archiveSha256 = $actualHash
            toolVersion = "$toolVersion"; networkMode = $isolation; containerImage = $ContainerImage
            containerImageId = $containerImageId
            imageCount = $imageCount; outputSha256 = $recoveredHash
            elapsedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
        } | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    }
    exit 0
}
finally {
    if ($containerId) { docker rm --force $containerId | Out-Null }
    if (Test-Path $scratch) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedScratch = [IO.Path]::GetFullPath($scratch)
        if ($resolvedScratch.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedScratch) -like 'bp-drill-*') {
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
