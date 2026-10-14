#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for scripts/check-em-dash.ps1: a PR may not add an em dash, written directly or as an
    escape, but text that was already there is left alone.
#>

BeforeAll {
    $script:Check = Join-Path (Split-Path $PSScriptRoot -Parent) 'check-em-dash.ps1'
    $script:Dash = [string][char]0x2014

    # A repo whose base commit already has an em dash, so only added lines can fail the check.
    function New-Repo {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('emdash-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root | Out-Null
        Push-Location $root
        try {
            git init -q .
            git config user.email 'test@example.com'
            git config user.name 'test'
            git config core.autocrlf false
            [IO.File]::WriteAllText((Join-Path $root 'old.md'), "Existing $script:Dash text`n")
            git add -A
            git commit -qm base
        }
        finally { Pop-Location }
        $root
    }

    function Invoke-Check([string]$Root) {
        Push-Location $Root
        try {
            $output = & pwsh -NoProfile -File $script:Check -Base HEAD 2>&1 | Out-String
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
        }
        finally { Pop-Location }
    }
}

Describe 'check-em-dash' {
    BeforeEach { $script:Root = New-Repo }
    AfterEach { Remove-Item -LiteralPath $script:Root -Recurse -Force -ErrorAction SilentlyContinue }

    It 'passes when added lines have no em dash, even though the base has one' {
        [IO.File]::WriteAllText((Join-Path $script:Root 'new.md'), "Clean, with a comma.`n")
        Add-Content (Join-Path $script:Root 'old.md') 'Another clean line.'
        git -C $script:Root add -A

        $result = Invoke-Check $script:Root

        $result.ExitCode | Should -Be 0
    }

    It 'fails on an added em dash and names the file and line' {
        [IO.File]::WriteAllText((Join-Path $script:Root 'new.md'), "First line`nSecond $script:Dash line`n")
        git -C $script:Root add -A

        $result = Invoke-Check $script:Root

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match ([regex]::Escape('new.md:2:'))
    }

    It 'fails on an escape that renders as an em dash: <Escape>' -ForEach @(
        @{ Escape = ('\' + 'u2014') }
        @{ Escape = ('&#x' + '2014;') }
        @{ Escape = ('&#' + '8212;') }
        @{ Escape = ('&' + 'mdash;') }
    ) {
        [IO.File]::WriteAllText((Join-Path $script:Root 'new.xaml'), "<TextBlock Text=`"a $Escape b`"/>`n")
        git -C $script:Root add -A

        $result = Invoke-Check $script:Root

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match ([regex]::Escape('new.xaml:1:'))
    }
}
