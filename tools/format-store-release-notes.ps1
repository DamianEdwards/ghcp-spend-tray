param(
    [Parameter(Mandatory)][string] $Body,
    [Parameter(Mandatory)][string] $Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$section = [regex]::Match($Body, '(?ms)^## What''s Changed[ \t]*\r?\n(?<items>.*?)(?=^## |\z)')
if (-not $section.Success) {
    throw 'The GitHub release must have a What''s Changed section for Store release notes.'
}
$titles = @(
    foreach ($line in ($section.Groups['items'].Value -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -cmatch '\A\* (?<title>.+?) by @[^ \r\n]+ in https://github\.com/[^/\s]+/[^/\s]+/pull/[0-9]+\z') {
            $title = $Matches.title.Trim()
        } else {
            throw "Unrecognized GitHub release change entry; review the release notes for $Version."
        }
        if ($title -match '[`*<>\[\]]') {
            throw "GitHub release change titles must be plain text for the Store ($Version)."
        }
        "- $title"
    }
)
if ($titles.Count -eq 0) { throw "The GitHub release has no changes to publish for $Version." }
$notes = "What's new in $Version`n" + ($titles -join "`n")
if ($notes.Length -gt 1500) { throw "Store release notes exceed 1500 characters for $Version." }
$notes
