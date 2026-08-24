#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks the conformance suite's own integrity, independently of any reader.

.DESCRIPTION
    The suite runners answer "does this implementation behave correctly?". This answers a question
    they cannot: "is the suite itself intact?".

    It verifies that:
      - every vector manifest is valid JSON with the fields the schema requires
      - every declared fixture file exists with the declared length and hash
      - the aggregate manifest hashes every vector manifest
      - no fixture file is present on disk without being declared

    That last check is the one that catches drift. A file nobody declares is a file nobody verifies,
    and it is how a suite quietly stops testing what its README says it tests.
#>
[CmdletBinding()]
param(
    [string]$VectorsRoot = 'vectors'
)

$ErrorActionPreference = 'Stop'
$findings = [System.Collections.Generic.List[string]]::new()

Write-Host 'Conformance suite integrity'
Write-Host ('=' * 60)

$aggregatePath = Join-Path $VectorsRoot 'MANIFEST.json'
if (-not (Test-Path $aggregatePath)) { throw "no MANIFEST.json under $VectorsRoot" }

$aggregate = Get-Content $aggregatePath -Raw | ConvertFrom-Json
$declaredFiles = [System.Collections.Generic.HashSet[string]]::new()
[void]$declaredFiles.Add((Resolve-Path $aggregatePath).Path)

foreach ($entry in $aggregate.vectors) {
    $manifestPath = Join-Path $VectorsRoot ($entry.manifest -replace '/', [IO.Path]::DirectorySeparatorChar)

    if (-not (Test-Path $manifestPath)) {
        $findings.Add("$($entry.id): manifest missing at $($entry.manifest)")
        continue
    }

    [void]$declaredFiles.Add((Resolve-Path $manifestPath).Path)

    $actualHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.sha256) {
        $findings.Add("$($entry.id): manifest hash differs from MANIFEST.json")
        continue
    }

    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $directory = Split-Path -Parent $manifestPath

    foreach ($required in @('schemaVersion', 'protocol', 'id', 'category', 'operation', 'inputs', 'expected')) {
        if ($null -eq $manifest.$required) {
            $findings.Add("$($entry.id): manifest is missing '$required'")
        }
    }

    $files = @($manifest.inputs)
    if ($manifest.expected.outputs) { $files += @($manifest.expected.outputs) }

    foreach ($file in $files) {
        $filePath = Join-Path $directory $file.path
        if (-not (Test-Path $filePath)) {
            $findings.Add("$($entry.id): declared file missing: $($file.path)")
            continue
        }

        [void]$declaredFiles.Add((Resolve-Path $filePath).Path)

        $item = Get-Item $filePath
        if ($item.Length -ne $file.length) {
            $findings.Add("$($entry.id): $($file.path) is $($item.Length) bytes, manifest says $($file.length)")
        }

        $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $file.sha256) {
            $findings.Add("$($entry.id): $($file.path) hash differs from the manifest")
        }
    }
}

Write-Host "  $($aggregate.vectors.Count) vector(s) declared"

# Orphans: on disk, declared by nobody.
$onDisk = Get-ChildItem (Join-Path $VectorsRoot '1.0') -Recurse -File | ForEach-Object { $_.FullName }
foreach ($file in $onDisk) {
    if (-not $declaredFiles.Contains($file)) {
        $relative = $file.Substring((Resolve-Path $VectorsRoot).Path.Length + 1)
        $findings.Add("undeclared file present: $relative")
    }
}

Write-Host "  $($onDisk.Count) file(s) on disk"

Write-Host ''
if ($findings.Count -eq 0) {
    Write-Host 'PASS - the suite is intact.' -ForegroundColor Green
    exit 0
}

Write-Host "FAIL - $($findings.Count) finding(s):" -ForegroundColor Red
foreach ($finding in $findings) { Write-Host "  $finding" }
exit 1
