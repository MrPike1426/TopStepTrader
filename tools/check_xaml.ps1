$file = "E:\VisStudioProjects\TopStepTrader\src\TopStepTrader.UI\Views\SuperTrendPlusView.xaml"
$text = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
$b = [System.Text.Encoding]::UTF8.GetBytes($text)
$nonAscii = @($b | Where-Object { $_ -gt 127 })
Write-Host "Non-ASCII bytes remaining: $($nonAscii.Count)"

# Find any remaining garbled attribute values (non-ASCII inside Text= or Value= attributes)
$matches = [regex]::Matches($text, '(?:Text|Value)="([^"]*)"')
foreach ($m in $matches) {
    $val = $m.Groups[1].Value
    $vb = [System.Text.Encoding]::UTF8.GetBytes($val)
    if ($vb | Where-Object { $_ -gt 127 }) {
        Write-Host "STILL GARBLED at char $($m.Index): $($m.Value.Substring(0,[Math]::Min(50,$m.Value.Length)))"
    }
}
Write-Host "Done."
