---
name: dev-loop
description: Runs and inspects the Lagerkraft local stack (Aspire AppHost or compose, seeded demo tenant, logs, NATS, Postgres) and the frozen Epixx POC. Use when asked to start the stack, run a service, seed a tenant, look at logs, connect to a tenant database, or run Epixx.
disable-model-invocation: true
---

# Dev loop

## Start everything

```powershell
dotnet run --project backend/src/AppHost        # Postgres, NATS, Mailpit, 4 services, dashboard URL printed
pnpm -C frontend dev                             # web on :5173, floor on :5174
```

Without Aspire: `docker compose up -d` then `dotnet run --project backend/src/<Service>` per service with `ASPNETCORE_ENVIRONMENT=Development`.

## Demo tenant

Provisioned on first AppHost start by the platform seeder: slug `demo`, owner `owner@demo.local` / `Demo123!`, one warehouse `DEMO`, an enrollment code printed in the platform log. Reset with `dotnet run --project backend/src/Platform -- seed --reset`.

## Look inside

```powershell
# Tenant DB (connection string from the platform log or pgAdmin in the dashboard)
psql "host=localhost port=5432 dbname=tenant_<id> user=postgres password=postgres"
SELECT seq, entity, op, occurred_at FROM change_log ORDER BY seq DESC LIMIT 20;
SELECT * FROM outbox WHERE published_at IS NULL;
SELECT * FROM processed_commands ORDER BY applied_at DESC LIMIT 20;

# NATS
nats stream info LAGERKRAFT
nats sub "lagerkraft.>"

# Migrate one tenant / all
dotnet run --project backend/src/WmsCore/Api -- migrate --tenant demo
dotnet run --project backend/src/WmsCore/Api -- migrate --all

# Replay outbox to NATS
dotnet run --project backend/src/WmsCore/Api -- replay --tenant demo --from <id> --to <id>
```

Emails land in Mailpit at http://localhost:8025.

## Run the frozen POC (reference only, never edit)

```powershell
sqllocaldb start MSSQLLocalDB
dotnet run --project Epixx/Epixx.csproj          # https://localhost:7132, admin@test.com / Admin123!
```

If the build fails with a file lock on `Epixx.exe`, an instance is already running; use it.

## Tests

```powershell
dotnet test backend/Lagerkraft.sln --filter "Category!=Load"   # unit + integration; needs Docker
dotnet test backend/tests/Lagerkraft.WmsCore.Tests --filter "FullyQualifiedName~ClaimTask"
pnpm -C frontend test
pnpm -C frontend e2e                             # Playwright E2E, needs the stack running
k6 run load/sync-commands.js                     # nightly in CI; local for a smoke run
```

Testcontainers needs Docker Desktop running.

## Tools on PATH

`dotnet`, `sqllocaldb`, `docker`, `node` are on PATH. Git is installed at `C:\Program Files\Git\cmd` but may not be on PATH in a given shell; pnpm comes from `corepack enable pnpm`.
