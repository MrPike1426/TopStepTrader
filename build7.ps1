$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$root = 'E:\TopStep\TopStepTrader'

Write-Host "--- Restoring packages for UI project ---"
& $dotnet restore "$root\src\TopStepTrader.UI\TopStepTrader.UI.vbproj"

Write-Host "`n--- Building UI project (+ all dependencies) ---"
& $dotnet build "$root\src\TopStepTrader.UI\TopStepTrader.UI.vbproj" --configuration Debug

Write-Host "`nDone."
