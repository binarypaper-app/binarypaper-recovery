#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the release artifacts for the recovery kit.

.DESCRIPTION
    Produces, into an output directory:

      - self-contained CLI archives for each requested runtime identifier
      - a source archive
      - an SPDX SBOM listing every dependency
      - SHA256SUMS covering every artifact

    This builds artifacts. It does NOT tag, push, or publish anything - those are the owner's
    decision, and nothing here touches a remote.

    The verification instructions this produces are meant to work *before* the binary is run. A
    checksum you can only obtain by executing the thing you are checking is not a check.

.PARAMETER Version
    The kit version, e.g. 1.0.0. Used in artifact names only.

.PARAMETER OutputDirectory
    Where to place the artifacts.

.PARAMETER RuntimeIdentifiers
    Which platforms to build. Defaults to the agreed matrix.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = 'artifacts',
    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $output = Join-Path $repoRoot $OutputDirectory
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output | Out-Null

    Write-Host "Building recovery kit $Version"
    Write-Host ('=' * 60)

    # ----------------------------------------------------------- CLI archives
    foreach ($rid in $RuntimeIdentifiers) {
        Write-Host "`n  $rid"
        $stage = Join-Path $output "stage-$rid"

        dotnet publish cli/src/BinaryPaper.Recovery.Cli `
            -c Release -r $rid --self-contained true `
            -p:PublishSingleFile=true `
            -p:Version=$Version `
            -o $stage --nologo -v quiet

        if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

        # Every archive carries its own licence and notices. Someone who downloads one binary and
        # nothing else must still receive the terms it is offered under.
        foreach ($file in @('LICENSE', 'NOTICE', 'README.md', 'TRADEMARKS.md')) {
            Copy-Item (Join-Path $repoRoot $file) $stage
        }

        Copy-Item (Join-Path $repoRoot 'protocol/1.0/codecs/NOTICE') (Join-Path $stage 'NOTICE.codecs')

        # Debug symbols are not part of a release archive. They carry no secret, but they are
        # noise in an artifact whose whole point is that a user can verify exactly what it is.
        Get-ChildItem $stage -Filter '*.pdb' | Remove-Item -Force

        $archive = Join-Path $output "binarypaper-recovery-$Version-$rid.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
        Remove-Item $stage -Recurse -Force

        Write-Host ("    {0:N1} MB" -f ((Get-Item $archive).Length / 1MB))
    }

    # ----------------------------------------------------------- source archive
    Write-Host "`n  source archive"
    $sourceArchive = Join-Path $output "binarypaper-recovery-$Version-source.zip"

    # git archive rather than a directory copy: it includes exactly what is tracked, and nothing
    # from a local build or a stray file in the working tree.
    git archive --format=zip --prefix="binarypaper-recovery-$Version/" -o $sourceArchive HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    Write-Host ("    {0:N1} MB" -f ((Get-Item $sourceArchive).Length / 1MB))

    # ----------------------------------------------------------- SBOM
    Write-Host "`n  SBOM"
    $sbomPath = Join-Path $output "binarypaper-recovery-$Version.spdx.json"
    & (Join-Path $PSScriptRoot 'write-sbom.ps1') -Version $Version -OutputPath $sbomPath
    if ($LASTEXITCODE -ne 0) { throw 'SBOM generation failed' }

    # ----------------------------------------------------------- checksums
    Write-Host "`n  SHA256SUMS"
    $sums = Join-Path $output 'SHA256SUMS'
    $lines = Get-ChildItem $output -File | Where-Object { $_.Name -ne 'SHA256SUMS' } |
        Sort-Object Name | ForEach-Object {
            "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
        }

    Set-Content -Path $sums -Value $lines -Encoding utf8NoBOM
    Write-Host "    $($lines.Count) artifact(s) hashed"

    Write-Host "`n$('=' * 60)"
    Write-Host "Artifacts in $output"
    Write-Host ''
    Write-Host 'Nothing has been tagged, pushed, or published. Those are separate decisions.'
    Write-Host 'Before publishing, run tools/publication-audit.ps1 and confirm it passes.'
}
finally {
    Pop-Location
}
