$Host.UI.RawUI.WindowTitle = "Strategy 2 — Volume Breakout [VOL]"
Set-Location $PSScriptRoot
$date = Get-Date -Format 'yyyyMMdd'
$file = "logs\strategy2-volume-$date.log"

Write-Host "=== Strategy 2 — Volume Breakout ===" -ForegroundColor Green
while (-not (Test-Path $file)) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Waiting for log file..." -ForegroundColor Yellow
    Start-Sleep 3
}
Write-Host "Watching: $file" -ForegroundColor Green
Get-Content $file -Wait -Tail 40
