$workerDir = Join-Path $PSScriptRoot "src\UpstoxTrader.Worker"

Start-Process powershell -ArgumentList "-NoExit", "-File", "$workerDir\Watch-ORB.ps1"
Start-Process powershell -ArgumentList "-NoExit", "-File", "$workerDir\Watch-Volume.ps1"

Write-Host "Opened log monitor windows for both strategies." -ForegroundColor Green
