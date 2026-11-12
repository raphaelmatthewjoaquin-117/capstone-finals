$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pidFile = Join-Path $root ".local-processes.json"

if (Test-Path $pidFile) {
    $existing = Get-Content $pidFile -Raw | ConvertFrom-Json
    $running = @($existing | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
    if ($running.Count -gt 0) {
        throw "Local services already appear to be running. Use scripts\stop-local.ps1 first."
    }
    Remove-Item $pidFile
}

dotnet build (Join-Path $root "StorePlatform.slnx")
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$data = Join-Path $root "data"
$logs = Join-Path $root "logs"
New-Item -ItemType Directory -Force -Path $data, $logs | Out-Null
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:JWT_SIGNING_KEY = if ($env:JWT_SIGNING_KEY) { $env:JWT_SIGNING_KEY } else { "local-development-signing-key-change-me-0123456789" }
$env:JWT_ISSUER = if ($env:JWT_ISSUER) { $env:JWT_ISSUER } else { "store-platform" }
$env:JWT_AUDIENCE = if ($env:JWT_AUDIENCE) { $env:JWT_AUDIENCE } else { "storefront" }
$env:Messaging__Enabled = "false"
$env:CATALOG_DB = Join-Path $data "catalog.db"
$env:INVENTORY_DB = Join-Path $data "inventory.db"
$env:ORDERS_DB = Join-Path $data "orders.db"
$env:PAYMENTS_DB = Join-Path $data "payments.db"
$env:IDENTITY_DB = Join-Path $data "identity.db"
$env:CATALOG_URL = "http://localhost:5101"
$env:INVENTORY_URL = "http://localhost:5102"
$env:PAYMENTS_URL = "http://localhost:5104"

$apps = @(
    @{ Name = "Catalog"; Project = "Catalog.Api"; Port = 5101; Health = "/health" },
    @{ Name = "Inventory"; Project = "Inventory.Api"; Port = 5102; Health = "/health" },
    @{ Name = "Payments"; Project = "Payments.Api"; Port = 5104; Health = "/health" },
    @{ Name = "Identity"; Project = "Identity.Api"; Port = 5105; Health = "/health" },
    @{ Name = "Orders"; Project = "Orders.Api"; Port = 5103; Health = "/health" },
    @{ Name = "Gateway"; Project = "Gateway"; Port = 5100; Health = "/catalog/v1/products" },
    @{ Name = "Storefront"; Project = "Storefront"; Port = 5200; Health = "/" }
)
$started = @()
try {
    foreach ($app in $apps) {
        $dll = Join-Path $root ("src\{0}\bin\Debug\net11.0\{0}.dll" -f $app.Project)
        $stdout = Join-Path $logs ($app.Project + ".log")
        $stderr = Join-Path $logs ($app.Project + ".err.log")
        $process = Start-Process -FilePath "dotnet" `
            -ArgumentList "`"$dll`" --urls http://localhost:$($app.Port)" `
            -WorkingDirectory (Join-Path $root ("src\" + $app.Project)) -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $started += [pscustomobject]@{ Name = $app.Name; Id = $process.Id; Port = $app.Port }
    }
    $started | ConvertTo-Json | Set-Content -Path $pidFile

    foreach ($app in $apps) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 45; $attempt++) {
            $process = $started | Where-Object { $_.Name -eq $app.Name }
            if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
                $log = Get-Content (Join-Path $logs ($app.Project + ".err.log")) -Raw
                throw "$($app.Name) exited before becoming ready.`n$log"
            }
            try {
                Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$($app.Port)$($app.Health)" -TimeoutSec 2 | Out-Null
                $ready = $true
                break
            } catch {
                Start-Sleep -Seconds 1
            }
        }
        if (-not $ready) { throw "$($app.Name) did not become ready; inspect logs\$($app.Project).log." }
        Write-Output "$($app.Name) ready at http://localhost:$($app.Port)"
    }
    Write-Output "Storefront: http://localhost:5200"
    Write-Output "Gateway:    http://localhost:5100"
    Write-Output "Stop with:  .\scripts\stop-local.ps1"
} catch {
    foreach ($item in $started) {
        if (Get-Process -Id $item.Id -ErrorAction SilentlyContinue) {
            Stop-Process -Id $item.Id
        }
    }
    if (Test-Path $pidFile) { Remove-Item $pidFile }
    throw
}
