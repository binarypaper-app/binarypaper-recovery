# Reads the dependency graph of the binary actually built, including its runtime pack.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DepsPath,
    [Parameter(Mandatory)][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$catalog = Get-Content (Join-Path $PSScriptRoot '../third-party/components.json') -Raw | ConvertFrom-Json
$deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
$foundRuntime = $false
$foundPackages = @()
foreach ($entry in $deps.libraries.PSObject.Properties) {
    $name, $version = $entry.Name -split '/', 2
    if ($entry.Value.type -eq 'package') {
        $component = @($catalog.packages | Where-Object { $_.name -eq $name -and $_.version -eq $version })
        if ($component.Count -ne 1) { throw "No reviewed component/notice for resolved dependency $name/$version" }
        $foundPackages += $name
        $component[0]
    }
    elseif ($entry.Value.type -eq 'runtimepack') {
        if ($name -ne "runtimepack.Microsoft.NETCore.App.Runtime.$RuntimeIdentifier") {
            throw "Unexpected runtime pack $name"
        }
        $foundRuntime = $true
        [pscustomobject]@{
            name = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
            version = $version
            licence = 'MIT'
            purpose = "Self-contained .NET runtime for $RuntimeIdentifier"
            url = "https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.$RuntimeIdentifier/$version"
        }
    }
}
if (-not $foundRuntime) { throw 'The published dependency graph has no self-contained runtime pack' }
foreach ($component in $catalog.packages) {
    if ($foundPackages -notcontains $component.name) { throw "Reviewed dependency $($component.name) is absent from the binary" }
}
