# Regression checks for the release inventory gate. No package restore or production fixture edits.
$ErrorActionPreference = 'Stop'
$toolsRoot = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('bp-deps-test-' + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $catalog = Get-Content (Join-Path $toolsRoot '../third-party/components.json') -Raw | ConvertFrom-Json
    $libraries = [ordered]@{}
    foreach ($package in $catalog.packages) { $libraries["$($package.name)/$($package.version)"] = @{ type = 'package' } }
    $libraries['runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.6'] = @{ type = 'runtimepack' }
    $path = Join-Path $scratch 'binarypaper.deps.json'
    function Save { @{ libraries = $libraries } | ConvertTo-Json -Depth 6 | Set-Content $path }
    function MustRefuse([string]$Description) {
        $refused = $false
        try { & (Join-Path $toolsRoot 'read-release-dependencies.ps1') -DepsPath $path -RuntimeIdentifier linux-x64 | Out-Null }
        catch { $refused = $true }
        if (-not $refused) { throw "Did not refuse: $Description" }
    }
    Save
    $resolved = @(& (Join-Path $toolsRoot 'read-release-dependencies.ps1') -DepsPath $path -RuntimeIdentifier linux-x64)
    if (($resolved | Where-Object name -eq 'Microsoft.NETCore.App.Runtime.linux-x64').version -ne '10.0.6') {
        throw 'The exact built runtime was not retained'
    }
    $libraries['Unreviewed.Transitive/1.0.0'] = @{ type = 'package' }; Save
    MustRefuse 'unreviewed transitive dependency'
    $libraries.Remove('Unreviewed.Transitive/1.0.0')
    $libraries.Remove('runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.6'); Save
    MustRefuse 'missing runtime'
    $libraries['runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.6'] = @{ type = 'runtimepack' }; Save
    MustRefuse 'runtime architecture mismatch'
    Write-Host 'PASS: release dependency graph retains the runtime and refuses unreviewed or mismatched dependencies'
}
finally {
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedScratch) -like 'bp-deps-test-*') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
