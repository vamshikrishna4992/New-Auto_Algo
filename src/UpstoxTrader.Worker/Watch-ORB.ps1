$Host.UI.RawUI.WindowTitle = "Strategy 1 — ORB Breakout"
Set-Location $PSScriptRoot
$date = Get-Date -Format 'yyyyMMdd'
$file = "logs\strategy1-orb-$date.log"

Write-Host "=== Strategy 1 — ORB Breakout ===" -ForegroundColor Cyan
while (-not (Test-Path $file)) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Waiting for log file..." -ForegroundColor Yellow
    Start-Sleep 3
}
Write-Host "Watching: $file" -ForegroundColor Cyan
Get-Content $file -Wait -Tail 40
