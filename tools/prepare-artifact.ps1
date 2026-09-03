[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('crash', 'dump', 'boundary', 'release')][string]$Kind,
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][string]$Destination,
    [switch]$Optional
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'artifact-policy.ps1')

function Get-RegularFiles([IO.FileSystemInfo]$Item) {
    if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not admitted: $($Item.Name)" }
    if ($Item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Item.FullName -Force) { Get-RegularFiles $child }
    } else { $Item }
}

try {
    if (Test-Path -LiteralPath $Destination) { throw 'Artifact staging directory must be new' }
    $files = @($Paths | ForEach-Object {
        foreach ($pattern in ($_ -split '\r?\n' | Where-Object { $_.Trim() })) {
            # Only filesystem wildcard expansion; no expression evaluation.
            foreach ($item in Get-Item -Path $pattern.Trim() -Force -ErrorAction SilentlyContinue) {
                Get-RegularFiles $item
            }
        }
    } | Sort-Object FullName -Unique)
    if (@($files | Group-Object Name | Where-Object Count -gt 1).Count -gt 0) { throw 'Artifact basenames must be unique' }
    [long]$bytes = ($files | Measure-Object Length -Sum).Sum
    # Refuse large files before even calling the API or copying a dump.
    Assert-ArtifactAdmission $Kind $bytes $files.Count @() | Out-Null
    if (-not $env:GH_TOKEN -or -not $env:GITHUB_REPOSITORY -or -not $env:GITHUB_API_URL) {
        throw 'Authenticated Actions artifact inventory is required'
    }
    $inventory = @()
    $page = 1
    do {
        $uri = "$env:GITHUB_API_URL/repos/$env:GITHUB_REPOSITORY/actions/artifacts?per_page=100&page=$page"
        $response = Invoke-RestMethod -Uri $uri -Headers @{
            Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
        if ($null -eq $response.total_count -or $null -eq $response.artifacts) { throw 'Incomplete artifact inventory' }
        if ($response.artifacts.Count -eq 0 -and $inventory.Count -lt $response.total_count) { throw 'Truncated artifact inventory' }
        $inventory += @($response.artifacts)
        $page++
        if ($page -gt 100) { throw 'Artifact inventory exceeds the bounded scan' }
    } while ($inventory.Count -lt $response.total_count)
    $retained = Assert-ArtifactAdmission $Kind $bytes $files.Count $inventory
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $Destination }
    $copied = @(Get-ChildItem -LiteralPath $Destination -File)
    [long]$copiedBytes = ($copied | Measure-Object Length -Sum).Sum
    Assert-ArtifactAdmission $Kind $copiedBytes $copied.Count $inventory | Out-Null
    if ($copiedBytes -ne $bytes) { throw 'Evidence changed while staging' }
    $line = "Artifact seatbelt: $Kind; $bytes bytes; retained $retained bytes; retention 1 day; ceiling 384 MiB."
    Write-Host $line
    if ($env:GITHUB_STEP_SUMMARY) { $line | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    if ($env:GITHUB_OUTPUT) {
        'allowed=true' | Add-Content -LiteralPath $env:GITHUB_OUTPUT
        "path=$([IO.Path]::GetFullPath($Destination))" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    }
}
catch {
    $message = "Artifact refused: $($_.Exception.Message)"
    if ($env:GITHUB_OUTPUT) { 'allowed=false' | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    Write-Host $message
    if ($env:GITHUB_STEP_SUMMARY) { $message | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    if (-not $Optional) { throw }
}
