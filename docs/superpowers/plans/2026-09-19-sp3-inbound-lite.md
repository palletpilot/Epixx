# Sub-project 3 lite: Inventory + inbound. Implementation plan

> **For agentic workers:** implement one task at a time with `.cursor/skills/implement-plan-task/SKILL.md`. Superpowers `executing-plans` also fits. Steps use checkbox (`- [ ]`) syntax for tracking.

Spec: [2026-09-05-lagerkraft-architecture-design.md](../specs/2026-09-05-lagerkraft-architecture-design.md) (Scope decomposition item 3 **Lite**, Stock representation, StockMovement ledger, floor is the truth, ConfirmPutaway, LocationReservation, Permission model). Branch: `lagerkraft/sp3-inbound` from `lagerkraft/sp2-catalog`. Every task ends in a commit; every non-trivial task ends in a test that fails if the logic breaks. If a task needs something the spec is silent on, stop and propose a spec change.

**Goal:** A worker receives one handling unit into `RECEIVING`, a putaway task appears with a suggested bin, and `ConfirmPutaway` moves the unit there so outbound can pick it next.

**Architecture:** `Lagerkraft.WmsCore.Inventory` owns ledger and handling-unit contracts. `TenantDbContext` in wms-core Api stays the only writer. Receive and putaway are floor commands through the existing pipeline (not office REST). `change_log` entities `handling_unit`, `stock_balance`, `task`. Suggestion is first empty mapped bin; no strategy interface yet.

**Tech stack:** .NET 10, EF Core + Npgsql, Vue 3, Dexie, Tailwind in `packages/ui`. Quantities `NUMERIC(18,6)` as decimal strings on the wire. No new UI library.

## Global constraints

- Spec wins. Client-generated UUIDv7 for handling unit, content is 1:1 with the HU in lite, putaway `task_id`, reservation. Floor is the truth: wrong suggested bin is `Deviation(kind=policy_override)`, not a rejection. Rejections are impossible states only. One tenant prefix.
- Never edit `Epixx/`, `Epixx.slnx`, or the root `WarehouseController.cs`.
- Swedish UI strings in `frontend/packages/i18n` only. No emojis.
- Commits: `sp3: <task id> - <what>`.
- Verify: `dotnet build backend/Lagerkraft.sln -warnaserror`; `dotnet test backend/Lagerkraft.sln --filter "Category!=Load"` for backend tasks; `pnpm -C frontend lint ; pnpm -C frontend typecheck ; pnpm -C frontend test` when the frontend tree changed. If Platform.Tests `UseEnvironment` still fails at HEAD, build WmsCore.Api `-warnaserror` and test WmsCore.Tests + Architecture.Tests instead, and name that in the summary.
- Permissions `inbound.receive` / `inbound.putaway` / `inventory.read` already exist. Commands check them at `occurred_at` like `CreateLocationBatch`.

## Assumptions (spec silent; smallest answers)

1. **No ASN / Receipt / ReceiptLine in lite.** Receive is the command `ReceiveHandlingUnit`. Over-receipt deviations wait until receipt lines exist.
2. **Receive is a floor command**, not office REST. Physical goods-in. Office Uppgifter already lists the putaway task.
3. **One content row per HU.** `qty_base` is the article base quantity received. Default packaging level is the article's rank-1 level when `packaging_level_id` is omitted.
4. **LPN** is a client string, unique per warehouse, shown on screen. Floor mints `LPN-` plus the first 8 hex chars of the HU id (uppercase). Printing waits.
5. **Suggestion** = first empty mapped `bin` (`type=bin`, `is_system=false`, `status=active`) ordered by `code`. Empty = no `stock_balance` with `qty_base > 0` and no `location_reservation` with `expires_at > now`. If none, `suggested_location_id` is null and no reservation is written.
6. **LocationReservation** `expires_at` = now + warehouse `claim_minutes` (default 30). Unique `location_id` (one live reservation per bin). Released on `ConfirmPutaway`.
7. **ConfirmPutaway** in lite always moves the whole HU. `qty` must equal the HU content `qty_base`. `new_hu_id` is optional and ignored (additive field, no `v` bump). Partial putaway is later.
8. **Receive movement:** `reason=receive`, `from_location_id` null, `to_location_id` = warehouse `RECEIVING`, `from_handling_unit_id` null, `to_handling_unit_id` = HU id.
9. **Putaway movement:** `reason=putaway`, from `RECEIVING` to `location_id`, same HU both sides.
10. **Wrong suggested bin** is accepted with `Deviation(kind=policy_override)`. Unknown/inactive location, HU already not at `RECEIVING`, unknown task, qty mismatch: rejected.

## Out of this plan

ASN/receipts, smart putaway strategy, dimensional fit, partial putaway, `new_hu_id` split, receive-as-new-article drafts, labels/print, count, move, office inbound form, outbound.

---

## Files (locked here)

| Path | Role |
|---|---|
| `backend/src/WmsCore/Inventory/Contracts/Dtos.cs` | `HandlingUnitDto`, `HandlingUnitContentDto`, `StockBalanceDto` (extend existing file). |
| `backend/src/WmsCore/Api/Data/Entities.cs` | `HandlingUnit`, `HandlingUnitContent`, `StockMovement`, `StockBalance`, `LocationReservation`. |
| `backend/src/WmsCore/Api/Data/TenantDbContext.cs` | DbSets + indexes. |
| `backend/src/WmsCore/Api/Data/Migrations/` | `InventoryTables` (additive). |
| `contracts/commands/ReceiveHandlingUnit/v1.json` + `fixtures/v1.json` | Receive schema. |
| `contracts/commands/ConfirmPutaway/v1.json` + `fixtures/v1.json` | Putaway schema. |
| `backend/src/WmsCore/Api/Commands/Inventory/ReceiveHandlingUnitHandler.cs` | Receive + seed putaway task + reservation. |
| `backend/src/WmsCore/Api/Commands/Inventory/ConfirmPutawayHandler.cs` | Move HU, complete task, release reservation. |
| `backend/src/WmsCore/Api/Program.cs` | Register handlers. |
| `backend/src/WmsCore/Api/Internal/InternalApi.cs` | Snapshot `HandlingUnit`, `StockBalance`. |
| `backend/tests/Lagerkraft.WmsCore.Tests/InventoryModuleTests.cs` | Migrate + ledger. |
| `backend/tests/Lagerkraft.WmsCore.Tests/InboundCommandTests.cs` | Receive + ConfirmPutaway. |
| `frontend/packages/domain/src/generated/commands.ts` | `pnpm -C frontend gen:commands`. |
| `frontend/apps/floor/src/db.ts` | Dexie v4 `handling_units`, `stock_balances`. |
| `frontend/apps/floor/src/stores/floor.ts` | Snapshot fetch + enqueue receive/putaway. |
| `frontend/apps/floor/src/views/ReceiveView.vue` | Receive one HU. |
| `frontend/apps/floor/src/views/TaskDetailView.vue` | Putaway confirm. |
| `frontend/apps/floor/src/router.ts` | `/receive`. |
| `frontend/packages/i18n/src/locales/{sv,en}.json` | `floor.receive*` / `putaway.*`. |
| `frontend/apps/floor/e2e/receive-putaway.spec.ts` | Playwright mocked loop. |

---

## Task I1. Inventory tables (done 2026-09-19, d292648)

**Files:** `Entities.cs`, `TenantDbContext.cs`, migration `InventoryTables`, extend `Inventory/Contracts/Dtos.cs`, test `InventoryModuleTests.cs`. Also `LayoutCommandTests` `snapshot_schema` if the last migration name changes (same as C1).

**Files:** `Entities.cs`, `TenantDbContext.cs`, migration `InventoryTables`, extend `Inventory/Contracts/Dtos.cs`, test `InventoryModuleTests.cs`. Also `LayoutCommandTests` `snapshot_schema` if the last migration name changes (same as C1).

**Interfaces:**

- Consumes: tenant migrate used by `CatalogModuleTests`.
- Produces: tables `handling_unit`, `handling_unit_content`, `stock_movement`, `stock_balance`, `location_reservation`.

Entities:

```
HandlingUnit: id, warehouse_id, lpn, height_mm null, received_at
HandlingUnitContent: id, handling_unit_id, article_id, qty_base numeric(18,6), packaging_level_id
StockMovement: id, occurred_at, recorded_at, actor_user_id, device_id, command_id,
               article_id, from_location_id null, to_location_id null,
               from_handling_unit_id null, to_handling_unit_id null,
               qty_base numeric(18,6), base_uom_code,
               entered_qty numeric(18,6), entered_level_id null, entered_uom_id null,
               secondary_qty null, secondary_uom_id null,
               reason, reference_type null, reference_id null, tolerance_delta_base null
StockBalance: location_id, article_id, handling_unit_id, qty_base numeric(18,6),
              reserved_qty_base numeric(18,6), secondary_qty null
              composite PK (location_id, article_id, handling_unit_id)
LocationReservation: location_id (PK), task_id, expires_at
```

Unique `(warehouse_id, lpn)` on handling_unit. Unique `(handling_unit_id, article_id)` on content.

- [x] **Step 1:** Write `InventoryModuleTests.Migrate_CreatesInventoryTables`. Copy `CatalogModuleTests` fixture. After migrate, `to_regclass` for the five table names is not null.

- [x] **Step 2:** Run `dotnet test backend/tests/Lagerkraft.WmsCore.Tests/Lagerkraft.WmsCore.Tests.csproj --filter "FullyQualifiedName~Migrate_CreatesInventoryTables"`. Expected: FAIL (no table).

- [x] **Step 3:** Entities, DbSets, indexes, `dotnet ef migrations add InventoryTables --project backend/src/WmsCore/Api --output-dir Data/Migrations`. Additive only. `has-pending-model-changes` prints none.

- [x] **Step 4:** Re-run the test. PASS. Update `LayoutCommandTests` snapshot_schema if it still expects `CatalogTables`.

- [x] **Step 5:** Break (rename a table in the test query); confirm fail; restore.

**Tests:** `Migrate_CreatesInventoryTables`.

**Commit:** `sp3: I1 - inventory tables for HU, ledger, balances`.

---

## Task I2. ReceiveHandlingUnit (done 2026-09-19, d88b0c1)

**Files:** `contracts/commands/ReceiveHandlingUnit/v1.json` + fixture; `ReceiveHandlingUnitHandler.cs`; `Program.cs` register; `InboundCommandTests.cs`; `CommandRegistryTests` lists the type; `pnpm -C frontend gen:commands`.

**Interfaces:**

```
ReceiveHandlingUnit v1
  warehouse_id, handling_unit_id, task_id, lpn, article_id, qty_base
  packaging_level_id?  height_mm?
```

`qty_base` is a decimal string.

Create rules:

- Empty `handling_unit_id` or `task_id` → rejected `validation`.
- Unknown warehouse → `unknown_warehouse`.
- Unknown article → `unknown_article`.
- Missing `inbound.receive` at `occurred_at` → `forbidden`.
- Duplicate LPN in warehouse → `duplicate_lpn`.
- `qty_base` parseable decimal > 0, multiple of article `quantity_step`, precision respected; else `invalid_qty`.
- Default `packaging_level_id` = article's rank-1 level; unknown id → `unknown_packaging_level`.
- Insert HU, one content row, movement (`reason=receive`, from location null, to `RECEIVING`), balance at RECEIVING.
- Insert `task` `type=putaway` `status=open` with client `task_id`, one `task_line` (article, qty, `from_location_id`=RECEIVING, `from_handling_unit_id`=HU).
- Suggestion + reservation per assumptions 5–6.
- `change_log` upserts: `handling_unit` (content nested), `stock_balance`, `task`. Outbox `inventory.received` with the HU payload.
- Idempotent retry via `processed_commands`.

- [x] **Step 1:** Write `ReceiveHandlingUnit_UnknownArticle_Rejected` (must not insert HU). Run; fail.

- [x] **Step 2:** Schema, fixture, handler, register.

- [x] **Step 3:** Remaining tests: `ReceiveHandlingUnit_HappyPath_BalanceAtReceiving`; `ReceiveHandlingUnit_SuggestsFirstEmptyBin`; `ReceiveHandlingUnit_DuplicateLpn_Rejected`; `ReceiveHandlingUnit_Viewer_Forbidden`; `ReceiveHandlingUnit_IdempotentRetry`.

- [x] **Step 4:** Break suggestion (always null); confirm empty-bin test fails; restore.

**Tests:** those six names.

**Commit:** `sp3: I2 - ReceiveHandlingUnit into RECEIVING with putaway task`.

---

## Task I3. ConfirmPutaway (done 2026-09-19, 074322a)

**Files:** `contracts/commands/ConfirmPutaway/v1.json` + fixture; `ConfirmPutawayHandler.cs`; register; tests in `InboundCommandTests.cs`; gen:commands.

**Interfaces:**

```
ConfirmPutaway v1
  task_id, location_id, qty
  new_hu_id?
```

`qty` decimal string. Lite: must equal HU content qty_base.

Rules:

- Unknown task or type not `putaway` → `unknown_task`.
- Task already `done` → `invalid_status`.
- Missing `inbound.putaway` → `forbidden`.
- Unknown or inactive location → `unknown_location`.
- HU not at RECEIVING (balance missing or zero) → `hu_moved`.
- `qty` ≠ content qty_base → `qty_mismatch` (partial is later; this is impossible for lite whole-HU).
- Apply: movement putaway RECEIVING → location_id; decrement RECEIVING balance to 0 (delete or set 0 — delete the row when qty hits 0); upsert balance at destination; HU stays same id; task `status=done`; delete reservation on suggested (and on destination if different); `change_log` HU, balances, task.
- If `location_id` ≠ `task.suggested_location_id` (and suggested is not null): still apply, insert `Deviation(kind=policy_override, command_id, location_id, handling_unit_id, article_id, qty_base)`.
- Idempotent retry.

- [x] **Step 1:** Write `ConfirmPutaway_HuAlreadyMoved_Rejected`. Run; fail.

- [x] **Step 2:** Schema, handler, register.

- [x] **Step 3:** `ConfirmPutaway_HappyPath_MovesToBin`; `ConfirmPutaway_WrongBin_PolicyOverride`; `ConfirmPutaway_UnknownLocation_Rejected`; `ConfirmPutaway_IdempotentRetry`.

- [x] **Step 4:** Break override (reject wrong bin); confirm policy test fails; restore.

**Tests:** those five names.

**Commit:** `sp3: I3 - ConfirmPutaway moves HU and records override`.

---

## Task I4. Floor receive and putaway (done 2026-09-19, e4a73b7)

**Files:** `ReceiveView.vue`, `TaskDetailView.vue`, `router.ts`, `floor.ts` enqueue, `TaskListView.vue` link, i18n sv/en.

**Interfaces:**

- Consumes: Dexie articles (C3), locations, `ReceiveHandlingUnit` / `ConfirmPutaway` outbox writers.
- Produces: `/receive` page; putaway task detail confirms to suggested bin.

i18n:

```
floor.receive: Ta emot / Receive
floor.receiveTitle: Ta emot / Receive
floor.receiveQty: Antal / Quantity
floor.receiveSubmit: Ta emot / Receive
floor.receiveLpn: Kollinummer / LPN
putaway.confirm: Bekräfta inlagring / Confirm putaway
putaway.suggested: Föreslagen plats / Suggested location
```

Receive: article `<select>` from Dexie articles, qty default `1`, submit mints HU id, task id, LPN `LPN-` + first 8 hex of HU id. Enqueue `ReceiveHandlingUnit` and optimistic task+HU in **one Dexie transaction** with the outbox (new helper or extend `enqueueWithTask`). Then go to `/tasks/:id`.

Putaway detail: if `task.type === 'putaway'`, show suggested location code (from locations table), claim if open, confirm sends `ConfirmPutaway` with `location_id` = suggested (or first mapped bin if null) and `qty` from the line. Do not send `CompleteTask` for putaway. Other task types keep CompleteTask.

- [x] **Step 1:** No page unit test. Implement.

- [x] **Step 2:** `pnpm -C frontend lint ; pnpm -C frontend typecheck`.

**Tests:** Playwright in I5.

**Commit:** `sp3: I4 - floor receive and putaway confirm`.

---

## Task I5. Snapshot + Dexie + Playwright

**Files:** `InternalApi.cs` snapshot branches; `db.ts` v4; `floor.ts` `loadSnapshot`; `floor.snapshotStock.test.ts` optional; `frontend/apps/floor/e2e/receive-putaway.spec.ts`.

Snapshot: `entity=HandlingUnit` (content nested, warehouse filter); `entity=StockBalance` (warehouse via join on location). Dexie v4 tables `handling_units`, `stock_balances`. `Promise.all` extra GETs in `loadSnapshot`. Replace whole tables for that warehouse.

Playwright (floor app, mock sync like existing floor e2e): login/enroll as the existing specs do, or mock enough to open `/receive` if that is smaller — follow `frontend/apps/floor/e2e/` pattern. Flow: receive KAFFE-500 qty 1 → list has putaway task → confirm. If enroll is too heavy, a focused test that the receive heading `/ta emot|receive/i` renders after mock session is acceptable **plus** keep I2/I3 as the ledger proof. Prefer a mocked command POST that 200s.

- [ ] **Step 1:** Snapshot tests in `InboundCommandTests`: `Snapshot_EntityHandlingUnit_ReturnsLpn`. Fail; implement.

- [ ] **Step 2:** Dexie + loadSnapshot. `pnpm -C frontend test`.

- [ ] **Step 3:** Playwright spec. Do not weaken `/ta emot|receive/i`.

**Tests:** snapshot integration + Playwright.

**Commit:** `sp3: I5 - snapshot stock and Playwright receive`.

---

## Order

I1 → I2 → I3 → I4 → I5.

## Definition of done

On Aspire: map one aisle, create article `KAFFE-500`, on the floor app **Ta emot** qty 1, see LPN, putaway task suggests a bin, confirm, balance is at that bin not RECEIVING. `GET /internal/snapshot?entity=StockBalance` shows the HU at the bin. No ASN, no strategy plugin, no labels.

## Self-review

- Spec lite (receive one HU, putaway task, ConfirmPutaway, first-empty-bin suggestion, on-screen LPN): I2–I4.
- Ledger + balances + reservation: I1 + I2/I3.
- Floor is the truth (wrong bin = policy_override): I3.
- Client-generated ids: I2 payload + I4 mint.
- Snapshot for later outbound: I5.
- Not in lite: listed under Out of this plan.
