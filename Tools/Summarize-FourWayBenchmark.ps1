param([Parameter(Mandatory=$true)][string]$ResultDirectory)
$culture = [Globalization.CultureInfo]::InvariantCulture
$successful = @(Get-Content -LiteralPath (Join-Path $ResultDirectory 'manifest.json') -Raw | ConvertFrom-Json | ForEach-Object { [IO.Path]::GetFileName($_.Log) })
$samples = foreach ($file in Get-ChildItem -LiteralPath $ResultDirectory -Filter '*.tsv' | Sort-Object Name) {
    if ($file.Name -notin $successful) { Write-Warning "Excluded unsuccessful process: $($file.Name)"; continue }
    $rows = Import-Csv -LiteralPath $file.FullName -Delimiter "`t" -Header Run,Pid,Version,Stage,Ms
    foreach ($group in $rows | Where-Object Run -ne 'startup' | Group-Object Run) {
        $items = $group.Group
        $scenario = ($items | Where-Object { $_.Stage -like 'scenario:*' } | Select-Object -First 1).Stage.Substring(9)
        $shown = [double]::Parse(($items | Where-Object Stage -eq 'shown' | Select-Object -First 1).Ms, $culture)
        $painted = [double]::Parse(($items | Where-Object Stage -eq 'form-painted' | Select-Object -First 1).Ms, $culture)
        $completed = ($items | Where-Object { $_.Stage -in @('form-painted','programs-painted','ui-callback-after-show') } |
          ForEach-Object { [double]::Parse($_.Ms,$culture) } | Measure-Object -Maximum).Maximum
        [pscustomobject]@{
          File=$file.Name; Version=$items[0].Version; Scenario=$scenario
          ShownMs=$shown; PaintedMs=$painted; CompleteMs=$completed
          Nodes=($items | Where-Object { $_.Stage -like 'programs-ready:*' } | Select-Object -First 1).Stage
          DPI=($items | Where-Object { $_.Stage -like 'dpi=*' } | Select-Object -First 1).Stage
        }
    }
}
$samples | Export-Csv -LiteralPath (Join-Path $ResultDirectory 'samples.csv') -NoTypeInformation -Encoding utf8
$summary = $samples | Group-Object Version,Scenario | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
      Version=$g[0].Version; Scenario=$g[0].Scenario; N=$g.Count
      ShownMs=[Math]::Round(($g.ShownMs | Measure-Object -Average).Average,1)
      PaintedMs=[Math]::Round(($g.PaintedMs | Measure-Object -Average).Average,1)
      CompleteMs=[Math]::Round(($g.CompleteMs | Measure-Object -Average).Average,1)
      MinPaintedMs=[Math]::Round(($g.PaintedMs | Measure-Object -Minimum).Minimum,1)
      MaxPaintedMs=[Math]::Round(($g.PaintedMs | Measure-Object -Maximum).Maximum,1)
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $ResultDirectory 'summary.csv') -NoTypeInformation -Encoding utf8
$summary | Format-Table -AutoSize
Write-Output 'Startup measurements (main-entry to services/plugins/optional preload ready):'
foreach ($file in Get-ChildItem -LiteralPath $ResultDirectory -Filter '*.tsv' | Sort-Object Name) {
    if ($file.Name -notin $successful) { continue }
    Import-Csv -LiteralPath $file.FullName -Delimiter "`t" -Header Run,Pid,Version,Stage,Ms |
      Where-Object Run -eq 'startup' | Select-Object Version,Ms,Stage
}
