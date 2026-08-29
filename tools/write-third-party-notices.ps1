#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Assembles THIRD-PARTY-NOTICES for a release archive.

.DESCRIPTION
    A release archive is one self-contained binary. The dependencies are inside it, and so is the
    .NET runtime, and their MIT and Apache-2.0 terms require their notices to travel with the code
    rather than being available somewhere else. An SPDX identifier in the SBOM is a reference to a
    licence; it is not the notice text the licence asks you to carry.

    The dependency texts come from third-party/notices. The .NET runtime's own LICENSE.TXT and
    THIRD-PARTY-NOTICES.TXT are taken from the runtime pack that was actually published for this
    runtime identifier, rather than from a copy in this repository that would silently go stale.

    Fails rather than emitting a partial file. An incomplete notices file is worse than an obvious
    build failure, because it looks like compliance.

.PARAMETER RuntimeIdentifier
    The RID whose runtime pack supplies the .NET notices, e.g. linux-x64.

.PARAMETER OutputPath
    Where to write THIRD-PARTY-NOTICES.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$componentsPath = Join-Path $repoRoot 'third-party/components.json'
$noticeRoot = Join-Path $repoRoot 'third-party/notices'

$components = Get-Content -LiteralPath $componentsPath -Raw | ConvertFrom-Json

$out = [System.Text.StringBuilder]::new()

function Add-Section([string]$Title, [string]$Body) {
    [void]$out.AppendLine(('-' * 78))
    [void]$out.AppendLine($Title)
    [void]$out.AppendLine(('-' * 78))
    [void]$out.AppendLine()
    [void]$out.AppendLine($Body.TrimEnd())
    [void]$out.AppendLine()
}

[void]$out.AppendLine('THIRD-PARTY NOTICES')
[void]$out.AppendLine()
[void]$out.AppendLine('The BinaryPaper Recovery Kit ships as a single self-contained executable. The')
[void]$out.AppendLine('components below are inside it, and are redistributed under the terms reproduced')
[void]$out.AppendLine('here. The kit itself is licensed separately; see LICENSE and NOTICE.')
[void]$out.AppendLine()

foreach ($component in $components.packages) {
    if (-not $component.notice) {
        throw "component $($component.name) has no notice file declared in components.json"
    }

    $path = Join-Path $noticeRoot $component.notice
    if (-not (Test-Path -LiteralPath $path)) {
        throw "component $($component.name) declares notice '$($component.notice)', which does not exist"
    }

    Add-Section `
        "$($component.name) $($component.version) — $($component.licence)`n$($component.url)" `
        (Get-Content -LiteralPath $path -Raw)
}

foreach ($component in $components.vendored) {
    Add-Section `
        "$($component.name) $($component.version) — $($component.licence)" `
        ("Vendored as source into this kit. Its upstream notice is redistributed with this`n" +
            "release as NOTICE.codecs, and is also kept beside the source it applies to.")
}

# ------------------------------------------------------------------ .NET runtime
#
# A self-contained publish embeds the runtime, so its terms come with the archive. The runtime pack
# carries both files; a publish does not copy them, which is exactly how they go missing.
#
# The package root is resolved the way NuGet resolves it, and cross-platform:
# GetFolderPath('UserProfile') is $HOME on Unix and %USERPROFILE% on Windows, where reading the
# environment variable directly yields null on the other platform.
$packageRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
}
else {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
}

$packRoot = Join-Path $packageRoot "microsoft.netcore.app.runtime.$RuntimeIdentifier"

if (-not (Test-Path -LiteralPath $packRoot)) {
    throw "no .NET runtime pack for $RuntimeIdentifier under $packageRoot; publish it before writing notices"
}

# Highest version present: the publish restored it, and a stale sibling must not win.
$pack = Get-ChildItem -LiteralPath $packRoot -Directory |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } |
    Select-Object -Last 1

foreach ($file in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    $path = Join-Path $pack.FullName $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "the .NET runtime pack at $($pack.FullName) has no $file"
    }
}

Add-Section `
    ".NET runtime $($pack.Name) ($RuntimeIdentifier) — MIT`nhttps://github.com/dotnet/runtime" `
    (Get-Content -LiteralPath (Join-Path $pack.FullName 'LICENSE.TXT') -Raw)

Add-Section `
    ".NET runtime $($pack.Name) — components within the runtime" `
    (Get-Content -LiteralPath (Join-Path $pack.FullName 'THIRD-PARTY-NOTICES.TXT') -Raw)

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

Set-Content -LiteralPath $OutputPath -Value $out.ToString() -Encoding utf8NoBOM
Write-Host ("    THIRD-PARTY-NOTICES: {0} component(s) plus the .NET runtime" -f
    ($components.packages.Count + $components.vendored.Count))
