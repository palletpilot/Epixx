# Sub-project 4 lite: Outbound. Implementation plan

> **For agentic workers:** implement one task at a time with `.cursor/skills/implement-plan-task/SKILL.md`. Superpowers `executing-plans` also fits. Steps use checkbox (`- [ ]`) syntax for tracking.

Spec: [2026-09-05-lagerkraft-architecture-design.md](../specs/2026-09-05-lagerkraft-architecture-design.md) (Scope decomposition item 4 **Lite**, Outbound model, StockMovement ledger, floor is the truth, ConfirmPick, Permission model). Branch: `lagerkraft/sp4-outbound` from `lagerkraft/sp3-inbound`. Every task ends in a commit; every non-trivial task ends in a test that fails if the logic breaks. If a task needs something the spec is silent on, stop and propose a spec change.

**Goal:** Office creates one manual order, allocation fills a pick task from stock that inbound put away, the floor confirms the pick onto a tote, and ship-as-picked closes the order.

**Architecture:** `Lagerkraft.WmsCore.Outbound` owns order/shipment contracts. `TenantDbContext` in wms-core Api stays the only writer. Order create is office REST (like articles). Pick and ship-as-picked are floor commands through the existing pipeline. `change_log` entities `order`, `stock_balance`, `task`, `handling_unit`. Allocation is FIFO by `HandlingUnit.received_at` in the handler; no strategy interface yet.

**Tech stack:** .NET 10, EF Core + Npgsql, Vue 3, Dexie, Tailwind in `packages/ui`. Quantities `NUMERIC(18,6)` as decimal strings on the wire. No new UI library.

## Global constraints

- Spec wins. Client-generated UUIDv7 for order, order line, tote HU, shipment. Floor is the truth: short pick at the bin is `Deviation(kind=short_pick)` plus re-allocate, not a silent skip. Rejections are impossible states only. One tenant prefix.
- Never edit `Epixx/`, `Epixx.slnx`, or the root `WarehouseController.cs`.
- Swedish UI strings in `frontend/packages/i18n` only. No emojis.
- Commits: `sp4: <task id> - <what>`.
- Verify: `dotnet build backend/Lagerkraft.sln -warnaserror`; `dotnet test backend/Lagerkraft.sln --filter "Category!=Load"` for backend tasks; `pnpm -C frontend lint ; pnpm -C frontend typecheck ; pnpm -C frontend test` when the frontend tree changed. If Platform.Tests `UseEnvironment` still fails at HEAD, build WmsCore.Api `-warnaserror` and test WmsCore.Tests + Architecture.Tests instead, and name that in the summary.
- Permissions `orders.manage` / `outbound.pick` / `outbound.ship` already exist. Commands check them at `occurred_at` like `ConfirmPutaway`. Office REST follows warehouse/article (tenant header only).

## Assumptions (spec silent; smallest answers)

1. **No Destination table in lite.** Order carries `destination_name` string. Address/dock wait.
2. **Create is office REST** `POST /orders`, not a command. Body mints order id and line ids. Auto-release + allocate in the same request (no hold).
3. **One line per order in the UI.** API may accept one line. `requested_level_id` defaults to the article rank-1 packaging level. `tolerance_pct` 0.
4. **Allocation** = FIFO `HandlingUnit.received_at` among balances with `qty_base > reserved_qty_base`, location not system, not QUARANTINE, article published. Prefer a whole HU when remaining qty >= HU content. Lite always allocates one whole HU when qty matches that HU; otherwise the largest fitting balance row. Shortage → line `short`, HTTP 200 with `status=short` and no pick task.
5. **One pick task per order.** `type=pick`, one `task_line` (`from_location_id`, `from_handling_unit_id`, `requested_qty_base`). Walk order is that one line. `suggested_location_id` = source bin.
6. **ConfirmPick v1:** `task_id`, `tote_id`, `tote_lpn`, `qty`. Lite: `qty` must equal the line `requested_qty_base` (whole-HU). Moves source HU content onto tote HU at `PICKING` (`reason=pick`). Completes the task and order line. Tote HU is client-generated.
7. **Insufficient source balance** → apply nothing, `Deviation(kind=short_pick)`, reject `short_pick` so the device sees it; re-allocate the line in the same handler if another HU exists, else leave short. (Floor is the truth: we do not invent stock.)
8. **ShipAsPicked v1** (floor command, `outbound.ship`): `order_id`, `tote_id`. Requires pick task `done`, warehouse `pack_step=false` (today's bool; false = optional). Movements tote `PICKING` → null (`reason=ship`). Order `shipped`. Outbox `outbound.shipped`.
9. **Tote LPN** on the device: `TOTE-` + first 8 hex of tote id uppercase.
10. **external_ref** unique per `(warehouse_id, source)`. Lite source always `manual`. Office mints `ORD-` + first 8 hex of order id if omitted.

## Out of this plan

Pick-check, CheckPick, pack station, Destination, waves, zone picking, multi-line orders UI, cancel/edit after pick, voice, FEFO, over-pick, required pack_step.

---

## Files (locked here)

| Path | Role |
|---|---|
| `backend/src/WmsCore/Outbound/Contracts/Dtos.cs` | `OrderDto`, `OrderLineDto`, `ShipmentDto`. |
| `backend/src/WmsCore/Api/Data/Entities.cs` | `OutboundOrder`, `OutboundOrderLine`, `Shipment`, `ShipmentHandlingUnit`. |
| `backend/src/WmsCore/Api/Data/TenantDbContext.cs` | DbSets + indexes. |
| `backend/src/WmsCore/Api/Data/Migrations/` | `OutboundTables` (additive). |
| `backend/src/WmsCore/Api/Outbound/OrderApi.cs` | `GET/POST /orders`. |
| `contracts/commands/ConfirmPick/v1.json` + fixture | Pick schema. |
| `contracts/commands/ShipAsPicked/v1.json` + fixture | Ship schema. |
| `backend/src/WmsCore/Api/Commands/Outbound/ConfirmPickHandler.cs` | Pick onto tote. |
| `backend/src/WmsCore/Api/Commands/Outbound/ShipAsPickedHandler.cs` | Ship tote, close order. |
| `backend/src/WmsCore/Api/Program.cs` | Register handlers + MapOrderApi. |
| `backend/src/WmsCore/Api/Internal/InternalApi.cs` | Snapshot `Order`. |
| `backend/tests/Lagerkraft.WmsCore.Tests/OutboundModuleTests.cs` | Migrate + create/allocate. |
| `backend/tests/Lagerkraft.WmsCore.Tests/OutboundCommandTests.cs` | ConfirmPick + ShipAsPicked. |
| `frontend/packages/domain/src/generated/commands.ts` | `pnpm -C frontend gen:commands`. |
| `frontend/apps/web/src/views/OrdersView.vue` | Create + list. |
| `frontend/apps/web/src/views/AppShell.vue` | Nav **Ordrar**. |
| `frontend/apps/web/src/router.ts` | `/app/orders`. |
| `frontend/packages/i18n/src/locales/{sv,en}.json` | `nav.orders`, `orders.*`, `pick.*`. |
| `frontend/apps/floor/src/views/TaskDetailView.vue` | Pick confirm onto tote. |
| `frontend/apps/floor/src/stores/floor.ts` | Enqueue ConfirmPick / ShipAsPicked. |
| `frontend/apps/web/e2e/order-pick-ship.spec.ts` | Playwright mocked loop (or floor e2e if smaller). |

---

## Task O1. Order tables (done 2026-09-19, 9687498)

**Files:** Outbound csproj + `Contracts/Dtos.cs`; `Entities.cs`, `TenantDbContext.cs`; migration `OutboundTables`; sln + Api + Architecture.Tests ProjectReferences; `OutboundModuleTests.cs`. Also snapshot_schema if the last migration name is asserted (LayoutCommandTests).

**Interfaces:**

- Consumes: tenant migrate used by `InventoryModuleTests`.
- Produces: tables `outbound_order`, `outbound_order_line`, `shipment`, `shipment_handling_unit`.

Entities:

```
OutboundOrder: id, warehouse_id, source, external_ref, destination_name, requested_ship_date null, status, notes null
OutboundOrderLine: id, order_id, article_id, requested_qty_base numeric(18,6), requested_level_id, allocated_qty_base numeric(18,6), tolerance_pct numeric(5,2), status
Shipment: id, order_id, dock_location_id null, carrier_ref null, confirmation_code null, shipped_at
ShipmentHandlingUnit: shipment_id, handling_unit_id  (composite PK)
```

Unique `(warehouse_id, source, external_ref)` on order.

- [x] **Step 1:** Write `OutboundModuleTests.Migrate_CreatesOutboundTables`. After migrate, `to_regclass` for the four table names is not null.

- [x] **Step 2:** Run `dotnet test ... --filter "FullyQualifiedName~Migrate_CreatesOutboundTables"`. Expected: FAIL (no table).

- [x] **Step 3:** Module, entities, DbSets, indexes, `dotnet ef migrations add OutboundTables --project backend/src/WmsCore/Api --output-dir Data/Migrations`. Additive only.

- [x] **Step 4:** Re-run the test. PASS. Update snapshot_schema assertion if it still expects `InventoryTables`.

- [x] **Step 5:** Break (rename a table in the test query); confirm fail; restore.

**Tests:** `Migrate_CreatesOutboundTables`.

**Commit:** `sp4: O1 - order, line and shipment tables`.

---

## Task O2. Create order and allocate (done 2026-09-19, 4d2e60e)

**Files:** `OrderApi.cs`; `Program.cs`; tests in `OutboundModuleTests.cs`.

**Interfaces:**

```
POST /orders
  id, warehouse_id, article_id, qty_base
  external_ref?  destination_name?  requested_level_id?
```

`qty_base` decimal string. Permission: tenant header only (same as POST /articles).

Create rules:

- Unknown warehouse / article → 400.
- Duplicate `(warehouse_id, source=manual, external_ref)` → 409.
- Insert order `status=released` then allocate. On success: `status=allocated`, line `allocated_qty_base`, pick task + line, `reserved_qty_base` += qty on the chosen balance, `change_log` order + task.
- Shortage: order stays `released`, line `short`, no task, 200 with that body.
- `GET /orders?warehouse=` lists newest first.

- [x] **Step 1:** `CreateOrder_UnknownArticle_400`. Run; fail.

- [x] **Step 2:** OrderApi + allocate helper (same transaction as insert).

- [x] **Step 3:** `CreateOrder_HappyPath_PickTaskFromFifoHu`; `CreateOrder_Shortage_LineShort`; `CreateOrder_DuplicateExternalRef_409`.

- [x] **Step 4:** Break FIFO (always last HU); confirm fail; restore.

**Tests:** those four names.

**Commit:** `sp4: O2 - create order and FIFO allocate pick task`.

---

## Task O3. ConfirmPick

**Files:** `contracts/commands/ConfirmPick/v1.json` + fixture; `ConfirmPickHandler.cs`; register; `OutboundCommandTests.cs`; gen:commands.

**Interfaces:**

```
ConfirmPick v1
  task_id, tote_id, tote_lpn, qty
```

Rules:

- Unknown / not pick task → `unknown_task`.
- Missing `outbound.pick` → `forbidden`.
- `qty` ≠ line requested → `qty_mismatch`.
- Source balance missing or remaining < qty → `short_pick` + Deviation; re-allocate if another HU exists.
- Apply: create tote HU if new; pick movement source bin → PICKING, from source HU to tote; decrement/delete source balance; upsert tote balance at PICKING; clear reservation; task `done`; line `picked`; order `picked`; `change_log` HU, balances, task, order.
- Idempotent retry.

- [ ] **Step 1:** `ConfirmPick_ShortPick_Rejected`. Fail.

- [ ] **Step 2:** Schema, handler, register.

- [ ] **Step 3:** `ConfirmPick_HappyPath_ToteAtPicking`; `ConfirmPick_Viewer_Forbidden`; `ConfirmPick_IdempotentRetry`.

- [ ] **Step 4:** Break (leave stock at source); confirm happy-path fail; restore.

**Tests:** those four names.

**Commit:** `sp4: O3 - ConfirmPick onto tote at PICKING`.

---

## Task O4. ShipAsPicked

**Files:** `contracts/commands/ShipAsPicked/v1.json` + fixture; `ShipAsPickedHandler.cs`; register; tests in `OutboundCommandTests.cs`; gen:commands.

**Interfaces:**

```
ShipAsPicked v1
  order_id, tote_id
```

Rules:

- Unknown order → `unknown_order`.
- Order not `picked` → `invalid_status`.
- `pack_step=true` → `pack_required`.
- Tote not at PICKING → `hu_moved`.
- Missing `outbound.ship` → `forbidden`.
- Apply: shipment + shipment_hu; movement pick reason=ship, from PICKING to null; delete zero balances; order `shipped`; outbox `outbound.shipped`; `change_log` order, balances.
- Idempotent retry.

- [ ] **Step 1:** `ShipAsPicked_OrderNotPicked_Rejected`. Fail.

- [ ] **Step 2:** Schema, handler, register.

- [ ] **Step 3:** `ShipAsPicked_HappyPath_BalanceGone`; `ShipAsPicked_IdempotentRetry`.

- [ ] **Step 4:** Break (leave balance); confirm fail; restore.

**Tests:** those three names.

**Commit:** `sp4: O4 - ShipAsPicked closes the order`.

---

## Task O5. Office orders, floor pick, Playwright

**Files:** `OrdersView.vue`, `AppShell.vue`, `router.ts`, i18n, `TaskDetailView.vue` pick path, `floor.ts` enqueue, snapshot `Order`, Playwright.

Office: select warehouse + article (from existing list endpoints), qty default `1`, submit, list shows status. Nav **Ordrar**.

Floor: if `task.type === 'pick'`, show source location code, claim, confirm sends `ConfirmPick` (mint tote id/LPN) not `CompleteTask`. After pick `done`, a **Skicka** button on the same task (or order list — prefer task detail) sends `ShipAsPicked`.

Snapshot `entity=Order` (lines nested, warehouse filter). Optional Dexie `orders` table v5; skip if office-only list is enough and floor only needs the pick task. Prefer skip Dexie orders in lite (task snapshot already carries the pick).

Playwright (mock like inbound): after mocked session, office create is heavy — floor heading `/plocka|pick/i` on a pick task plus mocked ConfirmPick/ShipAsPicked POSTs is acceptable **plus** O2–O4 as ledger proof. Do not weaken `/plocka|pick/i` if that heading is used; if the task heading stays the type string `pick`, assert that plus the confirm button `/plocka|pick/i`.

- [ ] **Step 1:** i18n + OrdersView + nav. `pnpm -C frontend lint ; typecheck`.

- [ ] **Step 2:** Floor pick/ship enqueue.

- [ ] **Step 3:** Playwright spec.

**Tests:** Playwright.

**Commit:** `sp4: O5 - office order and floor pick-ship`.

---

## Order

O1 → O2 → O3 → O4 → O5.

## Definition of done

On Aspire: article `KAFFE-500` in a bin (inbound loop), office order qty 1, floor pick onto a tote, ship-as-picked, `GET /internal/snapshot?entity=StockBalance` no longer shows that HU, order `shipped`. No pick-check, no pack station, no Destination.

## Self-review

- Spec lite (one manual order, allocate, pick, ship-as-picked): O2–O5.
- FIFO allocate: O2.
- Tote HU client id: O3 + O5.
- Floor is the truth (short_pick): O3.
- Ship movements to null: O4.
- Not in lite: listed under Out of this plan.
- `Warehouse.pack_step` stays bool (`false` = optional). Spec string enum is a later spec-change, not this plan.
