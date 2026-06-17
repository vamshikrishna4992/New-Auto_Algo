$Host.UI.RawUI.WindowTitle = "Strategy 3 — Premium Stop Hunt [PSH]"
Set-Location $PSScriptRoot
$date = Get-Date -Format 'yyyyMMdd'
$file = "logs\strategy3-premium-$date.log"

Write-Host "=== Strategy 3 — Premium Stop Hunt ===" -ForegroundColor Magenta
while (-not (Test-Path $file)) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Waiting for log file..." -ForegroundColor Yellow
    Start-Sleep 3
}
Write-Host "Watching: $file" -ForegroundColor Magenta
Get-Content $file -Wait -Tail 40
