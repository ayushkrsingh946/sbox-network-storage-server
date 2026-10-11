<#
.SYNOPSIS
  Prints the "Tested with" markdown table for a server release from compat/library-matrix.json.

.DESCRIPTION
  Selects the `tested` rows that belong to a release: rows whose `server` equals -Version, plus rows
  recorded against -Commit (a row's `commit` and the given commit match when one is a prefix of the other).
  The output is meant to be pasted into, or appended to, the GitHub Release notes. The maintainer wires it
  into the release workflow; see docs/releasing.md.

  Works on Windows PowerShell 5.1 and PowerShell 7 with no extra modules.

.PARAMETER Version
  Server version without the leading "v", for example 0.5.0.

.PARAMETER Commit
  Release commit SHA (full or abbreviated). Optional; rows recorded against the commit are included.

.PARAMETER Matrix
  Path to the matrix. Defaults to compat/library-matrix.json in this repository.

.PARAMETER CiOnly
  Marks the release as ci-only: it was released without an editor run. The notes say so.

.PARAMETER Gate
  Release gate: exit with code 2 unless a `pass` row exists for the matrix's `recommended` library,
  or -CiOnly was given. The table is printed either way.

.EXAMPLE
  ./scripts/compat-tested-with.ps1 -Version 0.5.0 -Commit 6386646

.EXAMPLE
  ./scripts/compat-tested-with.ps1 -Version 0.5.0 -Gate | Out-File tested-with.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Commit = '',
    [string]$Matrix = '',
    [switch]$CiOnly,
    [switch]$Gate
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Matrix)) {
    $Matrix = Join-Path (Join-Path $PSScriptRoot '..') 'compat/library-matrix.json'
}
if (-not (Test-Path -LiteralPath $Matrix)) {
    throw "Matrix not found: $Matrix"
}

$Version = $Version.Trim().TrimStart('v', 'V')
$data = Get-Content -LiteralPath $Matrix -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @($data.tested)

function Test-Selected($row) {
    if ($row.server -eq $Version) { return $true }
    if ($Commit -and $row.PSObject.Properties['commit'] -and $row.commit) {
        $a = $Commit.ToLowerInvariant()
        $b = ([string]$row.commit).ToLowerInvariant()
        return $a.StartsWith($b) -or $b.StartsWith($a)
    }
    return $false
}

function Format-Evidence($items) {
    $links = foreach ($item in @($items)) {
        $text = [string]$item
        if ($text -match '^([\w.-]+/[\w.-]+)#(\d+)$') {
            '[{0}#{1}](https://github.com/{0}/issues/{1})' -f $Matches[1], $Matches[2]
        }
        elseif ($text -match '^https?://') {
            '<{0}>' -f $text
        }
        else {
            $text
        }
    }
    return ($links -join ', ')
}

function Format-Cell($value) {
    if ($null -eq $value -or [string]$value -eq '') { return '-' }
    return ([string]$value).Replace('|', '\|')
}

function Get-Field($row, $name) {
    if ($row.PSObject.Properties[$name]) { return $row.$name }
    return $null
}

$selected = @($rows | Where-Object { Test-Selected $_ })

# Newest library first, then by engine, so the recommended library leads.
$selected = @($selected | Sort-Object -Property @{ Expression = { [version]($_.library -replace '[-+].*$', '') }; Descending = $true }, @{ Expression = { Get-Field $_ 'engine' }; Descending = $true })

$out = New-Object System.Collections.Generic.List[string]
$out.Add('### Tested with')
$out.Add('')

if ($selected.Count -eq 0) {
    $out.Add("No test evidence is recorded for server $Version in compat/library-matrix.json.")
}
else {
    $out.Add('| Library | Revision | Engine | OS | Server kind | Database | Result | Evidence |')
    $out.Add('|---|---|---|---|---|---|---|---|')
    foreach ($row in $selected) {
        $database = (@($row.database) -join ', ')
        $out.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |' -f
                (Format-Cell $row.library),
                (Format-Cell (Get-Field $row 'revision')),
                (Format-Cell (Get-Field $row 'engine')),
                (Format-Cell (Get-Field $row 'os')),
                (Format-Cell (Get-Field $row 'kind')),
                (Format-Cell $database),
                (Format-Cell $row.result),
                (Format-Evidence $row.evidence)))
    }
}

$recommended = [string]$data.recommended
$hasPass = @($selected | Where-Object { $_.result -eq 'pass' -and $_.library -eq $recommended }).Count -gt 0

$out.Add('')
if ($CiOnly) {
    $out.Add("**ci-only release:** no editor run was recorded for the recommended library ($recommended). The server was verified by CI only.")
}
elseif (-not $hasPass) {
    $out.Add("**Warning:** no ``pass`` row for the recommended library ($recommended) exists for this release. Record an editor run in compat/library-matrix.json (docs/editor-test-plan.md), or release as ci-only.")
}

$out -join "`n"

if ($Gate -and -not $CiOnly -and -not $hasPass) {
    [Console]::Error.WriteLine("Release gate failed: no 'pass' row for recommended library $recommended on server $Version (and -CiOnly was not given).")
    exit 2
}
