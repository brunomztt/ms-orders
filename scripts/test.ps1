$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $projectRoot 'compose.yaml'
dotnet build (Join-Path $projectRoot 'src/MsOrders') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Get-Content -Raw (Join-Path $PSScriptRoot 'smoke.py') | docker compose -f $composeFile exec -T -e ORDERS_URL=http://orders:8080 -e CATALOG_URL=http://catalog:8080 -e NOTIFICATIONS_URL=http://notifications:8000 catalog python -
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
