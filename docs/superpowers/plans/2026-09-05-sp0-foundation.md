# Sub-project 0: Foundation. Implementation plan

Spec: [2026-09-05-lagerkraft-architecture-design.md](../specs/2026-09-05-lagerkraft-architecture-design.md). Branch: `lagerkraft/sp0-foundation`. Every task ends in a commit; every non-trivial task ends in a test that fails if the logic breaks. The plan is ordered so that something runs end to end as early as possible and grows from there.

## Goal

At the end of sub-project 0 a developer can run `dotnet run --project backend/src/AppHost` and get Postgres, NATS, four services and two thin frontends; sign up a tenant; watch it be provisioned; log in with a password or with Entra ID; enroll a device; claim and complete a task from the floor app while offline; see the task change live in the back office over SSE; and CI proves all of it on every pull request. No warehouse layout, catalog or ledger yet; those are sub-projects 1 to 3. What exists is the plumbing every later sub-project drops its feature into, exercised by one real feature ported from Epixx: task claiming with expiry.

## Scope (from the spec's sub-project 0 line, plus what the review pulled forward)

In: solution and service template, `Lagerkraft.Shared` kernel, platform DB and tenant DB contexts, tenant catalog and provisioning, migration fan-out command, local accounts and per-tenant OIDC, JWT with the spec's claims and `session_version`, roles and permission map, device registry and enrollment API, billing tables needed for trials and the hard cap (`BillingAccount`, `Plan`, `Subscription`) and the entitlement endpoint, tenant lifecycle states `Provisioning`, `Trialing`, `Active` and the maintenance flag, command pipeline in wms-core (idempotency, advisory locks, change_log, outbox, `processed_commands`, upcasters, results `applied | rejected | held | unknown`), outbox relay to NATS, sync-gateway (commands, changes, snapshot, compat, SSE), `Task` with claim-work and assignment expiry ported from Epixx plus the periodic-job framework its sweep needs, observability, frontend monorepo skeleton with the SSE client and the outbox, test infrastructure (Testcontainers, architecture tests, contract tests, Playwright offline, k6), CI on pull request, Terraform for staging and the two UpCloud spikes, runbooks for the sub-project 0 alerts.

Out: everything with a location, article or stock in it; label printing; the onboarding wizard; dunning and invoicing; SAML and SCIM; the print agent; Capacitor.

## Conventions used in every task

- .NET 10, C# latest, nullable on, `TreatWarningsAsErrors` on, central package management (`Directory.Packages.props`).
- Vertical slices: one folder per feature with `Endpoint.cs`, `Handler.cs`, `Validator.cs` (FluentValidation), no MediatR.
- Ids are UUIDv7 (`Guid.CreateVersion7()`); timestamps are `timestamptz`; money is integer öre; quantities do not exist yet.
- Tests: xUnit, `Shouldly` for assertions, Testcontainers for Postgres and NATS, one shared `PostgresFixture` per test project (collection fixture), `WebApplicationFactory` per service.
- Commits are small and named by task: `sp0: <task> - <what>`.
- Anything that needs a human (accounts, secrets, hardware) is marked **needs-human** and is not on the critical path of the tasks before it.

---

## Phase A: skeleton that runs

### Task A1. Solution, template project, Aspire AppHost

Files: `backend/Lagerkraft.sln`, `backend/Directory.Build.props`, `backend/Directory.Packages.props`, `backend/.editorconfig`, `backend/global.json` (SDK 10.0.400 roll-forward latestFeature), `backend/src/Shared/Lagerkraft.Shared.csproj`, `backend/src/Platform/Lagerkraft.Platform.csproj`, `backend/src/WmsCore/Api/Lagerkraft.WmsCore.Api.csproj`, `backend/src/SyncGateway/Lagerkraft.SyncGateway.csproj`, `backend/src/Integrations/Lagerkraft.Integrations.csproj`, `backend/src/AppHost/Lagerkraft.AppHost.csproj`, `backend/src/ServiceDefaults/Lagerkraft.ServiceDefaults.csproj`, `backend/tests/*/` (one test project per service plus `Lagerkraft.Architecture.Tests`, `Lagerkraft.Contracts.Tests`), `compose.yaml` at the repo root (Postgres 17, NATS 2.11 with JetStream, Mailpit).

Steps:
1. `dotnet new sln`, create the projects above as ASP.NET Core minimal API (`webapi` template, no controllers), reference `ServiceDefaults` from each service. `ServiceDefaults` is the Aspire-style project: OpenTelemetry (traces, metrics, logs via OTLP), Serilog with structured JSON, health endpoints `/health/live` and `/health/ready`, `AddServiceDiscovery`, resilience handlers for `HttpClient`.
2. AppHost: `AddPostgres("pg").WithPgAdmin()` with two databases `platform` and `tenant_demo`, `AddNats("nats").WithJetStream()`, `AddContainer("mailpit", ...)`, the four services with references and environment, later the two frontends via `AddNpmApp`.
3. Each service exposes `GET /` returning its name and version, and `/health/*`.
4. `compose.yaml` mirrors the infrastructure only (for anyone without Aspire); services run with `dotnet run` against it using `appsettings.Development.json` connection strings.

Verify: `dotnet build backend/Lagerkraft.sln` is warning-free; `dotnet run --project backend/src/AppHost` shows four healthy services in the Aspire dashboard; `docker compose up -d` brings up Postgres, NATS and Mailpit.

Commit: `sp0: A1 - solution, service template, AppHost`.

### Task A2. Shared kernel

Files under `backend/src/Shared/`: `Result.cs` (`Result<T>` with `Error(code, message, details)`), `Ids.cs` (UUIDv7 helper, `Slug` value object with the spec's rules and reserved list), `Clock.cs` (`IClock` with `SystemClock` and `FakeClock`), `Permissions.cs` (the spec's permission constants and the static role-to-permission map, one dictionary), `Tenancy/TenantContext.cs` (`TenantId`, `Slug`, `SchemaVersion`, `LifecycleState`, `Maintenance` flag), `Tenancy/TenantResolutionMiddleware.cs` (reads `tid` from the JWT, or the slug from the host for the back office, populates `TenantContext`), `Auth/LagerkraftClaims.cs` (claim names `sub tid amr own ra pv sv dev act`), `Auth/RoleAssignment.cs`, `Auth/PermissionPolicyProvider.cs` (`RequirePermission("x.y", warehouseId)` as an ASP.NET Core authorization policy that expands `ra` through the map), `Events/CloudEvent.cs`, `Events/EventEnvelope.cs`, `Outbox/OutboxMessage.cs`, `Outbox/IOutbox.cs`, `ChangeLog/ChangeLogEntry.cs`, `Jobs/PeriodicJob.cs` (a `BackgroundService` base that runs `RunOnceAsync` every N seconds with jitter, logs and records the last run as a metric; this is where Epixx's `ReservationCleanupService` pattern lands as a reusable base).

Tests (`Lagerkraft.Shared.Tests`): slug rules (padding, reserved words, length), permission expansion for each of the four roles against the spec's matrix (one data-driven test that fails if the map and the spec table diverge; the table is copied into the test as the expected data), `PeriodicJob` runs and survives an exception in `RunOnceAsync`.

Commit: `sp0: A2 - shared kernel`.

### Task A3. Architecture tests

Files: `backend/tests/Lagerkraft.Architecture.Tests/` using `NetArchTest.Rules`.

Rules: services do not reference each other (only `Shared` and `ServiceDefaults`); wms-core modules (`Lagerkraft.WmsCore.<Module>`) reference each other only through `*.Contracts` namespaces; nothing outside `Platform` references `Microsoft.AspNetCore.Identity`; no project references `MediatR`; `Shared` references no service.

Commit: `sp0: A3 - architecture tests`.

---

## Phase B: platform service

### Task B1. Platform DB and tenant catalog

Files under `backend/src/Platform/`: `Data/PlatformDbContext.cs` (Identity tables plus `Tenant`, `Membership`, `RoleAssignment`, `Role`, `Device`, `EnrollmentCode`, `IdentityProvider`, `SignupRequest`, `Invitation`, `BillingAccount`, `Plan`, `Subscription`, `LocationCounter`, `ProcessedEvent`, `TenantTombstone`, `AuditLog`), `Data/Migrations/` (EF), `Tenancy/TenantCatalog.cs` (read side: connection string decrypt with the platform DEK, `schema_version`, `migration_status`, lifecycle state, maintenance flag), `Tenancy/ConnectionStringProtector.cs` (AES-GCM with a key from configuration, per-tenant DEK wrapped by the KEK as the spec describes; the KEK is a dev constant in `appsettings.Development.json` and a Kubernetes secret in prod).

Columns follow the spec's "Platform data model changes" and "additions for the lifecycle" sections exactly, including `Tenant.slug` unique, `lifecycle_state`, `maintenance`, `feed_epoch` is **not** here (it lives in the tenant DB), `Membership.session_version`, `Membership.is_owner`, `RoleAssignment.valid_from/valid_to`, `Device.warehouse_ids`, `Device.revoked_at`, `Subscription` with trial fields, `Plan.hard_cap_units`.

Seed: four system roles (`tenant_admin`, `warehouse_manager`, `floor_worker`, `viewer`) and the plans `start`, `pro`, `business` with placeholder prices (öre), `is_public` true for the first two.

Internal API (called by other services, protected by a shared internal token header on the cluster network, `Lagerkraft-Internal-Token`): `GET /internal/tenants/{id}` (catalog row without the secret), `GET /internal/tenants/{id}/connection` (decrypted connection string; wms-core caches it), `GET /internal/tenants/{id}/entitlement` (plan features, hard cap, lifecycle state), `GET /internal/memberships/{tenantId}/history?userId=` (role assignments with validity windows, for authorization at `occurred_at`).

Tests: EF model snapshot has no pending changes (`dotnet ef migrations has-pending-model-changes` in CI, and a unit test that builds the model); catalog decrypts what the protector encrypted; entitlement endpoint returns the seeded plan.

Commit: `sp0: B1 - platform DB, catalog, internal API`.

### Task B2. Local accounts, JWT, refresh, session version

Files: `Auth/Login/` (email + password, optional TOTP step), `Auth/Refresh/`, `Auth/Logout/`, `Auth/SwitchTenant/`, `Auth/PinUnlock/` (server-side PIN check for devices: `POST /auth/pin-unlock` with device id, user id and PIN; PIN hash stored on `Membership` as `pin_hash`, Argon2id via `Konscious.Security.Cryptography` or PBKDF2 from `Microsoft.AspNetCore.Cryptography.KeyDerivation`, choose the latter since it is already in the framework), `Auth/Tokens/JwtIssuer.cs` (claims exactly as the spec: `sub tid amr own ra pv sv dev`, 15-minute access, refresh tokens as opaque random values hashed in `RefreshToken` table with `session_version`, 12-hour default lifetime read from `Tenant.refresh_token_hours`), `Auth/Tokens/SessionVersionBump.cs` (increments `Membership.session_version` and publishes `tenant.membership_changed`), tenant chooser: `POST /auth/login` returns either a token (one membership) or `{ memberships: [...] , chooser_token }` and `POST /auth/choose-tenant` exchanges it.

Signing key: RSA key pair in configuration for dev, Kubernetes secret in prod; JWKS at `GET /.well-known/jwks.json` so sync-gateway and wms-core validate offline.

Tests: login issues a token with the right claims for a two-warehouse manager; refresh fails after a session version bump; PIN unlock locks out after 5 failures for 15 minutes (use `FakeClock`); switch-tenant issues a token for the second membership and refuses a tenant the user is not a member of.

Commit: `sp0: B2 - local login, JWT, refresh, PIN unlock`.

### Task B3. Signup and provisioning

Files: `Signup/Request/` (form fields from the spec, Luhn check on the org number, disposable-domain list as an embedded resource, slug proposal and uniqueness, join-request branch when the org number exists), `Signup/Verify/` (24-hour token), `Signup/JoinRequest/` (owner approve creates an `Invitation`), `Provisioning/ProvisioningJob.cs` (a `PeriodicJob` that picks `SignupRequest` rows verified but not provisioned: create `Tenant` in `Provisioning`, generate the DEK, `CREATE DATABASE tenant_<id>` **or** call the provider API behind `ITenantDatabaseCreator` with two implementations `PostgresCreateDatabase` and `UpCloudApiCreator` (the second is a stub until the spike in Task G2), run `wms-core migrate --tenant` through an internal HTTP call to wms-core `POST /internal/tenants/{id}/migrate`, create owner `Membership` with `is_owner` and `tenant_admin`, create `BillingAccount` and `Subscription` in `trialing` with `trial_ends_at = now + 30 days` and the `pro` plan with `hard_cap_units = 1000`, publish `tenant.provisioned`, set `Trialing`; three attempts then `ProvisioningFailed` and an alert log line at Error level), `Email/IEmailSender.cs` with `MailpitSmtpSender` for dev and `MailjetSender` behind configuration.

Tests: full signup to `Trialing` against Testcontainers Postgres with wms-core's migrate endpoint faked; org-number collision produces a join request and reveals nothing but the company name; a failing `CREATE DATABASE` retries three times then lands in `ProvisioningFailed`.

Commit: `sp0: B3 - signup, verification, provisioning job`.

### Task B4. Per-tenant OIDC

Files: `Auth/Oidc/DynamicOidcHandler.cs` (resolve `IdentityProvider` by slug or email domain at request time, build the `OpenIdConnectOptions` per tenant, cache the discovery document per issuer), `Auth/Oidc/Callback/` (link or JIT-provision per the spec's strict rules: same tenant's provider, `email_verified`, domain matches `domain_hint`; `jit_provisioning` modes `off | mapped | all`; role from `role_mappings` on the `roles` claim then `groups`; groups-overflow detection via the `_claim_names` marker fails with the spec's message), `Auth/Oidc/Admin/` (tenant admin CRUD for the provider row, "test login" that completes the flow and reports the claims seen, `enforced` toggle that refuses unless the tenant is `Active`, one admin has logged in via SSO and every owner has TOTP enrolled), `client_secret_expires_at` with a `PeriodicJob` that emails owners at 30 and 7 days.

Tests: against a fake OIDC server in tests (`Duende.IdentityServer` test host is heavy; use `OpenIddict` in-memory or a hand-rolled minimal issuer with signed JWTs, whichever is smaller; a hand-rolled issuer of about 100 lines is the lazy option): JIT `off` refuses unknown users, `mapped` grants the mapped role, linking refuses an unverified email, enforced blocks password login for non-owners and allows owner plus TOTP.

**needs-human**: an Entra ID test tenant and a Google Workspace test project for the manual verification; until then the fake issuer is the test.

Commit: `sp0: B4 - per-tenant OIDC`.

### Task B5. Devices, enrollment, revocation

Files: `Devices/CreateEnrollmentCode/` (6 digits, 15 minutes, regenerable, tenant admin or warehouse manager), `Devices/Enroll/` (device posts the code, gets `device_id` and a device secret; returns `warehouse_ids` if the admin restricted it), `Devices/Revoke/` (sets `revoked_at`, bumps `session_version` for every membership cached on the device via `DeviceSession` rows, publishes `tenant.membership_changed`), `Devices/List/` (last sync, pending count and oldest pending `occurred_at` as reported by the gateway's beacon, `Devices/Beacon/` internal endpoint the gateway calls after each sync).

Tests: enrollment code expires; a revoked device gets `410` from a gateway call in Task E2; cannot re-enroll a revoked device id.

Commit: `sp0: B5 - device enrollment and revocation`.

### Task B6. Lifecycle state machine and maintenance flag

Files: `Lifecycle/TenantStateMachine.cs` (states and transitions from the spec's diagram, only `Provisioning`, `ProvisioningFailed`, `Trialing`, `Active`, `TrialExpired` wired in sub-project 0; the rest exist as enum values with transitions that throw `NotImplemented` so the table in the spec is the code's truth), every transition writes `AuditLog` and publishes `tenant.state_changed`, `Lifecycle/TrialExpiryJob.cs` (`PeriodicJob`, schedules the flip to `TrialExpired` at the tenant's next closed window per `operating_hours`, sends the 48-hour warning), `Lifecycle/ConvertTrial/` (owner picks a plan, enters billing details, `Active`).

Tests: transition table test (every allowed transition succeeds, every other pair throws); trial expiry lands at the next closed window given `operating_hours` and `night_shift` (use `FakeClock`).

Commit: `sp0: B6 - lifecycle state machine`.

---

## Phase C: wms-core command pipeline

### Task C1. Tenant DB context and migrate command

Files under `backend/src/WmsCore/`: `Api/Program.cs` with subcommands via `System.CommandLine`: default (serve), `migrate [--tenant <slug>] [--all]`, `relay`, `replay --from <id> --to <id>`, `verify --tenant <slug>`; `Data/TenantDbContext.cs` (one type, connection resolved per request from `ITenantConnectionCache`, which calls platform's internal API and caches with the last known value, refreshed by `tenant.provisioned` and `tenant.migrated`), `Data/TenantMeta` table (`feed_epoch uuid`, `schema_version`), `Data/ChangeLog` (`seq bigserial, entity, id, op, payload jsonb, command_id, actor, occurred_at, recorded_at`), `Data/Outbox`, `Data/ProcessedCommands (command_id pk, result jsonb, applied_at)`, `Data/ProcessedEvents`, `Migrations/Migrator.cs` (fan-out per the spec: platform first is platform's job; here: demo tenant as canary, then by size ascending, 8 in parallel, prefer closed hours, restartable, resets `migrating` older than 15 minutes, drops `INVALID` indexes, `lock_timeout = 5s`, `statement_timeout = 60s`, retries with backoff up to 30 minutes, per-tenant status written back to platform via `PUT /internal/tenants/{id}/migration-status`, threshold rule 2 tenants or 5%, `[RequiresSnapshot]` attribute honoured by calling `pg_dump` to Object Storage; the dump target is a local folder in dev), `Migrations/PostMigrationVerify.cs` (row counts against the pre-migration manifest when one exists, FK sanity; the ledger invariant is added in sub-project 3), `Tenancy/SchemaVersionMiddleware.cs` (compares the tenant's `__EFMigrationsHistory` with the version the code requires; mismatch means maintenance mode: writes `503` with `Lagerkraft-Tenant-Maintenance`, reads continue).

Tests: migrate two tenant containers in parallel, one with a poisoned migration (a table the migration expects is missing) and confirm continue-isolate-decide; restartability by killing the migrator between tenants (simulate with a cancellation token) and rerunning; the maintenance middleware returns `503` for writes and `200` for reads when versions differ.

Commit: `sp0: C1 - TenantDbContext, migrator, maintenance middleware`.

### Task C2. Command pipeline

Files: `Commands/CommandEnvelope.cs` (`id, type, v, payload, occurred_at, device_id, user_id`), `Commands/ICommandHandler<TCommand>.cs`, `Commands/CommandRegistry.cs` (type name to handler, current version, upcaster chain), `Commands/IUpcaster.cs`, `Commands/CommandDispatcher.cs`: for a batch, `pg_advisory_xact_lock(hash(device_id))`, then per command in order inside one transaction each: `processed_commands` lookup returns the stored result; clock skew shift applied to `occurred_at` when the batch's skew exceeds 5 minutes, with `clock_skew_ms` recorded; upcast; authorize against membership history at `occurred_at` (from platform's internal API, cached per batch); tenant state and hard-cap checks produce `held`; validator; handler; `change_log` insert under `pg_advisory_xact_lock(hash(tenant_id))`; outbox rows; `processed_commands` insert; result. A rejection also inserts a `Deviation(kind=rejected)` in the same transaction: the `Deviation` table is created here with the spec's columns because it is pipeline infrastructure, even though the deviation feature UI is later. Stale-command rule is a hook that sub-project 3 fills in (it needs the ledger); the hook exists and is tested with a fake.

Results: `applied | rejected | held | unknown`, with `426 Upgrade Required` semantics for an unknown version (that command and everything after it in the batch is not applied, response carries `min_app_version`).

Internal endpoints: `POST /internal/commands` (batch, from the gateway), `GET /internal/changes?warehouse=&since=` (returns entries plus `feed_epoch`, `410` if `since` is older than the oldest retained seq), `GET /internal/snapshot?warehouse=` (paginated per entity, only `Task` exists now), `GET /internal/compat` (`min_command_versions`, `latest_app_version`).

Tests: idempotent retry returns the stored result and applies once; two batches for one device serialize; seq order equals commit order under 50 concurrent batches (the advisory lock test from the failure-mode analysis); upcaster chain golden test reads `contracts/commands/*/v*.json`; `held` for a tenant in `TrialExpired`; `426` stops the batch at the right index; rejection writes a deviation in the same transaction (kill the transaction after the domain write and check neither exists).

Commit: `sp0: C2 - command pipeline with idempotency, locks, upcasters`.

### Task C3. Task module: the Epixx port

Files: `Inventory/Lagerkraft.WmsCore.Inventory.csproj` with `Contracts/` (public) and `Tasks/` (internal): `Task(id, warehouse_id, type putaway|pick|move|count, status open|claimed|done|cancelled, assignee_user_id, assigned_until, suggested_location_id nullable, created_at)` and `TaskLine` per the spec (quantities as `NUMERIC(18,6)` even though nothing fills them yet, so the column type is right from the first migration), commands `CreateTask` (back office, REST for now and also a command so the pipeline has a second type), `ClaimTask(task_id)` (Epixx `ClaimPalletsForTransfer`: `SELECT ... FOR UPDATE SKIP LOCKED`, assignment expires after `Warehouse.claim_minutes`, default 30; a claim on a task assigned to someone else whose `assigned_until` has passed succeeds), `ReleaseTask`, `CompleteTask`; `Tasks/AssignmentSweepJob.cs` (`PeriodicJob`, every 60 seconds, releases expired claims and writes change_log entries: Epixx's `ReservationCleanupService`). `warehouse_id` is a plain column until Layout exists in sub-project 1; a `Warehouse(id, name, code_pattern, operating_hours, night_shift, claim_minutes, blind_count, count_auto_adjust_threshold, pack_step, zone_picking)` stub table is created here because tasks, permissions and the gateway all scope by warehouse, with `POST /warehouses` for admins. Sub-project 1 grows it; it does not replace it.

Tests: 20 concurrent `ClaimTask` for one task yield exactly one `applied` and 19 `rejected` with `already_claimed`; the sweep releases an expired claim and emits a change_log entry; claim by a `viewer` is rejected with `forbidden`; claim with `occurred_at` before the user's `valid_from` is rejected, after `valid_to` too, in between accepted.

Commit: `sp0: C3 - Task module with claim-work and assignment sweep`.

### Task C4. Outbox relay and replay

Files: `Relay/OutboxRelay.cs` (`PeriodicJob` at 200 ms when idle, tight loop when busy: `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 100`, publish to `lagerkraft.{tenant}.{module}.{event}` with `Nats-Msg-Id` = event id, mark published; runs per tenant DB, iterating the catalog; JetStream stream `LAGERKRAFT` with subjects `lagerkraft.>`, dedupe window 10 minutes, file storage, created on startup if missing), `Relay/Replay.cs` (`wms-core replay --tenant --from --to`), `Events/` (the first events in `contracts/events/`: `inventory.task.created|claimed|released|completed`, `tenant.provisioned|state_changed|membership_changed|migrated` owned by platform, all CloudEvents JSON with `specversion`, `id`, `source`, `type`, `time`, `data`), retention job deletes outbox rows older than 30 days and change_log older than 30 days.

Tests (Testcontainers NATS): publish twice with the same `Nats-Msg-Id` and consume once; kill the relay mid-batch and confirm nothing is lost or duplicated after restart; contract test validates every fixture in `contracts/events/` against its JSON Schema.

Commit: `sp0: C4 - outbox relay, replay, event contracts`.

---

## Phase D: sync-gateway

### Task D1. Stateless gateway: commands, changes, snapshot, compat

Files under `backend/src/SyncGateway/`: JWT validation against platform's JWKS, `Sync/Commands/` (`POST /sync/commands`: validates device claim `dev` and that the device is not revoked (`410`), enforces one in-flight batch per device with an in-memory set plus `409`; evaluates lifecycle state and maintenance flag for the batch and returns `402 | 423 | 503` with `Lagerkraft-Tenant-State`; accepts flushes with a stale `sv` and any active member's session per the spec, but only for `POST /sync/commands`; forwards to wms-core over internal HTTP with the batch's `now` for skew; posts the device beacon to platform), `Sync/Changes/` (`GET /sync/changes` proxy with `feed_epoch` header), `Sync/Snapshot/`, `Sync/Compat/` (`GET /sync/compat`, and the same values as headers `Lagerkraft-Min-App-Version`, `Lagerkraft-Latest-App-Version` on every sync response), rate limiting `429` with `Retry-After` per tenant using the built-in rate limiter, `X-Lagerkraft-App-Version` recorded as a metric label.

Tests: `409` on a concurrent second batch for the same device; `423` for a `TrialExpired` tenant with the whole batch untouched; revoked device gets `410`; stale `sv` flush is accepted and a stale `sv` read is `401`; headers present.

Commit: `sp0: D1 - sync-gateway command intake, changes, compat`.

### Task D2. Realtime SSE

Files: `Realtime/RealtimeEndpoint.cs` (`GET /realtime?warehouse=&since=`, JWT in the initial request, `Last-Event-ID` = seq; on connect: subscribe to `lagerkraft.{tenant}.>` first, buffer, serve catch-up from wms-core's changes endpoint, then drain the buffer and go live; heartbeat comment every 15 seconds; `event: resync` on NATS reconnect; on `SIGTERM` send `retry: <1-10 s random>` and close within the grace period), `Realtime/MembershipWatcher.cs` (subscribes to `tenant.membership_changed` and closes connections whose user's `sv` changed), `Realtime/ConnectionRegistry.cs` (in-memory, per instance, metrics: connections by tenant, delivery lag histogram measured from `recorded_at` to send).

Tests: connect with a stale `Last-Event-ID`, publish three change entries during catch-up, receive exactly the right sequence without gaps or duplicates; membership change closes the stream; `SIGTERM` simulation sends `retry`.

Commit: `sp0: D2 - realtime SSE with catch-up-then-live`.

---

## Phase E: integrations service skeleton

### Task E1. Event consumer and webhook delivery skeleton

Files under `backend/src/Integrations/`: durable JetStream consumer `integrations` on `lagerkraft.>`, `processed_events` dedupe, `Webhooks/` (`WebhookEndpoint(tenant_id, url, secret, events[], active)`, `WebhookDelivery` with retry schedule 1m, 5m, 30m, 2h, 12h then dead-letter, HMAC-SHA256 signature header, `webhook.gap` event type defined), tenant admin CRUD for endpoints. Import and export jobs are sub-project 1 and 2; only the folder and the `ImportJob` table exist.

Tests: duplicate event delivered once; failed delivery follows the retry schedule with `FakeClock`; signature verifies.

Commit: `sp0: E1 - integrations consumer and webhook delivery`.

---

## Phase F: frontend skeleton

### Task F1. pnpm monorepo, packages

Files: `frontend/pnpm-workspace.yaml`, `frontend/package.json`, `frontend/tsconfig.base.json`, `frontend/.npmrc`, `frontend/packages/domain/` (command types generated from `contracts/commands/*.json` via `json-schema-to-typescript`; the permission map as a TypeScript constant generated from `Lagerkraft.Shared.Permissions` by a small `dotnet run` generator so the two never drift, with a CI check that the generated file is committed and current), `frontend/packages/api-client/` (generated from `contracts/openapi/*.json`, which each service writes at build with `Microsoft.AspNetCore.OpenApi`; `openapi-typescript` plus `openapi-fetch`), `frontend/packages/realtime/` (`createRealtime({ url, token, warehouse, since })`: `EventSource` with `Last-Event-ID`, 45-second dead-connection detection, reconnect with the server's `retry`, `resync` handling, 30-second polling fallback, 100 ms batching for consumers), `frontend/packages/ui/` (Tailwind config, shadcn-vue init, three components: `Button`, `Input`, `Banner`).

Tooling: Vite, Vue 3, TypeScript strict, ESLint flat config, Prettier, Vitest.

Tests: `realtime` unit tests with a fake `EventSource`: catch-up then live ordering, reconnect on missing heartbeat, batch flush.

**needs-human**: `corepack enable pnpm` on each dev machine (Node 24 ships corepack).

Commit: `sp0: F1 - frontend monorepo and shared packages`.

### Task F2. Back office shell (`apps/web`)

Pages: signup (with slug preview and the join-request branch), verify, login (password, TOTP step, SSO redirect when the tenant has a provider, tenant chooser), provisioning progress (waits on `tenant.provisioned` over SSE), warehouses (create, list), tasks (create, list live over SSE with `useRealtime()` feeding TanStack Query, "last activity" line from `actor` and `occurred_at`), devices (enrollment code, list with pending counts, revoke), users (invite, roles per warehouse), SSO settings (provider CRUD, test login, enforced toggle with its preconditions explained), trial banner from day 20 and the `TrialExpired` conversion page. Tenant switcher in the header.

Tests: Vitest for the auth store and the realtime composable; Playwright smoke: signup to task list.

Commit: `sp0: F2 - back office shell`.

### Task F3. Floor app shell (`apps/floor`)

Files: `apps/floor/` with vite-plugin-pwa (`registerType: 'prompt'`, full precache), Dexie schema v1: `outbox(id, type, v, payload, created_at, device_id, user_id, state, sent_at)`, `confirmed(id, confirmed_at)` (7 days), `tasks`, `cursor(warehouse_id, seq, feed_epoch, snapshot_schema)`, `sessions(user_id, display_name, encrypted_refresh_token, pin_hash, failed_attempts, locked_until)`, `device(id, secret, warehouse_ids, crypto_key_handle)`; `sync/` (Web Locks around the flush, batch size 50, results handling for `applied | rejected | held | unknown` and `426`, `410` → snapshot resync, `feed_epoch` change → resync and re-send unconfirmed, skew: send `now` with each batch), `auth/` (full login online, enrollment screen that refuses unless `display-mode: standalone` on iOS, pick-name-then-PIN unlock with the local hash and lockout rules, 60-minute expiry warning, `navigator.storage.persist()` at enrollment), `update/` (`isAtRest()` including the `426`-blocked clause, `applyUpdate()`), screens: enroll, unlock, warehouse pick, task list (`liveQuery`), task detail with claim and complete, sync issues list (device-bound), unsynced badge and 4-hour warning.

Tests: Vitest with `fake-indexeddb` for the outbox state machine (pending → sent → acked → confirmed, re-send of `sent` older than 60 seconds, `held` stays pending); Playwright: claim a task with `context.setOffline(true)`, go online, see it `applied` and the back office list update.

Commit: `sp0: F3 - floor app shell with offline outbox`.

---

## Phase G: CI, infrastructure, operations

### Task G1. GitHub Actions on pull request

Files: `.github/workflows/pr.yml` with path filters (`backend/src/Shared/**` and `contracts/**` trigger everything), jobs: `dotnet build` and unit tests; integration tests with Testcontainers (Docker service on the runner); architecture tests; contract tests; migration check (fresh Postgres, apply all migrations, `dotnet ef migrations has-pending-model-changes`, `dotnet ef migrations script` piped to `squawk` with the ruleset requiring `lock_timeout` and concurrent indexes); frontend lint, typecheck, Vitest, generated files current; Playwright against the AppHost started in CI; container images built with `docker buildx` but not pushed. `.github/workflows/nightly.yml`: k6 against a CI-started stack for `POST /sync/commands`, `GET /sync/changes` and SSE fan-out (500 idle connections plus a burst, delivery latency p95 under 1 second as the first threshold).

**needs-human**: the repository is `palletpilot/Epixx` on GitHub; branch protection on `master` requiring the PR checks once the workflow exists.

Commit: `sp0: G1 - CI on pull request and nightly load tests`.

### Task G2. Staging infrastructure and the two spikes

Files: `infra/terraform/` (UpCloud provider: UKS cluster with 3 workers, Managed PostgreSQL HA, Managed Load Balancer, Object Storage buckets `backups` and `tenants`, SDN network), `infra/k8s/base/` (Deployments, Services, PodDisruptionBudgets, the migrate Job, cert-manager issuer, NATS StatefulSet single node with file store) and `infra/k8s/overlays/staging/`, `.github/workflows/deploy-staging.yml` (on merge to `master`: build and push images by digest, write `releases/<date>-<n>.yaml`, `kustomize edit set image`, apply, wait for the migrate Job, rollout status, Playwright smoke, two-minute k6; the frontend bundle publish step `needs:` the service rollouts and health checks).

Spikes, each a short markdown note in `docs/superpowers/spikes/`: (1) can the platform role `CREATE DATABASE` on UpCloud Managed PostgreSQL; if not, implement `UpCloudApiCreator` from Task B3 for real; (2) does the Managed Load Balancer terminate TLS with HTTP/2 to clients; if not, `SharedWorker` fallback in `packages/realtime`.

**needs-human**: UpCloud account, API credentials as GitHub secrets, DNS for `*.staging.lagerkraft.se`, a founder other than the author for the `production` environment approval rule (prod promotion workflow is written but not exercised in sub-project 0).

Commit: `sp0: G2 - staging infrastructure, deploy workflow, spikes`.

### Task G3. Observability and runbooks

Files: OTel exporters configured for the Grafana stack (Prometheus, Loki, Tempo) in the staging overlay, Bugsink deployment for error tracking, dashboards as JSON in `infra/grafana/` for the four services with the panels the spec lists for sub-project 0 (RED per module, outbox backlog age, change_log write rate, migration status per tenant, commands per second and rejected share, SSE connections and delivery lag, devices offline age, logins by method and outcome, provisioning jobs), alert rules for: API down, login failures, sync intake errors, Postgres connections or disk, NATS not ready, outbox backlog age, tenant in maintenance mode, backup failed (rule defined, job arrives with BDR work in a later sub-project), certificate expiry; `lagerkraft_tenant_active{tenant}` metric from platform for tenant-aware paging. `docs/runbooks/<alert>.md` for each of those alerts in the spec's format: meaning, confirm, likely causes with the distinguishing command, fix, verify.

**needs-human**: Better Stack account for status page and paging.

Commit: `sp0: G3 - observability, alerts, runbooks`.

---

## Definition of done for sub-project 0

1. `dotnet run --project backend/src/AppHost` starts everything; `pnpm dev` in `frontend/` serves both apps against it.
2. Signup to `Trialing` in under a minute locally, provisioning visible in the back office, DB exists, migrations applied, owner logged in.
3. Login with password, with TOTP, with the fake OIDC issuer in tests and with Entra ID once the test tenant exists; tenant chooser for a two-tenant user; `enforced` respected with the owner break-glass.
4. Enroll a device from the back office code; unlock by name and PIN; lockout after 5 failures.
5. Claim and complete a task offline on the floor app; flush on reconnect; back office task list updates over SSE within a second; a second device's claim of the same task is rejected and shows in the sync issues list.
6. Restore drill in miniature: rotate `feed_epoch` on the demo tenant by hand and watch every client resync and re-send unconfirmed commands.
7. Migrate a poisoned tenant in staging: it lands in maintenance mode, the others proceed, the alert fires, `wms-core migrate --tenant` repairs it.
8. CI green on the pull request workflow; nightly k6 within thresholds; staging deploys on merge; the two spike notes written with a decision each.
9. Runbooks exist for every alert that can fire.

## Order and parallelism

A1 → A2 → A3 are sequential and short. B1 and C1 can proceed in parallel once A2 is in; B2 to B6 depend on B1; C2 to C4 depend on C1; D1 depends on C2 and B2; D2 depends on C4 and D1; E1 depends on C4; F1 can start after A2 (contracts exist as files before services exist); F2 depends on B2, B3, D2; F3 depends on D1, D2, B5; G1 can start after A3 and grows with every task; G2 and G3 are last and mostly needs-human.

Two developers: one takes B (platform), one takes C then D (core and gateway); whoever finishes first takes F1. A third developer takes F2 and F3 as soon as D2 lands, and G in the gaps.

## Deliberate simplifications (each carries its ceiling)

- `ponytail:` the gateway's in-flight-batch set and SSE registry are per instance, in memory; wms-core's advisory lock is the real guard, so this is correct but a second instance does not know about the first's `409`; fine until we scale out, then a shared set in Postgres or NATS KV.
- `ponytail:` membership history is fetched from platform per batch and cached for the batch; a `membership_history` table in the tenant DB fed by events replaces it when latency shows.
- `ponytail:` the periodic-job framework is a `BackgroundService` per job with no leader election; two replicas run every sweep twice, which is harmless for idempotent sweeps but wasteful; add a Postgres advisory-lock leader when replicas exceed one.
- `ponytail:` `Warehouse` is a stub table; sub-project 1 adds the location tree, code pattern validation and the map.
- `ponytail:` no `Backfill` framework yet; the first data backfill in a later sub-project brings it.
