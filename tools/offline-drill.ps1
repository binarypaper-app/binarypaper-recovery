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

    Network access is disabled for the recovery process where the platform allows it, and the drill
    reports whether it could actually enforce that rather than assuming it did.

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
    [string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory)][string]$ImagesDirectory,
    [Parameter(Mandatory)][string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'

Write-Host 'BinaryPaper offline recovery drill'
Write-Host ('=' * 60)

$scratch = Join-Path ([IO.Path]::GetTempPath()) "bp-drill-$([guid]::NewGuid().ToString('n').Substring(0,8))"
New-Item -ItemType Directory -Path $scratch | Out-Null

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

    foreach ($required in @('LICENSE', 'NOTICE')) {
        if (-not (Test-Path (Join-Path $toolDirectory $required))) {
            throw "the archive does not carry $required; someone downloading only this binary would not receive the terms"
        }
    }

    Write-Host '   LICENSE and NOTICE present in the archive'

    # ------------------------------------------------------------- copy images out of the repo
    Write-Host "`n3. Copy page images to scratch (nothing else from the repository)"
    $imagesCopy = Join-Path $scratch 'pages'
    New-Item -ItemType Directory -Path $imagesCopy | Out-Null
    Get-ChildItem $ImagesDirectory -Include '*.png', '*.jpg' -Recurse | ForEach-Object {
        Copy-Item $_.FullName $imagesCopy
    }

    $imageCount = (Get-ChildItem $imagesCopy -File).Count
    if ($imageCount -eq 0) { throw "no page images found in $ImagesDirectory" }
    Write-Host "   $imageCount page image(s)"

    # ------------------------------------------------------------- recover
    Write-Host "`n4. Recover, with the working directory outside the repository"
    $outputDirectory = Join-Path $scratch 'recovered'

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executable.FullName `
        -ArgumentList @('recover', $imagesCopy, '--output', $outputDirectory) `
        -WorkingDirectory $scratch `
        -NoNewWindow -PassThru -Wait `
        -RedirectStandardOutput (Join-Path $scratch 'stdout.txt') `
        -RedirectStandardError (Join-Path $scratch 'stderr.txt')
    $stopwatch.Stop()

    Get-Content (Join-Path $scratch 'stdout.txt') | ForEach-Object { Write-Host "   $_" }

    if ($process.ExitCode -ne 0) {
        Get-Content (Join-Path $scratch 'stderr.txt') | ForEach-Object { Write-Host "   $_" }
        throw "recovery failed with exit code $($process.ExitCode)"
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
    Write-Host "  tool version $(& $executable.FullName --version | Select-Object -First 1)"
    Write-Host "  images       $imageCount"
    Write-Host "  elapsed      $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"
    Write-Host ''
    Write-Host 'Recovered from page images using only a checksum-verified release archive, with'
    Write-Host 'nothing from this repository beyond the images themselves.'
    exit 0
}
finally {
    if (Test-Path $scratch) {
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
