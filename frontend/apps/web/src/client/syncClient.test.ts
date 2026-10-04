import { describe, expect, it } from "vitest";
import {
  applyChanges,
  emptyLists,
  listsFromSnapshots,
  loadOfficeLists,
  mapChangeRows,
  mapDeviationSnapshot,
  mapStockSnapshot,
  type ChangeRow,
  type StockRow,
} from "./syncClient";

const warehouseId = "wh-1";
const otherWarehouse = "wh-2";

function stockSnapshotItem(overrides: Partial<StockRow> = {}): StockRow {
  return {
    id: "s1",
    warehouse_id: warehouseId,
    location_id: "A-01-01",
    handling_unit_id: "HU-001",
    article_id: null,
    qty_base: 4,
    created_at: "2026-09-13T08:00:00.000Z",
    ...overrides,
  };
}

function change(partial: Omit<ChangeRow, "occurred_at"> & { occurred_at?: string }): ChangeRow {
  return {
    occurred_at: "2026-09-13T08:00:01.000Z",
    recorded_at: "2026-09-13T08:00:01.100Z",
    actor: null,
    ...partial,
  };
}

describe("snapshot mapping", () => {
  it("maps stock items and keeps a null article_id", () => {
    const rows = mapStockSnapshot({
      snapshot_schema: "ignore-me",
      items: [stockSnapshotItem({ article_id: null, qty_base: 12.5 })],
    });
    expect(rows).toEqual([stockSnapshotItem({ article_id: null, qty_base: 12.5 })]);
  });

  it("maps an empty warehouse to an empty list and ignores snapshot_schema", () => {
    const lists = listsFromSnapshots({ snapshot_schema: "2", items: [] }, { snapshot_schema: "2", items: [] });
    expect(lists.stock.size).toBe(0);
    expect(lists.deviations.size).toBe(0);
  });

  it("maps a warehouse-scoped occupied_bin deviation", () => {
    const rows = mapDeviationSnapshot({
      items: [
        {
          warehouse_id: warehouseId,
          id: "d1",
          kind: "occupied_bin",
          command_id: "cmd-1",
          detail: {
            location_id: "A-01-01",
            existing_handling_unit_id: "HU-OLD",
            incoming_handling_unit_id: "HU-NEW",
            lpn: "LPN-1",
          },
          created_at: "2026-09-13T08:00:00.000Z",
        },
      ],
    });
    expect(rows).toHaveLength(1);
    expect(rows[0]?.kind).toBe("occupied_bin");
    expect(rows[0]?.detail.lpn).toBe("LPN-1");
  });
});

describe("change mapping", () => {
  it("reads change rows and treats an omitted article_id as null", () => {
    const rows = mapChangeRows({
      entries: [
        {
          seq: 3,
          entity: "stock",
          id: "s2",
          op: "upsert",
          payload: {
            id: "s2",
            warehouse_id: warehouseId,
            location_id: "B-01-01",
            handling_unit_id: "HU-002",
            qty_base: 2,
            created_at: "2026-09-13T08:00:02.000Z",
          },
          command_id: "cmd-2",
          actor: "anna",
          occurred_at: "2026-09-13T08:00:02.000Z",
          recorded_at: "2026-09-13T08:00:02.100Z",
          warehouse_id: warehouseId,
        },
      ],
    });
    expect(rows).toHaveLength(1);
    expect(rows[0]?.warehouse_id).toBe(warehouseId);
    const applied = applyChanges(emptyLists(), rows, warehouseId);
    expect(applied.stock.get("s2")?.article_id).toBeNull();
    expect(applied.stock.get("s2")?.qty_base).toBe(2);
  });
});

describe("applyChanges", () => {
  it("leaves a change without warehouse_id off, even when the payload has one", () => {
    const start = listsFromSnapshots({ items: [stockSnapshotItem()] }, { items: [] });
    const next = applyChanges(
      start,
      [
        change({
          seq: 1,
          entity: "stock",
          id: "s1",
          op: "delete",
          payload: { warehouse_id: warehouseId, id: "s1" },
        }),
        change({
          seq: 2,
          entity: "stock",
          id: "s-new",
          op: "upsert",
          warehouse_id: otherWarehouse,
          payload: {
            id: "s-new",
            warehouse_id: otherWarehouse,
            location_id: "Z-01-01",
            handling_unit_id: "HU-Z",
            qty_base: 9,
            created_at: "2026-09-13T08:00:01.000Z",
          },
        }),
      ],
      warehouseId,
    );
    expect(next.stock.get("s1")?.handling_unit_id).toBe("HU-001");
    expect(next.stock.has("s-new")).toBe(false);
  });

  it("keeps an empty warehouse empty when there are no matching changes", () => {
    const next = applyChanges(emptyLists(), [], warehouseId);
    expect(next.stock.size).toBe(0);
    expect(next.deviations.size).toBe(0);
  });

  it("applies occupied-bin delete, upsert, and deviation insert with the same command_id", () => {
    const start = listsFromSnapshots({ items: [stockSnapshotItem({ id: "s-old", handling_unit_id: "HU-OLD" })] }, { items: [] });
    const commandId = "cmd-occ-1";
    const next = applyChanges(
      start,
      [
        change({
          seq: 10,
          entity: "stock",
          id: "s-old",
          op: "delete",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            warehouse_id: warehouseId,
            id: "s-old",
            location_id: "A-01-01",
            handling_unit_id: "HU-OLD",
            qty_base: 4,
            created_at: "2026-09-13T08:00:00.000Z",
          },
        }),
        change({
          seq: 11,
          entity: "stock",
          id: "s-new",
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            id: "s-new",
            warehouse_id: warehouseId,
            location_id: "A-01-01",
            handling_unit_id: "HU-NEW",
            qty_base: 7,
            created_at: "2026-09-13T08:00:01.000Z",
          },
        }),
        change({
          seq: 12,
          entity: "deviation",
          id: "d-new",
          op: "insert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            warehouse_id: warehouseId,
            id: "d-new",
            kind: "occupied_bin",
            command_id: commandId,
            detail: {
              location_id: "A-01-01",
              existing_handling_unit_id: "HU-OLD",
              incoming_handling_unit_id: "HU-NEW",
              lpn: "LPN-9",
            },
            created_at: "2026-09-13T08:00:01.000Z",
          },
        }),
      ],
      warehouseId,
    );

    expect(next.stock.size).toBe(1);
    expect(next.stock.has("s-old")).toBe(false);
    expect(next.stock.get("s-new")).toMatchObject({
      handling_unit_id: "HU-NEW",
      article_id: null,
      qty_base: 7,
    });
    expect(next.deviations.size).toBe(1);
    expect(next.deviations.get("d-new")).toMatchObject({
      kind: "occupied_bin",
      command_id: commandId,
      warehouse_id: warehouseId,
      detail: {
        location_id: "A-01-01",
        existing_handling_unit_id: "HU-OLD",
        incoming_handling_unit_id: "HU-NEW",
        lpn: "LPN-9",
      },
    });
  });
});

describe("loadOfficeLists", () => {
  it("renders an empty warehouse from snapshot and leaves changes without warehouse_id off", async () => {
    const called: string[] = [];
    const lists = await loadOfficeLists(
      async (url) => {
        called.push(url);
        if (url.includes("/sync/snapshot")) {
          return { snapshot_schema: "do-not-use", items: [] };
        }
        return {
          entries: [
            {
              seq: 1,
              entity: "stock",
              id: "s-guess",
              op: "upsert",
              payload: {
                id: "s-guess",
                warehouse_id: warehouseId,
                location_id: "A-01-01",
                handling_unit_id: "HU-001",
                qty_base: 1,
                created_at: "2026-09-13T08:00:00.000Z",
              },
              command_id: "cmd-x",
              actor: null,
              occurred_at: "2026-09-13T08:00:00.000Z",
              recorded_at: "2026-09-13T08:00:00.100Z",
            },
          ],
        };
      },
      "http://sync.test",
      warehouseId,
    );
    expect(called.some((url) => url.includes("/sync/snapshot") && url.includes("entity=stock"))).toBe(true);
    expect(called.some((url) => url.includes("/sync/snapshot") && url.includes("entity=deviation"))).toBe(true);
    expect(called.some((url) => url.includes("/sync/changes") && url.includes(`warehouse=${warehouseId}`))).toBe(true);
    expect(lists.stock.size).toBe(0);
    expect(lists.deviations.size).toBe(0);
  });
});
