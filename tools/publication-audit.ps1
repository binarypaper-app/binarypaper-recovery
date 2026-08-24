#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Audits this repository for anything that must not be published.

.DESCRIPTION
    Run before any change to repository visibility, and again before every release.

    Scans the working tree AND the full Git history. History matters more than the tip: deleting a
    secret in the latest commit does nothing, because the object is still reachable in the history
    of every clone. A finding in history is not a cleanup task, it is a decision about whether the
    repository can be published at all.

    Exits non-zero on any finding. There is no "warn and continue" mode by design - a publication
    audit that can be ignored is not an audit.

.PARAMETER SkipHistory
    Scan only the working tree. For fast iteration during development; never for a release.
#>
[CmdletBinding()]
param(
    [switch]$SkipHistory
)

$ErrorActionPreference = 'Stop'
$findings = [System.Collections.Generic.List[string]]::new()

function Add-Finding([string]$Category, [string]$Detail) {
    $findings.Add("[$Category] $Detail")
}

Write-Host 'BinaryPaper recovery kit - publication audit'
Write-Host ('=' * 60)

# The agent loaders are the documented bridge back to private context and are untracked here on
# purpose. Everything else in the tree is subject to the boundary rules.
$excluded = @('AGENTS.md', 'CLAUDE.md', 'tools/private-patterns.txt')

# --------------------------------------------------------------- private references
Write-Host "`n1. Private references in tracked files"

# The patterns are loaded from a file rather than written here.
#
# That is not indirection for its own sake: a script that hard-codes the names of private
# repositories would itself disclose them the moment this repository is published, and it would
# flag itself forever. Keeping the list external means the published tool contains nothing private
# and stays useful to anyone auditing their own fork.
#
# The file is untracked here; maintainers get it from the private working set.
$patternFile = Join-Path $PSScriptRoot 'private-patterns.txt'
$privatePatterns = @()

if (Test-Path -LiteralPath $patternFile) {
    $privatePatterns = Get-Content -LiteralPath $patternFile |
        Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') }
    Write-Host "   $($privatePatterns.Count) pattern(s) from $(Split-Path -Leaf $patternFile)"
}
else {
    Write-Host '   no private-patterns.txt found - skipping private-reference scan'
    Add-Finding 'audit' 'private-patterns.txt is absent; this run cannot clear the repository for publication'
}

$tracked = git ls-files
if ($privatePatterns.Count -gt 0) {
    foreach ($file in $tracked) {
        if ($excluded -contains $file) { continue }
        if (-not (Test-Path -LiteralPath $file)) { continue }

        # Binary fixtures are checked separately; grepping them produces noise, not findings.
        if ($file -match '\.(bpq|bin|png|jpg|zip)$') { continue }

        $content = Get-Content -LiteralPath $file -Raw -ErrorAction SilentlyContinue
        if ($null -eq $content) { continue }

        foreach ($pattern in $privatePatterns) {
            if ($content -match $pattern) {
                Add-Finding 'private-reference' "$file matches a private-reference pattern"
            }
        }
    }
}

Write-Host "   scanned $($tracked.Count) tracked file(s)"

# --------------------------------------------------------------- secrets
Write-Host "`n2. Secret-shaped strings"

$secretPatterns = @{
    'private key'      = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY'
    'AWS access key'   = 'AKIA[0-9A-Z]{16}'
    'GitHub token'     = 'gh[pousr]_[A-Za-z0-9]{36}'
    'Slack token'      = 'xox[baprs]-[0-9A-Za-z-]{10,}'
    'generic api key'  = '(?i)(api[_-]?key|secret[_-]?key|access[_-]?token)\s*[:=]\s*["''][A-Za-z0-9_\-]{20,}'
    'keystore'         = '(?i)\.(jks|keystore|p12|pfx)\b'
}

foreach ($file in $tracked) {
    if ($excluded -contains $file) { continue }
    if (-not (Test-Path -LiteralPath $file)) { continue }
    if ($file -match '\.(bpq|bin|png|jpg|zip)$') { continue }

    $content = Get-Content -LiteralPath $file -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) { continue }

    foreach ($name in $secretPatterns.Keys) {
        if ($content -match $secretPatterns[$name]) {
            Add-Finding 'secret' "$file looks like it contains a $name"
        }
    }
}

Write-Host '   done'

# --------------------------------------------------------------- licensing completeness
Write-Host "`n3. Licence and notice completeness"

foreach ($required in @('LICENSE', 'NOTICE', 'README.md', 'SECURITY.md', 'TRADEMARKS.md', 'CONTRIBUTING.md', 'CHANGELOG.md')) {
    if (-not (Test-Path -LiteralPath $required)) {
        Add-Finding 'licence' "missing required file: $required"
    }
}

# Vendored third-party material must carry its upstream notice next to it.
foreach ($vendored in @('protocol/1.0/codecs', 'cli/src/BinaryPaper.Recovery.Codecs')) {
    if ((Test-Path -LiteralPath $vendored) -and -not (Test-Path -LiteralPath (Join-Path $vendored 'NOTICE'))) {
        Add-Finding 'licence' "vendored material in $vendored has no NOTICE"
    }
}

# Every first-party source file should carry an SPDX identifier. Vendored files keep their own
# upstream headers and are deliberately not rewritten.
$sourceFiles = $tracked | Where-Object { $_ -match '\.cs$' -and $_ -notmatch 'Codecs/' }
$missingSpdx = @()
foreach ($file in $sourceFiles) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $head = Get-Content -LiteralPath $file -TotalCount 5 -ErrorAction SilentlyContinue
    if (($head -join "`n") -notmatch 'SPDX-License-Identifier') {
        $missingSpdx += $file
    }
}

if ($missingSpdx.Count -gt 0) {
    foreach ($file in $missingSpdx) {
        Add-Finding 'licence' "no SPDX-License-Identifier header: $file"
    }
}

Write-Host "   checked $($sourceFiles.Count) first-party source file(s)"

# --------------------------------------------------------------- fixture hygiene
Write-Host "`n4. Fixture hygiene"

# Fixture passwords must be labelled as test data so nobody reads one as a recommendation.
$manifests = $tracked | Where-Object { $_ -match 'vectors/.*manifest\.json$' }
foreach ($file in $manifests) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $content = Get-Content -LiteralPath $file -Raw
    if ($content -match '"password"' -and $content -notmatch '"kind":\s*"public-test-value"') {
        Add-Finding 'fixture' "$file carries a password that is not labelled as public test data"
    }
}

Write-Host "   checked $($manifests.Count) vector manifest(s)"

# --------------------------------------------------------------- history
if (-not $SkipHistory) {
    Write-Host "`n5. Full Git history"

    $commitCount = (git rev-list --count HEAD)
    Write-Host "   scanning $commitCount commit(s)"

    # Paths that ever existed, even if deleted since. A file removed at the tip is still in every
    # clone's history.
    $everTracked = git log --all --pretty=format: --name-only --diff-filter=A | Sort-Object -Unique |
        Where-Object { $_ -and $_.Trim() }

    foreach ($file in $everTracked) {
        if ($file -match '(?i)\.(jks|keystore|p12|pfx|pem|key)$') {
            Add-Finding 'history' "a credential-shaped file existed in history: $file"
        }
    }

    # Private references anywhere in the history of tracked text, not just at the tip.
    if ($privatePatterns.Count -gt 0) {
        $joined = ($privatePatterns -join '|')
        $historyHits = git grep -I -n -E $joined $(git rev-list --all) `
            -- ':!AGENTS.md' ':!CLAUDE.md' ':!tools/private-patterns.txt' 2>$null | Select-Object -First 5

        if ($historyHits) {
            foreach ($hit in $historyHits) {
                # Deliberately reports the location without echoing the matched text: an audit log
                # that quotes the secret it found is itself a place the secret now lives.
                $location = ($hit -split ':')[0..1] -join ':'
                Add-Finding 'history' "private reference in history at $location"
            }
        }
    }

    # Author identities that will be permanently visible.
    $authors = git log --all --format='%an <%ae>' | Sort-Object -Unique
    Write-Host "   commit identities that will be public:"
    foreach ($author in $authors) {
        Write-Host "     $author"
    }
}
else {
    Write-Host "`n5. Full Git history - SKIPPED"
    Add-Finding 'audit' 'history scan was skipped; this run is not sufficient for a release'
}

# --------------------------------------------------------------- verdict
Write-Host "`n$('=' * 60)"

if ($findings.Count -eq 0) {
    Write-Host 'PASS - no findings.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'This checks content. It does not authorise publication, which is a separate'
    Write-Host 'decision by the repository owner.'
    exit 0
}

Write-Host "FAIL - $($findings.Count) finding(s):" -ForegroundColor Red
foreach ($finding in $findings) {
    Write-Host "  $finding"
}

Write-Host ''
Write-Host 'A finding in the working tree is a fix. A finding in history is a decision about'
Write-Host 'whether this repository can be published at all: removing it from the tip does not'
Write-Host 'remove it from clones.'
exit 1
