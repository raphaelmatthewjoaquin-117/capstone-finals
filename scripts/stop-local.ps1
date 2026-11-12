$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pidFile = Join-Path $root ".local-processes.json"
if (-not (Test-Path $pidFile)) {
    Write-Output "No local service process file found."
    exit 0
}
$processes = Get-Content $pidFile -Raw | ConvertFrom-Json
foreach ($item in $processes) {
    if (Get-Process -Id $item.Id -ErrorAction SilentlyContinue) {
        Stop-Process -Id $item.Id
        Write-Output "Stopped $($item.Name) (PID $($item.Id))."
    }
}
Remove-Item $pidFile
