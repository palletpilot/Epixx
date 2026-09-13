# Starts the local Lagerkraft stack (Postgres, NATS, Mailpit, four services, Aspire dashboard).
# Stop with Ctrl+C. Compose cannot run at the same time: AppHost owns those containers.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    Write-Error "docker is not on PATH. Install Docker Desktop and retry."
}

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

dotnet run --project backend/src/AppHost
