# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.
<#
.SYNOPSIS
Fails when lines added since a base commit contain an em dash.
.DESCRIPTION
Only added lines are checked, so text that is already in the repo is left alone. Escapes that
render as an em dash in code, XAML or HTML count too.
Rewrite the sentence with a comma, colon, parentheses, or a period instead.
.PARAMETER Base
The commit to compare with. Defaults to where this branch left origin/main.
.EXAMPLE
./scripts/check-em-dash.ps1
.EXAMPLE
./scripts/check-em-dash.ps1 -Base HEAD^1
#>
[CmdletBinding()]
param([string]$Base)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $Base) {
    $Base = git merge-base origin/main HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not find where this branch left origin/main. Pass -Base." }
}

# Built from parts so this file does not match itself.
$dash = [string][char]0x2014
$pattern = (@([regex]::Escape($dash), ('\\' + 'u2014'), ('&#x' + '2014;'), ('&#' + '8212;'), ('&' + 'mdash;')) -join '|')
$regex = [regex]::new($pattern, 'IgnoreCase')

$diff = git -c core.quotepath=false diff --no-color --no-ext-diff --unified=0 $Base --
if ($LASTEXITCODE -ne 0) { throw "git diff against '$Base' failed." }

$file = $null
$line = 0
$found = [Collections.Generic.List[string]]::new()
foreach ($text in $diff) {
    if ($text.StartsWith('+++ ')) {
        $file = if ($text.StartsWith('+++ b/')) { $text.Substring(6) } else { $null }
        continue
    }
    if ($text -match '^@@ -\S+ \+(\d+)') {
        $line = [int]$Matches[1]
        continue
    }
    if ($file -and $text.StartsWith('+')) {
        if ($regex.IsMatch($text)) { $found.Add("${file}:${line}: $($text.Substring(1).Trim())") }
        $line++
    }
}

if ($found.Count -eq 0) {
    Write-Host "No em dashes in added lines."
    exit 0
}
Write-Host "Added lines contain an em dash. Use a comma, colon, parentheses, or a period instead:"
$found | ForEach-Object { Write-Host "  $_" }
exit 1
