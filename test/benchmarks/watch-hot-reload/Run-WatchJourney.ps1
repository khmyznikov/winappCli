# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
<#
.SYNOPSIS
  Runs the 40-save authored-edit Todo journey against a live 'winapp run --watch' session.
.DESCRIPTION
  Materializes the Todo app, starts 'winapp run --watch', applies each authored edit as one save, records what
  the watch reported for it and which process was running, and runs the journey's uiChecks against the live app
  at each checkpoint. The truth is: did every save land in the same process, and do the checks pass.
.EXAMPLE
  .\Run-WatchJourney.ps1 -Winapp ..\..\..\artifacts\cli\win-x64\winapp.exe -WinUIPackageSource <folder with a locally built Microsoft.WindowsAppSDK.WinUI .nupkg>
#>
[CmdletBinding()]
param(
    [string]$JourneyName = 'todo-app',
    # Defaults to the journey's primary variants.
    [string[]]$Variant,
    [string]$Winapp = (Join-Path $PSScriptRoot '..\..\..\artifacts\cli\win-x64\winapp.exe'),
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'winapp-watch-journey'),
    # A local feed with Microsoft.WindowsAppSDK.WinUI (a WinUI build with the hot reload service). Optional.
    [string]$WinUIPackageSource,
    [string]$WinUIPackageVersion = '3.0.0-dev',
    [ValidateSet('Unpackaged', 'Packaged')][string]$Packaging = 'Unpackaged',
    [int]$SaveTimeoutSeconds = 180,
    # Stop after this many saves (0 = all).
    [int]$MaxActions = 0,
    # Leave the app and the watch running after the last save (to inspect it).
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WatchJourney.psm1') -Force
$Winapp = (Resolve-Path $Winapp).Path
$journey = Get-Content (Join-Path $PSScriptRoot "journeys\$JourneyName.json") -Raw | ConvertFrom-Json
if (-not $Variant) { $Variant = @($journey.variants | Where-Object primary | ForEach-Object id) }
$runRoot = Join-Path $OutputRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function New-JourneyApp([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'app-template\*') $Directory
    $assets = Join-Path $PSScriptRoot "..\..\..\$($journey.app.assetsFrom)"
    if (Test-Path $assets) { Copy-Item $assets (Join-Path $Directory 'Assets') -Recurse }
    $properties = @()
    if ($Packaging -eq 'Unpackaged') { $properties += '<WindowsPackageType>None</WindowsPackageType>' }
    # The local WinUI package carries the runtime; self-contained keeps a third-party package's transitive
    # Microsoft.WindowsAppSDK from pulling in a different framework. It has to be set before the package targets
    # are evaluated (so here, not in .targets), and only for the app: class libraries reject it.
    if ($WinUIPackageSource) { $properties += "<WindowsAppSDKSelfContained Condition=`"'`$(MSBuildProjectFile)' == '$($journey.app.projectFile)'`">true</WindowsAppSDKSelfContained>" }
    # Next to the app folder rather than in it, so projects beside the app (a solution's libraries) share them.
    $shared = Split-Path $Directory
    if ($properties) {
        Set-Content (Join-Path $shared 'Directory.Build.props') "<Project><PropertyGroup>$($properties -join '')</PropertyGroup></Project>"
    }
    if ($WinUIPackageSource) {
        # Update, not Remove: a package added during the journey can bring Microsoft.WindowsAppSDK back transitively.
        Set-Content (Join-Path $shared 'Directory.Build.targets') @"
<Project>
  <ItemGroup>
    <PackageReference Update="Microsoft.WindowsAppSDK" ExcludeAssets="all" />
    <PackageReference Include="Microsoft.WindowsAppSDK.WinUI" Version="$WinUIPackageVersion" />
  </ItemGroup>
</Project>
"@
        Set-Content (Join-Path $shared 'nuget.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="winui-local" value="$WinUIPackageSource" />
  </packageSources>
</configuration>
"@
    }
}

function Invoke-Ui([string[]]$Arguments) {
    $output = & $Winapp ui @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Output = $output.Trim() }
}

function Get-ChildCount([string]$Target, [int]$ProcessId) {
    $result = Invoke-Ui @('inspect', $Target, '-a', "$ProcessId", '--depth', '4', '--json')
    if (-not $result.Ok) { return -1 }
    $root = @(($result.Output | ConvertFrom-Json).windows[0].elements)[0]
    $items = 0
    function Walk($node) {
        foreach ($child in @($node.children)) {
            if ($null -eq $child) { continue }
            if ($child.type -eq 'ListItem') { $script:items++ }
            Walk $child
        }
    }
    $script:items = 0
    Walk $root
    if ($script:items -gt 0) { return $script:items }
    return @($root.children).Where({ $null -ne $_ }).Count
}

$script:baselines = @{}

function Get-TextNumber([string]$Target, [int]$ProcessId) {
    $output = Invoke-Ui @('get-property', $Target, '-p', 'Name', '-a', "$ProcessId")
    if ($output.Ok -and $output.Output -match 'Name:\D*(\d+)') { return [int]$Matches[1] }
    return $null
}

# Values the step's checks compare against, read before its save.
function Read-Baselines($Checks, [int]$ProcessId) {
    $script:baselines = @{}
    foreach ($check in @($Checks | Where-Object { $_.assert -eq 'number-increased' })) {
        $script:baselines[[string]$check.target] = Get-TextNumber ([string]$check.target) $ProcessId
    }
}

function Test-UiCheck($Check, [int]$ProcessId) {
    $target = [string]$Check.target
    $value = [string]$Check.value
    $app = @('-a', "$ProcessId")
    switch ([string]$Check.assert) {
        'exists' {
            $result = Invoke-Ui (@('wait-for', $target) + $app + @('-t', '5000'))
            if ($result.Ok) { return $result }
            # Panels and frames have no UI Automation peer even in a normal build; look in the XAML tree instead.
            $output = & $Winapp devtools search $target --all --json @app 2>&1 | Out-String
            return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0 -and $output -match "`"$target`""); Output = "devtools: $($output.Trim())" }
        }
        'invoke' { return Invoke-Ui (@('invoke', $target) + $app) }
        'set-value' { return Invoke-Ui (@('set-value', $target, $value) + $app) }
        'text-equals' { return Invoke-Ui (@('wait-for', $target, '--value', $value) + $app + @('-t', '5000')) }
        'text-contains' { return Invoke-Ui (@('wait-for', $target, '--value', $value, '--contains') + $app + @('-t', '5000')) }
        'absent' { return Invoke-Ui (@('wait-for', $target, '--gone') + $app + @('-t', '5000')) }
        'toggle-state-equals' { return Invoke-Ui (@('wait-for', $target, '-p', 'ToggleState', '--value', $value) + $app + @('-t', '5000')) }
        'toggle-on' {
            $state = Invoke-Ui (@('get-property', $target, '-p', 'ToggleState') + $app)
            if ($state.Output -cnotmatch 'ToggleState: On') { $null = Invoke-Ui (@('invoke', $target) + $app) }
            return Invoke-Ui (@('wait-for', $target, '-p', 'ToggleState', '--value', 'On') + $app + @('-t', '5000'))
        }
        'layout-width-equals' {
            # The element's laid-out width in XAML units (an x:Name), e.g. an Image showing its file at natural size. Within
            # 1 unit: at a display scale other than 100% the layout snaps to device pixels (50 px at 125% lays out as 50.4).
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $output = & $Winapp devtools get-layout $target @app 2>&1 | Out-String
                if ($output -match 'actual:\s*([\d.]+)\s*x' -and [math]::Abs([double]$Matches[1] - [double]$value) -le 1) { return [pscustomobject]@{ Ok = $true; Output = $output.Trim() } }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "$($output.Trim()), expected width $value" }
        }
        'number-increased' {
            # A number in the element's text grew since before the save (read by Read-Baselines), e.g. an event counter whose
            # absolute value depends on how often the window resized at this display scale.
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $now = Get-TextNumber $target $ProcessId
                if ($null -ne $now -and $null -ne $script:baselines[$target] -and $now -gt $script:baselines[$target]) {
                    return [pscustomobject]@{ Ok = $true; Output = "$($script:baselines[$target]) -> $now" }
                }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "$($script:baselines[$target]) -> $now, expected an increase" }
        }
        'child-count-equals' {
            $deadline = (Get-Date).AddSeconds(5)
            do {
                $count = Get-ChildCount $target $ProcessId
                if ("$count" -eq $value) { return [pscustomobject]@{ Ok = $true; Output = "count $count" } }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            return [pscustomobject]@{ Ok = $false; Output = "count $count, expected $value" }
        }
        default { throw "No mapping for uiCheck assert '$($Check.assert)'." }
    }
}

# Only this run's app: other runs of the same app may be on the machine.
function Get-AppPid { (Get-Process TodoBench -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object StartTime -Descending | Select-Object -First 1).Id }

$summary = [System.Collections.Generic.List[object]]::new()
foreach ($variantId in $Variant) {
    $appDir = Join-Path $runRoot "$variantId\app"
    New-JourneyApp $appDir
    # Setup edits are part of the app the first build compiles.
    foreach ($setup in @($journey.setup)) { if ($setup) { Invoke-JourneyEdit -AppDir $appDir -Action $setup } }
    $log = Join-Path $runRoot "$variantId\watch.log"
    # A journey can pin the build configuration (for example Release, where XAML hot reload is not enabled).
    $runArguments = @('run', $appDir, '--watch') + $(if ($journey.app.configuration) { @('-c', $journey.app.configuration) } else { @() })
    $process = Start-Process -FilePath $Winapp -ArgumentList $runArguments -WorkingDirectory $appDir `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
    $script:consumed = 0
    function Read-NewLines {
        if (-not (Test-Path $log)) { return @() }
        $all = @(Get-Content $log)
        $new = @($all | Select-Object -Skip $script:consumed)
        $script:consumed = $all.Count
        return $new
    }
    function Wait-Line([string]$Pattern, [int]$TimeoutSeconds, [System.Collections.Generic.List[string]]$Seen) {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
            $found = $null
            foreach ($line in Read-NewLines) {
                $Seen.Add($line)
                if (-not $found -and $line -match $Pattern) { $found = $line }
            }
            if ($found) { return $found }
            Start-Sleep -Milliseconds 100
        }
        return $null
    }
    $seen = [System.Collections.Generic.List[string]]::new()
    $ready = Wait-Line '^\[watch\] (ready|error)' 600 $seen
    $appPid = Get-AppPid
    Write-Host "[$variantId] $ready"
    $launchPid = $appPid
    # What winapp must say once the app is up (for example why XAML hot reload is unavailable): checked with the first save.
    $launchChecks = @()
    if ($journey.app.launchExpect) {
        Start-Sleep -Milliseconds 1000
        foreach ($extra in Read-NewLines) { $seen.Add($extra) }
        $launchText = (($seen | Where-Object { $_ -match '^\[watch\]' }) -join "`n")
        $launchChecks += [pscustomobject]@{ Check = "launch $($journey.app.launchExpect)"; Ok = ($launchText -match $journey.app.launchExpect); Output = $launchText }
    }
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($step in (Get-JourneyPlan $journey $variantId)) {
        if ($MaxActions -gt 0 -and $step.Index -gt $MaxActions) { break }
        $seen = [System.Collections.Generic.List[string]]::new()
        $null = Read-NewLines
        if ($appPid) { Read-Baselines $step.Checkpoint.uiChecks ([int]$appPid) }
        Invoke-JourneyEdit -AppDir $appDir -Action $step.Action
        $line = Wait-Line '^\[watch\] (applied|waiting|error|restart|unchanged|ready)' $SaveTimeoutSeconds $seen
        $outcome = if ($line -match '^\[watch\] (\w+)') { $Matches[1] } else { 'timeout' }
        # After a failed rebuild or a crash, the save that fixes it rebuilds and relaunches: that's a restart.
        $relaunched = $outcome -eq 'ready'
        if ($relaunched) { $outcome = 'restart' }
        Start-Sleep -Milliseconds 300
        foreach ($extra in Read-NewLines) { $seen.Add($extra) }
        $timed = @($seen | Where-Object { $_ -match '\((\d+) ms\)\s*$' } | Select-Object -First 1)
        $ms = if ($timed -and $timed[0] -match '\((\d+) ms\)\s*$') { [int]$Matches[1] } else { $null }
        if ($outcome -eq 'restart') {
            if (-not $relaunched) { $null = Wait-Line '^\[watch\] ready' 600 $seen }
            $appPid = Get-AppPid
        }
        $checks = @($launchChecks)
        $launchChecks = @()
        if ($step.Checkpoint.uiChecks) {
            Start-Sleep -Milliseconds 700
            foreach ($check in $step.Checkpoint.uiChecks) {
                $result = Test-UiCheck $check ([int]$appPid)
                $checks += [pscustomobject]@{ Check = "$($check.assert) $($check.target) $($check.value)".Trim(); Ok = $result.Ok; Output = $result.Output }
            }
        }
        $detail = (($seen | Where-Object { $_ -match '^\[watch\]|^  ' }) -join "`n")
        # What the status line itself must say (an error the compiler would report, a note), checked like the UI.
        if ($step.Action.expect.outcome) {
            $checks += [pscustomobject]@{ Check = "outcome $($step.Action.expect.outcome)"; Ok = ($outcome -eq $step.Action.expect.outcome); Output = $detail }
        }
        if ($step.Action.expect.detail) {
            $checks += [pscustomobject]@{ Check = "detail $($step.Action.expect.detail)"; Ok = ($detail -match $step.Action.expect.detail); Output = $detail }
        }
        $row = [pscustomobject]@{
            Index = $step.Index; Action = $step.Action.id; Outcome = $outcome; Ms = $ms; Pid = $appPid
            SameProcess = ($appPid -eq $launchPid); Checkpoint = $step.Checkpoint.id
            Checks = $checks; Detail = $detail
        }
        $rows.Add($row)
        $checkText = if ($checks) { ' checks ' + (($checks | ForEach-Object { if ($_.Ok) { 'ok' } else { "FAIL($($_.Check))" } }) -join ',') } else { '' }
        Write-Host ("[{0}] {1,2} {2,-30} {3,-9} {4,6} ms pid {5}{6}" -f $variantId, $step.Index, $step.Action.id, $outcome, $ms, $appPid, $checkText)
    }
    if (-not $KeepRunning -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if (-not $KeepRunning -and $appPid) { Stop-Process -Id ([int]$appPid) -Force -ErrorAction SilentlyContinue }
    $rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $runRoot "$variantId\results.json")
    $summary.Add([pscustomobject]@{
        Variant = $variantId; Saves = $rows.Count
        Live = @($rows | Where-Object { $_.Outcome -ne 'restart' -and $_.Outcome -ne 'timeout' }).Count
        Restarts = @($rows | Where-Object Outcome -eq 'restart').Count
        Errors = @($rows | Where-Object Outcome -eq 'error').Count
        ChecksPassed = @($rows.Checks | Where-Object Ok).Count; Checks = @($rows.Checks).Count
        SameProcess = -not ($rows | Where-Object { -not $_.SameProcess })
    })
}

$summary | Format-Table -AutoSize | Out-String | Write-Host
$summary | ConvertTo-Json | Set-Content (Join-Path $runRoot 'summary.json')
Write-Host "Results: $runRoot"
