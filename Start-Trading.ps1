$workerDir = Join-Path $PSScriptRoot "src\UpstoxTrader.Worker"

# Open log monitor windows for both strategies
Start-Process powershell -ArgumentList "-NoExit", "-File", "$workerDir\Watch-ORB.ps1"
Start-Process powershell -ArgumentList "-NoExit", "-File", "$workerDir\Watch-Volume.ps1"

# Start the trading app in this window
Set-Location $workerDir
dotnet run
