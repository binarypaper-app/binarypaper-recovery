#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Writes an SPDX 2.3 SBOM for the recovery kit.

.DESCRIPTION
    Lists every third-party component a release actually contains: the NuGet packages the CLI
    depends on, and the codec source vendored into this repository.

    The vendored codecs matter as much as the packages and are easy to leave out of an SBOM,
    because no package manager knows about them. Someone auditing what they are running needs to see
    them, and someone tracking a vulnerability in one of those projects needs to be able to find
    this kit by searching for it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$namespace = "https://binarypaper.app/spdx/binarypaper-recovery-$Version-$([guid]::NewGuid())"

# The component list lives in third-party/components.json, which is also what
# write-third-party-notices.ps1 assembles the archive's notices from and what publication-audit.ps1
# checks. One list: an SBOM that disagreed with the notices would be worse than either alone,
# because both are believed.
$components = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'third-party/components.json') -Raw |
    ConvertFrom-Json

$packages = $components.packages
$vendored = $components.vendored

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('{')
[void]$builder.AppendLine('  "spdxVersion": "SPDX-2.3",')
[void]$builder.AppendLine('  "dataLicense": "CC0-1.0",')
[void]$builder.AppendLine('  "SPDXID": "SPDXRef-DOCUMENT",')
[void]$builder.AppendLine("  `"name`": `"binarypaper-recovery-$Version`",")
[void]$builder.AppendLine("  `"documentNamespace`": `"$namespace`",")
[void]$builder.AppendLine('  "creationInfo": {')
[void]$builder.AppendLine("    `"created`": `"$created`",")
[void]$builder.AppendLine('    "creators": ["Organization: BinaryPaper", "Tool: tools/write-sbom.ps1"]')
[void]$builder.AppendLine('  },')
[void]$builder.AppendLine('  "packages": [')

$entries = [System.Collections.Generic.List[string]]::new()

$entries.Add(@"
    {
      "SPDXID": "SPDXRef-Package-binarypaper-recovery",
      "name": "binarypaper-recovery",
      "versionInfo": "$Version",
      "downloadLocation": "https://github.com/binarypaper-app/binarypaper-recovery",
      "filesAnalyzed": false,
      "licenseConcluded": "Apache-2.0",
      "licenseDeclared": "Apache-2.0",
      "copyrightText": "Copyright 2026 BinaryPaper",
      "description": "Recovery specification, conformance vectors, and reference recovery tool for the BinaryPaper capsule protocol."
    }
"@)

foreach ($component in ($packages + $vendored)) {
    $id = "SPDXRef-Package-$($component.name -replace '[^A-Za-z0-9]', '-')"
    $relationship = if ($vendored -contains $component) { 'vendored source' } else { 'NuGet package' }

    $entries.Add(@"
    {
      "SPDXID": "$id",
      "name": "$($component.name)",
      "versionInfo": "$($component.version)",
      "downloadLocation": "$($component.url)",
      "filesAnalyzed": false,
      "licenseConcluded": "$($component.licence)",
      "licenseDeclared": "$($component.licence)",
      "description": "$($component.purpose) [$relationship]"
    }
"@)
}

[void]$builder.AppendLine(($entries -join ",`n"))
[void]$builder.AppendLine('  ],')
[void]$builder.AppendLine('  "relationships": [')

$relationships = [System.Collections.Generic.List[string]]::new()
foreach ($component in ($packages + $vendored)) {
    $id = "SPDXRef-Package-$($component.name -replace '[^A-Za-z0-9]', '-')"
    $relationships.Add(@"
    {
      "spdxElementId": "SPDXRef-Package-binarypaper-recovery",
      "relationshipType": "DEPENDS_ON",
      "relatedSpdxElement": "$id"
    }
"@)
}

[void]$builder.AppendLine(($relationships -join ",`n"))
[void]$builder.AppendLine('  ]')
[void]$builder.AppendLine('}')

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

Set-Content -Path $OutputPath -Value $builder.ToString() -Encoding utf8NoBOM

# Fail loudly rather than shipping an SBOM that is not valid JSON.
$null = Get-Content $OutputPath -Raw | ConvertFrom-Json

Write-Host "    $(($packages + $vendored).Count) component(s)"
