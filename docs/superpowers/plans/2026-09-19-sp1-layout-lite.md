# Sub-project 1 lite: Layout and mapping. Implementation plan

Spec: [2026-09-05-lagerkraft-architecture-design.md](../specs/2026-09-05-lagerkraft-architecture-design.md) (changelog 2026-09-19). Branch: `lagerkraft/sp1-layout`. Every task ends in a commit; every non-trivial task ends in a test that fails if the logic breaks. If a task needs something the spec is silent on, stop and propose a spec change.

> **For agentic workers:** implement one task at a time with `.cursor/skills/implement-plan-task/SKILL.md`. Do not start L2 while L1 is open.

**Goal:** A founder maps one aisle on the floor PWA (offline) and watches a live schematic fill in the back office, without being told what a Location is.

**Architecture:** `Lagerkraft.WmsCore.Layout` owns location contracts; `TenantDbContext` in wms-core Api stays the only writer. Mapping is commands (`CreateLocationBatch`, `SetLocationDimensions`) through the existing pipeline and outbox. The office map is SSE `change_log` upserts, the same path as tasks.

**Tech stack:** .NET 10, EF Core + Npgsql `ltree`, Vue 3, Dexie, Tailwind in `packages/ui`. No new UI library.

## Global constraints

- Spec wins. Client-generated UUIDv7 for every location. Floor is the truth. Holds not rejections for the location cap. One tenant prefix.
- Never edit `Epixx/`, `Epixx.slnx`, or the root `WarehouseController.cs`.
- Swedish UI strings in `frontend/packages/i18n` only. No emojis.
- Commits: `sp1: <task id> - <what>`.
- Verify: `dotnet build backend/Lagerkraft.sln -warnaserror`; `dotnet test backend/Lagerkraft.sln --filter "Category!=Load"` for backend tasks; `pnpm -C frontend lint && pnpm -C frontend typecheck && pnpm -C frontend test` when the frontend tree changed.

## Out of this plan

Labels, print agent, Bluetooth, CSV/JSON, location attributes, onboarding wizard, badge unlock, G1 CI, cloud/staging, catalog, stock, pick/ship.

---

## Task L1. Layout module, Location table, warehouse list, system locations

Files: `backend/src/WmsCore/Layout/Lagerkraft.WmsCore.Layout.csproj` (same shape as Inventory: classlib, `Contracts/` with `LocationDto` / `WarehouseDto` if moved), `backend/Lagerkraft.sln`, `backend/src/WmsCore/Api/Lagerkraft.WmsCore.Api.csproj`, `backend/tests/Lagerkraft.Architecture.Tests/Lagerkraft.Architecture.Tests.csproj` (ProjectReference so C3's module scan cannot skip Layout), `backend/src/WmsCore/Api/Data/Entities.cs`, `TenantDbContext.cs`, `Warehouses/WarehouseApi.cs`, new migration `LocationTree`.

Warehouse columns to add (nullable/defaults so expand/contract holds): `activated_at timestamptz null`, `default_height_mm` / `default_width_mm` / `default_depth_mm` / `default_max_weight_g` nullable ints. Default `code_pattern` when missing: `{aisle}-{rack:2}-{level:2}-{bin:2}`.

`Location`: `id`, `warehouse_id`, `parent_id` nullable, `type` (zone|aisle|rack|level|bin|floor|dock|staging), `code`, `path` ltree, `height_mm`, `width_mm`, `depth_mm`, `max_weight_g`, `barcode` (= code), `status` (active|inactive, default active), `is_system` bool. Unique `(warehouse_id, code)`. Index on `(warehouse_id, parent_id)`. Migration: `CREATE EXTENSION IF NOT EXISTS ltree` then the table.

On `POST /warehouses`, seed six system rows (`type=staging`, `is_system=true`, codes `RECEIVING`, `PICKING`, `SHIPPING`, `QUARANTINE`, `FLOOR`, `ADJUSTMENT`). They do not count toward the hard cap (L3). `GET /warehouses` returns id, name, code_pattern, activated_at. `POST /warehouses/{id}/activate` sets `activated_at` (`warehouses.manage`).

Steps:
1. Write `LayoutModuleTests.CreateWarehouse_SeedsSystemLocations` (integration): POST warehouse, GET locations or query DB, assert the six codes exist and `is_system`.
2. Run it; it fails (no table / no seed).
3. Add the Layout project, entity, migration (`dotnet ef migrations add LocationTree --project backend/src/WmsCore/Api --output-dir Data/Migrations`), GET list, seed in create, activate endpoint. `dotnet ef migrations has-pending-model-changes` clean.
4. Tests pass. Architecture tests still pass with the new ProjectReference.
5. Break the seed (skip `RECEIVING`); confirm the test fails; restore.

Tests: `CreateWarehouse_SeedsSystemLocations`; `ListWarehouses_ReturnsCreated`; `ActivateWarehouse_SetsActivatedAt`.

Commit: `sp1: L1 - location table, warehouse list, system locations`.

---

## Task L2. CreateLocationBatch

Files: `contracts/commands/CreateLocationBatch/v1.json` + `fixtures/v1.json`; `backend/src/WmsCore/Api/Commands/Layout/CreateLocationBatchHandler.cs` (or Layout module Commands if the dispatcher can see them — follow TaskHandlers in Api if Layout cannot reference TenantDbContext internals; keep handler next to Task handlers if that is the smaller diff); register in `Program.cs`; `backend/tests/Lagerkraft.Contracts.Tests` picks up the schema; `LayoutCommandTests.cs`.

Payload (spec 2026-09-19):

```json
{
  "warehouse_id": "uuid",
  "parent_id": null,
  "type": "aisle",
  "locations": [{ "id": "uuid", "code": "A", "parent_id": null }]
}
```

Required: `warehouse_id`, `type`, `locations` (min 1). Each location: client `id`, `code`, `parent_id` (null or an id in this batch or an existing row). Type aisle|rack|level|bin|floor|dock|zone (not system staging).

Rules:
- Unknown warehouse → `rejected` `unknown_warehouse` + deviation.
- Unknown parent → `rejected` `unknown_parent` + deviation.
- Code fails pattern prefix for its type → `rejected` `invalid_code` + deviation.
- Same code, different parent or type → `rejected` `structural_conflict` + deviation.
- Same code, same parent → treat as already created; include `{from: payload_id, to: existing_id}` in `details.id_map`; do not insert a second row.
- Trial hard cap: count of non-system `bin`/`floor` with `status=active` in warehouses where `activated_at` is set. Creating beyond cap → `held` `location_cap` (not rejected). Draft warehouses (null `activated_at`) never count and never hold.
- Permission `layout.map` at `occurred_at`. Viewer → `rejected` `forbidden`.
- Writes: location rows, `change_log` full state per location (`entity=location`, `op=upsert`), outbox `layout.location.created`, `processed_commands`.
- `path`: ltree from parent path + code slug (replace `-` with `_` if needed for ltree labels, or use a surrogate; pick one and test it). `barcode = code`.

Steps:
1. Write `CreateLocationBatch_UnknownParent_Rejected` first (the data-loss case: must not insert children). Run; fail.
2. Schema + handler + register.
3. Remaining tests: happy path inserts client ids; idempotent retry returns stored result; code collision remaps; hard cap held on activated warehouse; mapping in draft does not hold; viewer forbidden; occurred_at outside membership rejected.
4. Break remapping (always insert); confirm test fails; restore.
5. `pnpm -C frontend gen` if the domain generator includes commands.

Commit: `sp1: L2 - CreateLocationBatch with client ids and idempotent codes`.

---

## Task L3. SetLocationDimensions

Files: `contracts/commands/SetLocationDimensions/v1.json` + fixture; handler; register; tests in `LayoutCommandTests.cs`.

Payload: `ids` uuid[], `height_mm`, `width_mm`, `depth_mm`, `max_weight_g` (all ints, nullable individually but at least one set). Last-write-wins. Unknown id → `rejected` `unknown_location`. System locations may receive dimensions. `layout.map`. change_log upsert full state.

Tests: happy path; unknown id rejected; idempotent retry; last-write-wins on second command.

Commit: `sp1: L3 - SetLocationDimensions last-write-wins`.

---

## Task L4. Snapshot Location and Warehouse; kill the fake warehouse id

Files: `backend/src/WmsCore/Api/Internal/InternalApi.cs` `GetSnapshot` (today Task-only); floor `frontend/apps/floor/src/db.ts` Dexie v2 `locations` and `warehouses`; `stores/floor.ts` `loadSnapshot`; `WarehousePickView.vue` (no `01900000-…0001` fallback; show names); web `stores/tenant.ts` drop `DEV_WAREHOUSE_ID` fallback.

Snapshot `entity=Location` and `entity=Warehouse` paginated. `snapshot_schema` follows the new migration name.

Tests: integration `Snapshot_EntityLocation_ReturnsMappedAisle`; Vitest or Playwright later. Floor unit: warehouse pick with empty device.warehouse_ids loads from snapshot, not the hardcoded uuid.

Commit: `sp1: L4 - snapshot locations and real warehouse pick`.

---

## Task L5. Floor mapping flow

Files: `frontend/packages/ui` — `FloorButton.vue` (min-h-12, primary min-h-16), `NumberStepper.vue`; `frontend/packages/i18n` sv+en keys under `map.*`; `frontend/apps/floor` views `MapAisleView.vue` (one question per screen: aisle letter, racks, levels, bins, same dims, confirm), router `/map`, home link **Kartlägg gång**; `sync/outbox.ts` apply `id_map` rewrite on `applied` (the spec exception to opaque payloads); optimistic locations in the same Dexie transaction as the outbox insert.

Code generation on the device from warehouse `code_pattern` and answers. Confirm screen shows count and first/last code. Commands: one `CreateLocationBatch` per type layer (aisle, then racks, then levels, then bins) plus `SetLocationDimensions` for the bins (or per level if "same H/W/D" is no). Default dims from warehouse defaults, or 1200×800×400 mm / 500000 g if unset (`ponytail:` until the warehouse form collects them).

Tests: Vitest for pattern → codes (A, 2 racks, 3 levels, 2 bins → 1+2+6+12 locations, first bin `A-01-01-01`); outbox rewrite test: queued SetLocationDimensions ids follow `id_map`. Playwright in L7.

Commit: `sp1: L5 - floor one-question mapping and id_map rewrite`.

---

## Task L6. Office warehouse form and live schematic map

Files: `frontend/packages/ui` `EmptyState.vue`, `AisleMap.vue` (racks as columns, levels as rows, bins as cells; cells keyed by location id/code); `WarehousesView.vue` — name + code pattern with live example (`A-01-03-02` for the default pattern); extra fields behind `Mer`; empty state next action; map on the warehouse page fed by `useRealtimeFeed` like `TasksView.vue`. Nav: Lager is home; devices/users/sso visually secondary (muted links), not restyled.

Tests: Vitest for pattern preview; Playwright in L7.

Commit: `sp1: L6 - office live aisle map and warehouse create UX`.

---

## Task L7. Playwright: map offline, office map fills

Files: `frontend/apps/floor/e2e/map-offline.spec.ts`; extend or add web e2e that watches the map (or one spec driving both origins like claim-offline).

Flow: signup/login already in F2/F3 helpers if present; create warehouse; enroll if needed; floor offline; map aisle A, 2×3×2; go online; assert office schematic has 12 bin cells. Does not replace L2 integration tests.

Commit: `sp1: L7 - Playwright offline map to live office`.

---

## Order

L1 → L2 → L3 → L4 → L5 → L6 → L7. L3 can start after L2. L5 needs L2 and L4. L6 needs L4. L7 needs L5 and L6.

## Definition of done

On Aspire: sign up, create warehouse with default pattern and live example code, enroll device, pick warehouse **by name**, Kartlägg gång, map 2×3×2 offline, go online, office schematic fills without refresh. Browser verification of that path required. Playwright green. No labels, no fake warehouse id.
