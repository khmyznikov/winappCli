# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

param(
    [string]$WinappPath,
    [string]$Python = 'python',
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = -not $IsWindows -or $null -eq (Get-Command $Python -ErrorAction SilentlyContinue)
}

Describe 'Python WinUI counter' {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:tempDir = $null
        $script:runs = @()
        $script:save = $null
        $script:identityName = $null
        $script:sampleDir = $PSScriptRoot
        if ($script:skip) { return }

        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $script:platform = (& $Python -I -c 'import sysconfig; print(sysconfig.get_platform().replace("-", "_"))').Trim()
        if ($LASTEXITCODE -ne 0 -or $platform -notin @('win_amd64', 'win_arm64')) {
            throw 'The sample tests require Windows x64 or ARM64 CPython.'
        }
        $script:architecture = if ($platform -eq 'win_arm64') { 'arm64' } else { 'x64' }
        $packagePath = if ($WinappPath) { $WinappPath } else { Join-Path $PSScriptRoot '..\..\artifacts\python' }
        $packagePath = (Resolve-Path -LiteralPath $packagePath).Path
        if ((Test-Path -LiteralPath $packagePath -PathType Container) -and
            (Test-Path -LiteralPath (Join-Path $packagePath 'python') -PathType Container)) {
            $packagePath = Join-Path $packagePath 'python'
        }
        $wheels = if (Test-Path -LiteralPath $packagePath -PathType Container) {
            @(Get-ChildItem -LiteralPath $packagePath -Filter "winapp_cli-*-py3-none-$platform.whl" -File)
        } else {
            @(Get-Item -LiteralPath $packagePath | Where-Object Name -Like "winapp_cli-*-py3-none-$platform.whl")
        }
        if ($wheels.Count -ne 1) {
            throw "Expected one winapp-cli $platform wheel. Pass -WinappPath pointing to its file or directory."
        }

        function New-SampleProcess {
            param([string]$Executable, [string[]]$Arguments, [string]$Directory)
            $info = [Diagnostics.ProcessStartInfo]::new($Executable)
            $info.UseShellExecute = $false
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
            $info.WorkingDirectory = $Directory
            $info.Environment.Remove('WINAPP_CLI_PATH') | Out-Null
            foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($info)
            [pscustomobject]@{
                Process = $process
                Output = $process.StandardOutput.ReadToEndAsync()
                Errors = $process.StandardError.ReadToEndAsync()
                Window = $null
            }
        }

        function Invoke-SampleCommand {
            param([string]$Executable, [string[]]$Arguments, [string]$Directory = $script:tempDir)
            $run = New-SampleProcess $Executable $Arguments $Directory
            try {
                Wait-ProcessExitOrFail -Process $run.Process -Timeout ([TimeSpan]::FromMinutes(15)) `
                    -Description "$Executable $($Arguments -join ' ')"
                $output = $run.Output.GetAwaiter().GetResult()
                $errors = $run.Errors.GetAwaiter().GetResult()
                if ($run.Process.ExitCode -ne 0) {
                    throw "Command failed ($($run.Process.ExitCode)): $Executable $($Arguments -join ' ')`n$output`n$errors"
                }
                Write-Verbose $output
                return $output
            } finally {
                $run.Process.Dispose()
            }
        }

        function Get-CounterControl {
            param($Run, [string]$Id)
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
            $element = $Run.Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if (-not $element) { throw "WinUI control missing: $Id" }
            return $element
        }

        function Assert-CounterText {
            param($Run, [string]$Id, [string]$Expected)
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $actual = (Get-CounterControl $Run $Id).Current.Name
                if ($actual -eq $Expected) { return }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            $actual | Should -Be $Expected -Because "WinUI control '$Id' should update within ten seconds"
        }

        function Start-Counter {
            param([string]$Directory, [switch]$Identity)
            if ($Identity) {
                $run = New-SampleProcess $script:cli @('run', '--identity', '--project', $Directory, 'app.py') $Directory
            } else {
                $run = New-SampleProcess $script:cli @('run', '--project', $Directory, 'app.py') $Directory
            }
            $script:runs += $run
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty, 'Python WinUI counter')
            $deadline = [DateTime]::UtcNow.AddMinutes(5)
            do {
                if ($run.Process.HasExited) {
                    throw "Counter exited before opening WinUI.`n$($run.Output.GetAwaiter().GetResult())`n$($run.Errors.GetAwaiter().GetResult())"
                }
                $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                    [Windows.Automation.TreeScope]::Children, $condition)
                if ($windows.Count) {
                    $tree = Get-ProcessTree -RootProcessId $run.Process.Id
                    $ids = @($tree | ForEach-Object { $_.Process.ProcessId })
                    foreach ($window in $windows) {
                        if ($window.Current.ProcessId -in $ids) {
                            $run.Window = $window
                            $id = [Windows.Automation.PropertyCondition]::new(
                                [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Increment')
                            if ($window.FindFirst([Windows.Automation.TreeScope]::Descendants, $id)) { return $run }
                        }
                    }
                }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            throw 'Counter did not create its WinUI controls within five minutes.'
        }

        function Add-One {
            param($Run)
            (Get-CounterControl $Run 'Increment').GetCurrentPattern(
                [Windows.Automation.InvokePattern]::Pattern).Invoke()
        }

        function Subtract-One {
            param($Run)
            (Get-CounterControl $Run 'Decrement').GetCurrentPattern(
                [Windows.Automation.InvokePattern]::Pattern).Invoke()
        }

        function Get-CounterBounds {
            param($Run)
            Start-Sleep -Milliseconds 150
            $bounds = @{}
            foreach ($id in @('Increment', 'Count', 'Decrement')) {
                $bounds[$id] = (Get-CounterControl $Run $id).Current.BoundingRectangle
                $bounds[$id].Width | Should -BeGreaterThan 0
                $bounds[$id].Height | Should -BeGreaterThan 0
            }
            $bounds['Increment'].Right | Should -BeLessThan $bounds['Count'].Left
            $bounds['Count'].Right | Should -BeLessThan $bounds['Decrement'].Left
            return $bounds
        }

        function Assert-CounterBoundsUnchanged {
            param($Run, [hashtable]$Expected)
            $actual = Get-CounterBounds $Run
            foreach ($id in @('Increment', 'Count', 'Decrement')) {
                foreach ($property in @('Left', 'Top', 'Width', 'Height')) {
                    [Math]::Abs($actual[$id].$property - $Expected[$id].$property) |
                        Should -BeLessOrEqual 0.5 -Because "digit changes must not move or resize '$id'"
                }
            }
        }

        function Close-Counter {
            param($Run)
            $Run.Window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
            Wait-ProcessExitOrFail -Process $Run.Process -Timeout ([TimeSpan]::FromSeconds(15)) `
                -Description 'Python counter after closing WinUI'
            $Run.Process.ExitCode | Should -Be 0 -Because $Run.Errors.GetAwaiter().GetResult()
        }

        $script:tempDir = New-TempTestDirectory -Prefix 'python-counter'
        $script:appDir = Join-Path $tempDir 'project with spaces'
        $script:cloneDir = Join-Path $tempDir 'restored copy'
        $null = New-Item -ItemType Directory -Path $appDir, $cloneDir
        $sourceFiles = @('app.py', 'runtime.py', 'counter.py', 'window.py', 'pyproject.toml')
        foreach ($directory in @($appDir, $cloneDir)) {
            foreach ($name in $sourceFiles) {
                Copy-Item -LiteralPath (Join-Path $sampleDir $name) -Destination $directory
            }
        }
        $tool = Join-Path $tempDir 'tool'
        Invoke-SampleCommand $Python @('-I', '-m', 'venv', $tool) | Out-Null
        $toolPython = Join-Path $tool 'Scripts\python.exe'
        Invoke-SampleCommand $toolPython @('-I', '-m', 'pip', 'install', '--disable-pip-version-check',
            '--no-input', $wheels[0].FullName) | Out-Null
        $script:cli = Join-Path $tool 'Scripts\winapp-py.exe'
        $script:identityName = "Winapp.PythonCounter.$([guid]::NewGuid().ToString('N').Substring(0, 16)).debug"
    }

    AfterAll {
        if (-not $script:tempDir) { return }
        foreach ($run in $script:runs) {
            if (-not $run.Process.HasExited) {
                $tree = Get-ProcessTree -RootProcessId $run.Process.Id
                foreach ($node in ($tree | Sort-Object Depth -Descending)) {
                    $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($node.Process.ProcessId)"
                    if ($current -and $current.CreationDate -eq $node.Process.CreationDate) {
                        Stop-Process -Id $node.Process.ProcessId -Force
                    }
                }
                $run.Process.WaitForExit(10000) | Out-Null
            }
            $run.Process.Dispose()
        }
        $record = Join-Path $script:appDir '.winapp\python-identity.json'
        if (Test-Path -LiteralPath $record) {
            Invoke-SampleCommand $script:cli @('identity', 'clear', '--project', $script:appDir) | Out-Null
        }
        if ($script:identityName -and
            @(Get-AppxPackage -Name $script:identityName | Where-Object Name -EQ $script:identityName).Count) {
            throw "Counter identity remains registered. Test files are preserved at $script:tempDir."
        }
        if ($script:save -and (Test-Path -LiteralPath $script:save)) {
            Remove-Item -LiteralPath $script:save -Force
        }
        if ($SkipCleanup) {
            Write-Host "Test files preserved: $script:tempDir"
        } else {
            Remove-TempTestDirectory -Path $script:tempDir
        }
    }

    Context 'Phase 1: From-scratch guide workflow' {
        It 'Initializes SDKs, declared dependencies, manifest and selected bindings' -Skip:$script:skip {
            Invoke-SampleCommand $script:cli @('init', '--use-defaults', '--add-python-bindings') $script:appDir |
                Should -Match 'Python bindings ready'
            foreach ($name in @('winapp.yaml', 'Package.appxmanifest', 'Assets',
                '.venv\Scripts\python.exe', '.winapp\python\winapp_bindings\pyproject.toml')) {
                Join-Path $script:appDir $name | Should -Exist
            }
            $script:appPython = Join-Path $script:appDir '.venv\Scripts\python.exe'
            Invoke-SampleCommand $script:appPython @('-I', '-c',
                'import tomllib; from pathlib import Path; from winapp_bindings.windows.storage import ApplicationData; d=tomllib.loads(Path("pyproject.toml").read_text()); assert d["tool"]["winapp"]["python-bindings"]["scope"]=="types"; assert "Windows.Storage.ApplicationData" in d["tool"]["winapp"]["python-bindings"]["types"]') `
                $script:appDir | Out-Null
            [xml]$manifest = Get-Content -LiteralPath (Join-Path $script:appDir 'Package.appxmanifest') -Raw
            $manifest.Package.Identity.Name = $script:identityName.Substring(0, $script:identityName.Length - 6)
            $manifest.Save((Join-Path $script:appDir 'Package.appxmanifest'))
        }

        It 'Uses the restored Bootstrap DLL and forgets ordinary Python counts after closing' -Skip:$script:skip {
            $run = Start-Counter $script:appDir
            Assert-CounterText $run 'Mode' 'No identity - memory only'
            Assert-CounterText $run 'Count' '0'
            (Get-CounterControl $run 'Decrement').Current.IsEnabled | Should -BeFalse
            $bounds = Get-CounterBounds $run
            Add-One $run
            Assert-CounterText $run 'Count' '1'
            (Get-CounterControl $run 'Decrement').Current.IsEnabled | Should -BeTrue
            Subtract-One $run
            Assert-CounterText $run 'Count' '0'
            (Get-CounterControl $run 'Decrement').Current.IsEnabled | Should -BeFalse
            foreach ($step in 1..9) { Add-One $run }
            Assert-CounterText $run 'Count' '9'
            Assert-CounterBoundsUnchanged $run $bounds
            Add-One $run
            Assert-CounterText $run 'Count' '10'
            Assert-CounterBoundsUnchanged $run $bounds
            Subtract-One $run
            Assert-CounterText $run 'Count' '9'
            Assert-CounterBoundsUnchanged $run $bounds
            $modules = @(Get-Process -Id $run.Window.Current.ProcessId | Select-Object -ExpandProperty Modules)
            $bootstrap = @($modules | Where-Object ModuleName -EQ 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
            $bootstrap.Count | Should -Be 1
            $bootstrap[0].FileName | Should -Be (
                Join-Path $script:appDir ".winapp\bin\$script:architecture\Microsoft.WindowsAppRuntime.Bootstrap.dll")
            Close-Counter $run
            Join-Path $script:appDir 'python-counter.json' | Should -Not -Exist
            $again = Start-Counter $script:appDir
            Assert-CounterText $again 'Count' '0'
            Close-Counter $again
        }

        It 'Runs private identity Python without bootstrap and preserves saved counts across runs' -Skip:$script:skip {
            $bin = Join-Path $script:appDir '.winapp\bin'
            $withheld = Join-Path $script:appDir '.winapp\bin-withheld'
            Move-Item -LiteralPath $bin -Destination $withheld
            try {
                $run = Start-Counter $script:appDir -Identity
                Assert-CounterText $run 'Mode' 'Package identity - saved counter'
                Assert-CounterText $run 'Count' '0'
                (Get-CounterControl $run 'Decrement').Current.IsEnabled | Should -BeFalse
                $modules = @(Get-Process -Id $run.Window.Current.ProcessId | Select-Object -ExpandProperty Modules)
                @($modules | Where-Object ModuleName -EQ 'Microsoft.WindowsAppRuntime.Bootstrap.dll').Count |
                    Should -Be 0
                @($modules | Where-Object ModuleName -EQ 'Microsoft.UI.Xaml.dll').Count | Should -Be 1
                $package = @(Get-AppxPackage -Name $script:identityName | Where-Object Name -EQ $script:identityName)
                $package.Count | Should -Be 1
                $script:save = Join-Path $env:LOCALAPPDATA "Packages\$($package[0].PackageFamilyName)\LocalState\python-counter.json"
                $script:save | Should -Not -Exist
                Add-One $run
                Assert-CounterText $run 'Count' '1'
                Add-One $run
                Assert-CounterText $run 'Count' '2'
                Close-Counter $run
                (Get-Content -LiteralPath $script:save -Raw | ConvertFrom-Json).count | Should -Be 2
                $again = Start-Counter $script:appDir -Identity
                Assert-CounterText $again 'Count' '2'
                $attributes = (Get-Item -LiteralPath $script:save).Attributes
                try {
                    (Get-Item -LiteralPath $script:save).Attributes = $attributes -bor [IO.FileAttributes]::ReadOnly
                    Add-One $again
                    Assert-CounterText $again 'Status' 'Save failed; count unchanged. Check the terminal and try again.'
                    Assert-CounterText $again 'Count' '2'
                    (Get-Content -LiteralPath $script:save -Raw | ConvertFrom-Json).count | Should -Be 2
                    Subtract-One $again
                    Assert-CounterText $again 'Status' 'Save failed; count unchanged. Check the terminal and try again.'
                    Assert-CounterText $again 'Count' '2'
                    (Get-Content -LiteralPath $script:save -Raw | ConvertFrom-Json).count | Should -Be 2
                } finally {
                    (Get-Item -LiteralPath $script:save).Attributes = $attributes
                }
                Add-One $again
                Assert-CounterText $again 'Count' '3'
                Subtract-One $again
                Assert-CounterText $again 'Count' '2'
                Close-Counter $again
                (Get-Content -LiteralPath $script:save -Raw | ConvertFrom-Json).count | Should -Be 2

                [IO.File]::WriteAllText($script:save, '{"schema":1,"count":999999999}')
                $large = Start-Counter $script:appDir -Identity
                Assert-CounterText $large 'Count' '999999999'
                $largeBounds = Get-CounterBounds $large
                Add-One $large
                Assert-CounterText $large 'Count' '1000000000'
                Assert-CounterBoundsUnchanged $large $largeBounds
                $number = Get-CounterControl $large 'Count'
                $text = $number.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange
                $text.GetText(-1).Trim() | Should -Be '1000000000'
                $textBounds = @($text.GetBoundingRectangles())
                $textBounds.Count | Should -Be 1 -Because 'ten digits must fit on one line'
                $textBounds[0].Width | Should -BeGreaterThan 0
                $textBounds[0].Left | Should -BeGreaterOrEqual ($largeBounds['Count'].Left - 0.5)
                $textBounds[0].Right | Should -BeLessOrEqual ($largeBounds['Count'].Right + 0.5)
                Subtract-One $large
                Assert-CounterText $large 'Count' '999999999'
                Assert-CounterBoundsUnchanged $large $largeBounds
                Close-Counter $large
                (Get-Content -LiteralPath $script:save -Raw | ConvertFrom-Json).count | Should -Be 999999999

                $before = (Get-FileHash -LiteralPath $script:save).Hash
                Invoke-SampleCommand $script:cli @('identity', 'clear', '--project', $script:appDir) | Out-Null
                @((Get-AppxPackage -Name $script:identityName | Where-Object Name -EQ $script:identityName)).Count |
                    Should -Be 0
                (Get-FileHash -LiteralPath $script:save).Hash | Should -Be $before
                Join-Path $script:appDir '.venv\Scripts\python.exe' | Should -Exist
                Join-Path $script:appDir '.winapp\python-identity' | Should -Not -Exist
            } finally {
                Move-Item -LiteralPath $withheld -Destination $bin
            }
        }
    }

    Context 'Phase 2: Existing sample source and fresh-clone restore' {
        It 'Restores a clean copy without repeating initialization or changing SDK pins' -Skip:$script:skip {
            Copy-Item -LiteralPath (Join-Path $script:appDir 'winapp.yaml') -Destination $script:cloneDir
            $pins = Get-Content -LiteralPath (Join-Path $script:cloneDir 'winapp.yaml') -Raw
            Invoke-SampleCommand $script:cli @('restore', '--project', $script:cloneDir) | Out-Null
            (Get-Content -LiteralPath (Join-Path $script:cloneDir 'winapp.yaml') -Raw) | Should -Be $pins
            $run = Start-Counter $script:cloneDir
            Assert-CounterText $run 'Count' '0'
            Add-One $run
            Assert-CounterText $run 'Count' '1'
            Close-Counter $run
        }

        It 'Checks the existing source and counter helpers without changing the sample' -Skip:$script:skip {
            Invoke-SampleCommand $Python @('-I', '-B', '-m', 'unittest', 'discover', '-s',
                $script:sampleDir, '-p', 'test_app.py') | Out-Null
            Invoke-SampleCommand $Python @('-I', '-c',
                'import ast,sys; from pathlib import Path; p=Path(sys.argv[1]); [ast.parse(f.read_text()) for f in p.glob("*.py")]; assert not list(p.glob("*.xaml"))',
                $script:sampleDir) | Out-Null
        }
    }
}
