param([string]$Log)
# S2: lines of generated C# changed per save (MainPage.g.cs + MainPage.g.i.cs), normalized: no BOM, no '#pragma checksum'
# line (it always changes) and the spike's simulated "= false" change applied to both sides.
$tmp = Join-Path $env:TEMP "s2-$PID"; New-Item -ItemType Directory $tmp -Force | Out-Null
function Norm($src, $dst) {
    $text = [IO.File]::ReadAllText($src).TrimStart([char]0xFEFF)
    $text = [regex]::Replace($text, '(private static bool isobj\w+Disabled) = false;', '$1;')
    $lines = $text -split "`r?`n" | Where-Object { $_ -notmatch '^#pragma checksum' }
    [IO.File]::WriteAllLines($dst, $lines)
}
Get-ChildItem $Log -Directory | Sort-Object Name | ForEach-Object {
    $row = [ordered]@{ Save = $_.Name }
    foreach ($f in 'MainPage.g.cs', 'MainPage.g.i.cs') {
        $b = Join-Path $_.FullName "before\$f"; $a = Join-Path $_.FullName "after\$f"
        if ((Test-Path $b) -and (Test-Path $a)) {
            Norm $b "$tmp\b.cs"; Norm $a "$tmp\a.cs"
            $n = (git --no-pager diff --no-index --numstat "$tmp\b.cs" "$tmp\a.cs") -split '\s+'
            $row[$f] = if ($n.Count -ge 2) { "+$($n[0]) -$($n[1])" } else { '0' }
        } else { $row[$f] = 'n/a' }
    }
    $result = Get-Content (Join-Path $_.FullName 'result.txt') -ErrorAction SilentlyContinue
    $row.EnC = (($result | Select-String 'edit and continue: status (\w+)').Matches.Groups[1].Value) + $(if ($result -match 'build failed') { 'BuildFailed' })
    $row.Rude = @($result | Select-String '  rude edit').Count
    $row.RudeKinds = (@($result | Select-String '(ENC\d+)(?=:)' -AllMatches | Where-Object { $_.Line -match 'rude edit' } | ForEach-Object { $_.Matches.Value }) | Group-Object | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '
    [pscustomobject]$row
}
Remove-Item $tmp -Recurse -Force
