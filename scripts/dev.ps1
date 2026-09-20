# Starts the local Lagerkraft stack: Postgres, NATS, Mailpit, four services,
# Aspire dashboard, office (:5173) and floor (:5174).
# Stop with Ctrl+C. Compose cannot run at the same time: AppHost owns those containers.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Assert-OnPath([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Error "$Name is not on PATH."
    }
}

function Stop-ProcessTree([int]$Id) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$Id" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-ProcessTree -Id $_.ProcessId }
    Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}

Assert-OnPath docker
Assert-OnPath pnpm

try {
    docker info --format "{{.ServerVersion}}" | Out-Null
} catch {
    Write-Error "Docker engine is not ready. Start Docker Desktop and retry."
}

$composeIds = docker compose ps -q 2>$null
if ($composeIds) {
    Write-Host "Stopping docker compose so AppHost can own Postgres, NATS and Mailpit."
    docker compose down
}

if (-not (Test-Path "frontend/node_modules")) {
    Write-Host "Installing frontend dependencies."
    pnpm -C frontend install
}

$pnpm = (Get-Command pnpm.cmd).Source
$vite = @()
try {
    Write-Host "Office http://localhost:5173  Floor http://localhost:5174"
    $vite += Start-Process -FilePath $pnpm -ArgumentList @("-C", "frontend", "dev:web") -PassThru -NoNewWindow
    $vite += Start-Process -FilePath $pnpm -ArgumentList @("-C", "frontend", "dev:floor") -PassThru -NoNewWindow
    dotnet run --project backend/src/AppHost
}
finally {
    foreach ($p in $vite) {
        if ($null -ne $p) {
            Stop-ProcessTree -Id $p.Id
        }
    }
}
