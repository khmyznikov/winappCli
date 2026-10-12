# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Lean runner helpers for the authored-edit Todo journey against 'winapp run --watch'.

$script:Utf8 = [System.Text.UTF8Encoding]::new($false)

function ConvertTo-JourneyText($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value -replace "`r`n", "`n" }
    return (@($Value) | ForEach-Object { [string]$_ }) -join "`n"
}

function Get-JourneyPlan {
    param([Parameter(Mandatory)]$Journey, [Parameter(Mandatory)][string]$VariantId)
    $variant = $Journey.variants | Where-Object id -eq $VariantId
    if (-not $variant) { throw "Unknown variant '$VariantId'." }
    $plan = [System.Collections.Generic.List[object]]::new()
    foreach ($segment in $Journey.segments) {
        $actions = if ($segment.kind -eq 'swap') {
            $choice = [string]$variant.choices.($segment.dimension)
            @($segment.orders.$choice | ForEach-Object { $id = $_; $segment.actions | Where-Object id -eq $id })
        } else { @($segment.actions) }
        for ($i = 0; $i -lt $actions.Count; $i++) {
            $checkpoint = $actions[$i].checkpoint
            if (-not $checkpoint -and $i -eq $actions.Count - 1) { $checkpoint = $segment.checkpoint }
            $plan.Add([pscustomobject]@{ Index = $plan.Count + 1; Action = $actions[$i]; Checkpoint = $checkpoint })
        }
    }
    return , $plan
}

function Set-JourneyFile([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    for ($attempt = 0; ; $attempt++) {
        try { [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8); return }
        catch [System.IO.IOException] { if ($attempt -ge 20) { throw }; Start-Sleep -Milliseconds 100 }
    }
}

function Assert-OneMatch([string]$Content, [string]$Needle, $Action) {
    $count = ([regex]::Matches($Content, [regex]::Escape($Needle))).Count
    if ($count -ne 1) { throw "Action '$($Action.id)': expected one match, found $count." }
}

function Invoke-JourneyEdit {
    param([Parameter(Mandatory)][string]$AppDir, [Parameter(Mandatory)]$Action)
    $edit = $Action.edit
    $path = Join-Path $AppDir $Action.file
    $content = if (Test-Path -LiteralPath $path -PathType Leaf) { [System.IO.File]::ReadAllText($path) -replace "`r`n", "`n" } else { $null }
    switch ([string]$edit.op) {
        'create-file' { $text = ConvertTo-JourneyText $edit.text; if (-not $text.EndsWith("`n")) { $text += "`n" }; Set-JourneyFile $path $text }
        'delete-file' { Remove-Item -LiteralPath $path -Force }
        'rename-folder' { Rename-Item -LiteralPath $path -NewName ([string]$edit.to) }
        'write-base64' { $null = New-Item -ItemType Directory -Force -Path (Split-Path $path); [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String([string]$edit.base64)) }
        'insert-after' { $anchor = ConvertTo-JourneyText $edit.anchor; Assert-OneMatch $content $anchor $Action; Set-JourneyFile $path $content.Replace($anchor, $anchor + "`n" + (ConvertTo-JourneyText $edit.text)) }
        'insert-before' { $anchor = ConvertTo-JourneyText $edit.anchor; Assert-OneMatch $content $anchor $Action; Set-JourneyFile $path $content.Replace($anchor, (ConvertTo-JourneyText $edit.text) + "`n" + $anchor) }
        'replace' { $find = ConvertTo-JourneyText $edit.find; Assert-OneMatch $content $find $Action; Set-JourneyFile $path $content.Replace($find, (ConvertTo-JourneyText $edit.text)) }
        'delete' { $find = ConvertTo-JourneyText $edit.find; Assert-OneMatch $content $find $Action; Set-JourneyFile $path $content.Replace($find, '') }
        'move' {
            $block = ConvertTo-JourneyText $edit.block
            Assert-OneMatch $content $block $Action
            $content = [regex]::Replace($content.Replace($block + "`n", ''), "`n{3,}", "`n`n")
            $to = ConvertTo-JourneyText $edit.to
            Assert-OneMatch $content $to $Action
            $content = if ($edit.position -eq 'before') { $content.Replace($to, $block + "`n`n" + $to) } else { $content.Replace($to, $to + "`n`n" + $block) }
            Set-JourneyFile $path ([regex]::Replace($content, "`n{3,}", "`n`n"))
        }
        default { throw "Unknown edit op '$($edit.op)'." }
    }
}

Export-ModuleMember -Function Get-JourneyPlan, Invoke-JourneyEdit
