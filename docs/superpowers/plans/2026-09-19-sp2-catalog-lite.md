# Sub-project 2 lite: Catalog. Implementation plan

> **For agentic workers:** implement one task at a time with `.cursor/skills/implement-plan-task/SKILL.md`. Superpowers `subagent-driven-development` or `executing-plans` also fit. Steps use checkbox (`- [ ]`) syntax for tracking.

Spec: [2026-09-05-lagerkraft-architecture-design.md](../specs/2026-09-05-lagerkraft-architecture-design.md) (Scope decomposition item 2 **Lite**, Units of measure, PackagingLevel, Permission model). Branch: `lagerkraft/sp2-catalog` from current `lagerkraft/sp1-layout`. Every task ends in a commit; every non-trivial task ends in a test that fails if the logic breaks. If a task needs something the spec is silent on, stop and propose a spec change.

**Goal:** A founder creates an article (SKU, name, unit) in the back office and it is on the tenant snapshot for the floor, so inbound can receive it next.

**Architecture:** `Lagerkraft.WmsCore.Catalog` owns article, packaging-level and unit-of-measure contracts. `TenantDbContext` in wms-core Api stays the only writer. Create is office REST (same shape as `POST /warehouses`), not a floor command. `change_log` entity `article` so SSE and snapshot stay one path. Articles are tenant-wide (no warehouse filter on snapshot).

**Tech stack:** .NET 10, EF Core + Npgsql, Vue 3, Dexie, Tailwind in `packages/ui`. Quantities `NUMERIC(18,6)` as decimal strings on the wire. No new UI library.

## Global constraints

- Spec wins. Client-generated UUIDv7 for article and packaging-level ids (office mints them, same as warehouse create). Floor is the truth (not in play until inbound). One tenant prefix.
- Never edit `Epixx/`, `Epixx.slnx`, or the root `WarehouseController.cs`.
- Swedish UI strings in `frontend/packages/i18n` only. No emojis.
- Commits: `sp2: <task id> - <what>`.
- Verify: `dotnet build backend/Lagerkraft.sln -warnaserror`; `dotnet test backend/Lagerkraft.sln --filter "Category!=Load"` for backend tasks; `pnpm -C frontend lint && pnpm -C frontend typecheck && pnpm -C frontend test` when the frontend tree changed.
- Permissions `catalog.read` / `catalog.write` already exist. wms-core public REST today keys only on `Lagerkraft-Tenant-Id` (warehouse create). Catalog REST follows that; do not invent JWT on wms-core in this plan.

## Assumptions (spec silent; smallest answers)

1. **REST, not a command.** Office configure matches warehouse create. Inbound's "receive as new article" is a later command.
2. **Precision / step from UoM dimension.** `count` → `quantity_precision=0`, `quantity_step=1`. Any other dimension → `3` and `0.001`.
3. **Volume base is litre.** `m3.factor_to_dimension_base = 1000`. Mass base kg, length m, area m2, count 1, as the spec's `g = 0.001` to kg example.
4. **One packaging level** = insert `rank=1`, `qty_in_base=1`, `name` = UoM `display_name_sv`. No second level in the UI.
5. **Default unit** is `st`. Status `published`. `allow_loose_pick=true`. `catch_weight=false`.
6. **Artiklar** is a primary nav item (Lager / Artiklar / Uppgifter). Devices, users, SSO stay muted.

## Out of this plan

Attribute engine, CSV import, PalletType, pick-instruction UI, GTIN UI, CreateArticle command, inbound receive, labels, extra packaging levels, rebase of `base_uom_id`.

---

## Files (locked here)

| Path | Role |
|---|---|
| `backend/src/WmsCore/Catalog/Lagerkraft.WmsCore.Catalog.csproj` | Module assembly (same shape as Layout). |
| `backend/src/WmsCore/Catalog/Contracts/Dtos.cs` | `UnitOfMeasureDto`, `ArticleDto`, `PackagingLevelDto`, `CatalogDefaults` (seed rows + well-known ids). |
| `backend/src/WmsCore/Api/Data/Entities.cs` | `UnitOfMeasure`, `Article`, `PackagingLevel`. |
| `backend/src/WmsCore/Api/Data/TenantDbContext.cs` | DbSets + unique sku + UoM seed. |
| `backend/src/WmsCore/Api/Data/Migrations/` | `CatalogTables` (additive). |
| `backend/src/WmsCore/Api/Catalog/ArticleApi.cs` | `GET /units`, `GET /articles`, `POST /articles`. |
| `backend/src/WmsCore/Api/Internal/InternalApi.cs` | Snapshot `entity=Article` and `entity=UnitOfMeasure`. |
| `backend/src/WmsCore/Api/Program.cs` | `MapArticleApi()`. |
| `backend/Lagerkraft.sln` | Catalog project. |
| `backend/src/WmsCore/Api/Lagerkraft.WmsCore.Api.csproj` | ProjectReference. |
| `backend/tests/Lagerkraft.Architecture.Tests/Lagerkraft.Architecture.Tests.csproj` | ProjectReference so C3 cannot skip the module. |
| `backend/tests/Lagerkraft.WmsCore.Tests/CatalogModuleTests.cs` | Integration tests. |
| `frontend/apps/web/src/views/ArticlesView.vue` | List + create. |
| `frontend/apps/web/src/views/AppShell.vue` | Nav **Artiklar**. |
| `frontend/apps/web/src/router.ts` | `/app/articles`. |
| `frontend/packages/i18n/src/locales/{sv,en}.json` | `nav.articles`, `articles.*`. |
| `frontend/apps/floor/src/db.ts` | Dexie `articles` + `units`. |
| `frontend/apps/floor/src/stores/floor.ts` | Snapshot `entity=Article` and `entity=UnitOfMeasure`. |
| `frontend/apps/web/e2e/article-create.spec.ts` | Playwright list after create. |

---

## Task C1. Catalog module, UoM seed, Article and PackagingLevel tables (done 2026-09-19, 92f0fa3)

**Files:** create Catalog csproj + `Contracts/Dtos.cs`; modify `Entities.cs`, `TenantDbContext.cs`; new migration `CatalogTables`; sln + Api + Architecture.Tests ProjectReferences; test `CatalogModuleTests.cs`.

**Interfaces:**

- Consumes: tenant migrate endpoint already used by `LayoutModuleTests`.
- Produces: `CatalogDefaults.Units` (9 rows), `CatalogDefaults.StId`, tables `unit_of_measure`, `article`, `packaging_level`.

`CatalogDefaults` well-known ids (UUIDv7-shaped, fixed):

```
st  01900000-0000-7000-8000-000000000101  count  1
kg  01900000-0000-7000-8000-000000000102  mass   1
g   01900000-0000-7000-8000-000000000103  mass   0.001
l   01900000-0000-7000-8000-000000000104  volume 1
ml  01900000-0000-7000-8000-000000000105  volume 0.001
m   01900000-0000-7000-8000-000000000106  length 1
cm  01900000-0000-7000-8000-000000000107  length 0.01
m2  01900000-0000-7000-8000-000000000108  area   1
m3  01900000-0000-7000-8000-000000000109  volume 1000
```

`display_name_sv` / `display_name_en`: st/st, kg/kg, g/g, l/l, ml/ml, m/m, cm/cm, m²/m², m³/m³.

Entities (columns the spec lists even if the lite UI ignores them — expand now so inbound/outbound do not migrate the hottest table):

```
UnitOfMeasure: id, code, dimension, factor_to_dimension_base numeric(18,6), display_name_sv, display_name_en
Article: id, sku, name, gtin null, status, base_uom_id, quantity_precision, quantity_step numeric(18,6),
         allow_loose_pick, order_multiple_level_id null, weight_per_base_unit_g null,
         pick_instruction null, catch_weight, catch_weight_uom_id null
PackagingLevel: id, article_id, rank, name, qty_in_base numeric(18,6),
                height_mm, width_mm, depth_mm, gross_weight_g, barcode null,
                pallet_type_id null, is_breakable, is_default_receiving, is_default_shipping
```

Unique `(sku)` on article. Unique `(article_id, rank)` on packaging_level. FK `base_uom_id` → `unit_of_measure`. Seed UoM in `OnModelCreating` `HasData` so the migration inserts them.

- [x] **Step 1:** Write `CatalogModuleTests.Migrate_SeedsNineUnits`. Copy the `LayoutModuleTests` fixture (`PostgresCollection`, `TenantDatabases.CreateAsync`, `POST /internal/tenants/{id}/migrate`). After migrate, query `unit_of_measure` (Npgsql, same as `LoadSystemCodes`) and assert the nine codes.

- [x] **Step 2:** Run `dotnet test backend/tests/Lagerkraft.WmsCore.Tests/Lagerkraft.WmsCore.Tests.csproj --filter "FullyQualifiedName~Migrate_SeedsNineUnits"`. Expected: FAIL (no table).

- [x] **Step 3:** Add the Catalog project (copy Layout csproj, RootNamespace `Lagerkraft.WmsCore.Catalog`). `dotnet sln backend/Lagerkraft.sln add backend/src/WmsCore/Catalog/Lagerkraft.WmsCore.Catalog.csproj`. ProjectReference from Api and Architecture.Tests. Entities, DbSets, HasData, migration:

```
dotnet ef migrations add CatalogTables --project backend/src/WmsCore/Api --output-dir Data/Migrations
dotnet ef migrations has-pending-model-changes --project backend/src/WmsCore/Api
```

Must print none. Additive only (new tables).

- [x] **Step 4:** Re-run the test. Expected: PASS. Architecture `WmsCoreModules_CrossModule_OnlyThroughContracts` still passes.

- [x] **Step 5:** Break the seed (drop `st`); confirm the test fails; restore.

**Tests:** `Migrate_SeedsNineUnits`.

**Commit:** `sp2: C1 - catalog module, units seed, article tables`.

---

## Task C2. POST /articles, GET /articles, GET /units (done 2026-09-19, 6bfa351)

**Files:** create `backend/src/WmsCore/Api/Catalog/ArticleApi.cs`; modify `Program.cs`; tests in `CatalogModuleTests.cs`; snapshot branch in `InternalApi.cs` `GetSnapshot`.

**Interfaces:**

- Consumes: `CatalogDefaults.StId`, seeded units from C1.
- Produces:

```
GET  /units     -> UnitOfMeasureDto[]
GET  /articles  -> ArticleDto[]
POST /articles  -> 201 ArticleDto
```

```csharp
public sealed record UnitOfMeasureDto(Guid Id, string Code, string Dimension, string FactorToDimensionBase, string DisplayNameSv, string DisplayNameEn);

public sealed record PackagingLevelDto(Guid Id, Guid ArticleId, int Rank, string Name, string QtyInBase);

public sealed record ArticleDto(
    Guid Id, string Sku, string Name, string Status, Guid BaseUomId,
    int QuantityPrecision, string QuantityStep, bool AllowLoosePick,
    PackagingLevelDto[] PackagingLevels);

public sealed record CreateArticleRequest(
    Guid Id, string Sku, string Name, Guid? BaseUomId, Guid? PackagingLevelId);
```

JSON snake_case (existing `JsonNamingPolicy.SnakeCaseLower` on the test client). `qty_in_base` and `quantity_step` are decimal **strings**.

Create rules:

- `id` required (client). Empty → 400.
- `sku` trim, 1–64 chars, unique → duplicate 409 `{ error: "duplicate_sku" }`.
- `name` trim, 1–200 chars.
- `base_uom_id` default `CatalogDefaults.StId`. Unknown → 400 `{ error: "unknown_uom" }`.
- Insert article: `status=published`, `catch_weight=false`, `allow_loose_pick=true`, precision/step from assumption 2.
- Insert one packaging level: `id` = `packaging_level_id` or a new UUIDv7, `rank=1`, `qty_in_base=1`, `name` = unit `display_name_sv`, `is_breakable=true`, `is_default_receiving=true`, `is_default_shipping=true`.
- Write `change_log` `entity=article`, `op=upsert`, payload = `ArticleDto` JSON (levels nested). `occurred_at`/`recorded_at` = clock now. No `command_id`.
- Return 201 with the dto.

Snapshot: `entity=Article` lists all articles with levels (ignore `warehouse`). `entity=UnitOfMeasure` lists units. Do not change the Task default.

- [x] **Step 1:** Write `CreateArticle_UnknownUom_400` first (must not insert). Run; fail.

- [x] **Step 2:** Implement `ArticleApi` + `MapArticleApi()`. Register in `ConfigureApp` next to `MapWarehouseApi()`.

- [x] **Step 3:** Remaining tests: `CreateArticle_SeedsOnePackagingLevelQtyOne` (sku `KAFFE-500`, name `Kaffe`, default st → one level `qty_in_base` `"1.000000"` or `"1"`, compare as decimal); `CreateArticle_DuplicateSku_409`; `ListArticles_ReturnsCreated`; `GetUnits_ReturnsSt`; `Snapshot_EntityArticle_ReturnsSkuAndLevel` (`GET /internal/snapshot?tenantId=&entity=Article`).

- [x] **Step 4:** Break uniqueness (allow second insert); confirm duplicate test fails; restore.

**Tests:** those six names.

**Commit:** `sp2: C2 - create article with one packaging level`.

---

## Task C3. Floor snapshot stores articles and units (done 2026-09-19, 5122228)

**Files:** `frontend/apps/floor/src/db.ts`, `frontend/apps/floor/src/stores/floor.ts`. Optional small Vitest if `loadSnapshot` is already tested; otherwise the C2 snapshot integration test is the backend proof and C5 covers the office. Add a focused store test only if one already exists for locations — there is `warehousePick.test.ts`; add `floor.snapshotArticles.test.ts` that mocks `authedFetch` and asserts Dexie rows. If Dexie in Vitest is painful, skip the unit test and keep C5 + C2.

**Interfaces:**

- Consumes: snapshot JSON from C2 (`items[].sku`, nested `packaging_levels`).
- Produces: Dexie tables `articles`, `units`. Bump Dexie version (locations used v2; this is v3).

```ts
export type ArticleRow = {
  id: string;
  sku: string;
  name: string;
  status: string;
  base_uom_id: string;
  quantity_precision: number;
  quantity_step: string;
  packaging_levels: { id: string; rank: number; name: string; qty_in_base: string }[];
};
```

`loadSnapshot` also fetches `entity=Article` and `entity=UnitOfMeasure` (warehouse query param may be sent; server ignores it). Same pattern as Location: `Promise.all` extra GETs.

- [x] **Step 1:** If adding a unit test, write it first so empty Dexie fails. Otherwise go to step 3.

- [x] **Step 2:** Run it; fail.

- [x] **Step 3:** Dexie v3 + fetch in `loadSnapshot`.

- [x] **Step 4:** `pnpm -C frontend test` green.

**Tests:** `floor.snapshotArticles.test.ts` (optional); C2 snapshot integration remains required.

**Commit:** `sp2: C3 - floor snapshot articles and units`.

---

## Task C4. Office Artiklar page (done 2026-09-19, PENDING)

**Files:** `ArticlesView.vue`, `AppShell.vue`, `router.ts`, `sv.json`, `en.json`.

**Interfaces:**

- Consumes: `GET /units`, `GET /articles`, `POST /articles` with `Lagerkraft-Tenant-Id` (copy `tenant.createWarehouse`).
- Produces: page at `/app/articles`.

i18n keys:

```
nav.articles: Artiklar / Articles
articles.title: Artiklar / Articles
articles.sku: Artikelnummer / SKU
articles.name: Namn / Name
articles.unit: Enhet / Unit
articles.create: Skapa artikel / Create article
articles.empty: Inga artiklar ännu. / No articles yet.
articles.emptyAction: Skapa en artikel. Nästa steg är att ta emot den på golvet. / Create an article. Next you receive it on the floor app.
articles.list: Artikellista / Article list
articles.error: Kunde inte skapa artikeln. / Could not create the article.
```

UI: `h1` = `articles.title` (Playwright can match `/artikel|articles/i`). EmptyState + form (sku, name, unit `<select>` of GET /units, default `st`). Submit mints two UUIDs, POSTs, prepends to the list. List shows `sku` and `name`. Primary orange button already comes from tokens. No Mer, no GTIN.

Nav: `RouterLink` to `/app/articles` with the same `navClass` as Lager/Uppgifter, **before** Uppgifter.

- [ ] **Step 1:** No page unit test (frontend-vue: a page is Playwright). Implement the view.

- [ ] **Step 2:** `pnpm -C frontend lint && pnpm -C frontend typecheck`.

- [ ] **Step 3:** Browser: login, Artiklar, create `KAFFE-500` / `Kaffe`, row appears. (C5 automates this.)

**Tests:** Playwright in C5.

**Commit:** `sp2: C4 - office articles list and create`.

---

## Task C5. Playwright: create article, list shows it

**Files:** `frontend/apps/web/e2e/article-create.spec.ts`.

Mock platform `/me` and login like `signup-to-tasks.spec.ts`. Mock wms `GET /units` with `st`, `GET /articles` empty then after POST return the created row, `POST /articles` 201. Flow: login → `/app/articles` → fill sku + name → Skapa artikel → list has `KAFFE-500`. Does not replace C2 integration tests.

- [ ] **Step 1:** Write the spec. Run `pnpm -C frontend exec playwright test --config apps/web/playwright.config.ts`. Expected: FAIL (no /articles route or copy).

- [ ] **Step 2:** C4 already added the page; the spec should pass. If selectors miss, fix the spec or the labels — do not weaken `/artikel|articles/i`.

- [ ] **Step 3:** Confirm `signup-to-tasks` still green.

**Tests:** `article-create.spec.ts`.

**Commit:** `sp2: C5 - Playwright create article`.

---

## Order

C1 → C2 → C3 → C4 → C5. C3 and C4 can start in parallel after C2. C5 needs C4.

## Definition of done

On Aspire: log in, open **Artiklar**, create an article with SKU + name + unit `st`, see it in the list. `GET /sync/snapshot?entity=Article` returns that sku and one packaging level with `qty_in_base` 1. Browser check of that path. Playwright green. No CSV, no attributes, no inbound.

---

## Self-review

- Spec lite (sku, name, one packaging level, seeded units): C1 seed, C2 create+level, C4 form.
- Spec units table + Article columns including catch-weight placeholders: C1 entities.
- Tenant-wide catalog / snapshot: C2 snapshot ignores warehouse.
- Client-generated ids: C2 + C4 mint UUIDs.
- Floor snapshot for later inbound: C3.
- Sales UX strings in i18n, no second library, secondary nav stays muted: C4.
- Not in lite (attributes, CSV, PalletType, pick-instruction UI, commands): listed under Out of this plan.
