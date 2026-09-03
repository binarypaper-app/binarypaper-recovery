# Shared by the upload gate and its regression checks. Units are binary MiB/GiB.
function Get-ArtifactPolicy([string]$Kind) {
    switch ($Kind) {
        # Reserve all three compact packets plus one requested dump, including wrapper overhead.
        'crash'    { return @{ MaxBytes = 1MB; ReservationBytes = 71MB } }
        'dump'     { return @{ MaxBytes = 64MB; ReservationBytes = 71MB } }
        'boundary' { return @{ MaxBytes = 1MB; ReservationBytes = 6MB } }
        'release'  { return @{ MaxBytes = 256MB; ReservationBytes = 257MB } }
        default    { throw "Unknown artifact policy: $Kind" }
    }
}

function Assert-ArtifactAdmission([string]$Kind, [long]$Bytes, [int]$FileCount, [object[]]$Artifacts) {
    $policy = Get-ArtifactPolicy $Kind
    if ($FileCount -lt 1 -or $FileCount -gt 64) { throw 'An artifact must contain 1–64 files' }
    if ($Bytes -lt 0 -or $Bytes -gt $policy.MaxBytes) {
        throw "$Kind evidence is $Bytes bytes; its uncompressed limit is $($policy.MaxBytes) bytes"
    }
    [long]$retained = 0
    foreach ($artifact in $Artifacts) {
        if ($null -eq $artifact.size_in_bytes -or [long]$artifact.size_in_bytes -lt 0) {
            throw 'Artifact inventory contains an invalid size'
        }
        # Include expired entries until GitHub actually removes them from its inventory.
        $retained += [long]$artifact.size_in_bytes
    }
    if ($retained + $policy.ReservationBytes -gt 384MB) {
        throw "Storage seatbelt: $retained retained bytes plus $($policy.ReservationBytes) reserved bytes exceeds 384 MiB"
    }
    return $retained
}
