import { describe, expect, it } from "vitest";
import {
  applyChanges,
  asDecimalString,
  changeMatchesSelectedWarehouse,
  deviationLocationId,
  compareDecimalStrings,
  emptyLists,
  formatDecimalSvSe,
  listsFromStockSnapshot,
  loadOfficeLists,
  mapChangeRows,
  mapStockSnapshot,
  SyncLoadError,
  type ChangeRow,
  type StockRow,
} from "./syncClient";

const warehouseId = "wh-1";
const otherWarehouse = "wh-2";
const huId = "hu-1";

function stockSnapshotItem(overrides: Partial<StockRow> = {}): StockRow {
  return {
    location_id: "loc-a",
    article_id: "art-1",
    handling_unit_id: huId,
    qty_base: "4",
    reserved_qty_base: "0",
    ...overrides,
  };
}

function change(partial: Omit<ChangeRow, "occurred_at"> & { occurred_at?: string }): ChangeRow {
  return {
    occurred_at: "2026-09-19T08:00:01.000Z",
    recorded_at: "2026-09-19T08:00:01.100Z",
    actor: null,
    ...partial,
  };
}

describe("snapshot mapping", () => {
  it("maps StockBalance items and keeps qty_base as a decimal string", () => {
    const rows = mapStockSnapshot({
      snapshot_schema: "ignore-me",
      items: [stockSnapshotItem({ qty_base: "12.5", reserved_qty_base: "1.25" })],
    });
    expect(rows).toEqual([stockSnapshotItem({ qty_base: "12.5", reserved_qty_base: "1.25" })]);
  });

  it("maps an empty warehouse to an empty list and ignores snapshot_schema", () => {
    const lists = listsFromStockSnapshot({ snapshot_schema: "2", items: [] });
    expect(lists.stock.size).toBe(0);
    expect(lists.deviations.size).toBe(0);
  });

  it("keeps a quantity that Number() cannot represent exactly", () => {
    const raw = "9007199254740993";
    expect(Number(raw) === Number.parseFloat(raw)).toBe(true);
    expect(String(Number(raw))).not.toBe(raw);
    const rows = mapStockSnapshot({
      items: [stockSnapshotItem({ qty_base: raw, reserved_qty_base: "0.123456" })],
    });
    expect(rows[0]?.qty_base).toBe(raw);
    expect(rows[0]?.reserved_qty_base).toBe("0.123456");
    expect(asDecimalString(Number(raw))).toBeNull();
    expect(formatDecimalSvSe(raw)).toBe("9\u00A0007\u00A0199\u00A0254\u00A0740\u00A0993");
    expect(compareDecimalStrings(raw, "9007199254740992")).toBeGreaterThan(0);
  });
});

describe("change mapping", () => {
  it("reads change rows and keeps qty_base as a string", () => {
    const rows = mapChangeRows({
      entries: [
        {
          seq: 3,
          entity: "stock_balance",
          id: "hu-2",
          op: "upsert",
          payload: {
            location_id: "loc-b",
            article_id: "art-2",
            handling_unit_id: "hu-2",
            qty_base: "2.000000",
            reserved_qty_base: "0",
          },
          command_id: "cmd-2",
          actor: "anna",
          occurred_at: "2026-09-19T08:00:02.000Z",
          recorded_at: "2026-09-19T08:00:02.100Z",
          warehouse_id: warehouseId,
        },
      ],
    });
    expect(rows).toHaveLength(1);
    const applied = applyChanges(emptyLists(), rows, warehouseId);
    expect(applied.stock.get("hu-2")?.qty_base).toBe("2.000000");
    expect(applied.stock.get("hu-2")?.reserved_qty_base).toBe("0");
  });
});

describe("applyChanges", () => {
  it("leaves a change without warehouse_id off, even when the payload has one", () => {
    const start = listsFromStockSnapshot({ items: [stockSnapshotItem()] });
    expect(changeMatchesSelectedWarehouse(change({ seq: 1, entity: "stock_balance", id: huId, op: "delete", payload: {} }), warehouseId)).toBe(
      false,
    );
    const next = applyChanges(
      start,
      [
        change({
          seq: 1,
          entity: "stock_balance",
          id: huId,
          op: "delete",
          payload: { warehouse_id: warehouseId, handling_unit_id: huId },
        }),
        change({
          seq: 2,
          entity: "stock_balance",
          id: "hu-other",
          op: "upsert",
          warehouse_id: otherWarehouse,
          payload: {
            location_id: "loc-z",
            article_id: "art-z",
            handling_unit_id: "hu-other",
            qty_base: "9",
            reserved_qty_base: "0",
          },
        }),
      ],
      warehouseId,
    );
    expect(next.stock.get(huId)?.location_id).toBe("loc-a");
    expect(next.stock.has("hu-other")).toBe(false);
  });

  it("keeps an empty warehouse empty when there are no matching changes", () => {
    const next = applyChanges(emptyLists(), [], warehouseId);
    expect(next.stock.size).toBe(0);
    expect(next.deviations.size).toBe(0);
  });

  it("applies occupied-bin as stock_balance upsert plus deviation upsert under one command_id", () => {
    const start = listsFromStockSnapshot({
      items: [stockSnapshotItem({ location_id: "loc-old", qty_base: "4", reserved_qty_base: "0" })],
    });
    const commandId = "cmd-occ-1";
    const next = applyChanges(
      start,
      [
        change({
          seq: 10,
          entity: "stock_balance",
          id: huId,
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            location_id: "loc-new",
            article_id: "art-1",
            handling_unit_id: huId,
            qty_base: "4",
            reserved_qty_base: "0",
          },
        }),
        change({
          seq: 11,
          entity: "deviation",
          id: "d-new",
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            warehouse_id: warehouseId,
            id: "d-new",
            kind: "occupied_bin",
            command_id: commandId,
            movement_id: "skip-me",
            detail: {
              location_id: "loc-new",
              existing_handling_unit_id: "hu-old",
              incoming_handling_unit_id: huId,
              lpn: "LPN-9",
            },
            created_at: "2026-09-19T08:00:01.000Z",
          },
        }),
        change({
          seq: 12,
          entity: "handling_unit",
          id: huId,
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: { id: huId, warehouse_id: warehouseId, lpn: "LPN-9" },
        }),
        change({
          seq: 13,
          entity: "task",
          id: "task-1",
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: { id: "task-1", warehouse_id: warehouseId, type: "putaway", status: "done" },
        }),
      ],
      warehouseId,
    );

    expect(next.stock.size).toBe(1);
    expect(next.stock.get(huId)).toMatchObject({
      location_id: "loc-new",
      handling_unit_id: huId,
      qty_base: "4",
    });
    expect(next.deviations.size).toBe(1);
    expect(next.deviations.get("d-new")).toMatchObject({
      kind: "occupied_bin",
      command_id: commandId,
      warehouse_id: warehouseId,
      detail: {
        location_id: "loc-new",
        existing_handling_unit_id: "hu-old",
        incoming_handling_unit_id: huId,
        lpn: "LPN-9",
      },
    });
    expect(next.deviations.get("d-new")).not.toHaveProperty("movement_id");
  });

  it("applies a wrong_location deviation with suggested and actual location ids", () => {
    const next = applyChanges(
      emptyLists(),
      [
        change({
          seq: 1,
          entity: "deviation",
          id: "d-wrong",
          op: "upsert",
          warehouse_id: warehouseId,
          payload: {
            warehouse_id: warehouseId,
            id: "d-wrong",
            kind: "wrong_location",
            command_id: "cmd-wrong",
            detail: {
              suggested_location_id: "loc-suggested",
              actual_location_id: "loc-actual",
            },
            created_at: "2026-09-19T08:00:00.000Z",
          },
        }),
      ],
      warehouseId,
    );
    const row = next.deviations.get("d-wrong");
    expect(row?.kind).toBe("wrong_location");
    expect(row?.detail.suggested_location_id).toBe("loc-suggested");
    expect(row?.detail.actual_location_id).toBe("loc-actual");
    expect(deviationLocationId(row!)).toBe("loc-actual");
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
        const since = Number(new URL(url).searchParams.get("since"));
        if (since > 0) {
          return { entries: [] };
        }
        return {
          entries: [
            {
              seq: 1,
              entity: "stock_balance",
              id: "hu-guess",
              op: "upsert",
              payload: {
                location_id: "loc-a",
                article_id: "art-1",
                handling_unit_id: "hu-guess",
                qty_base: "1",
                reserved_qty_base: "0",
                warehouse_id: warehouseId,
              },
              command_id: "cmd-x",
              actor: null,
              occurred_at: "2026-09-19T08:00:00.000Z",
              recorded_at: "2026-09-19T08:00:00.100Z",
            },
          ],
        };
      },
      "http://sync.test",
      warehouseId,
    );
    expect(called.some((url) => url.includes("/sync/snapshot") && url.includes("entity=StockBalance"))).toBe(true);
    expect(called.every((url) => !url.includes("entity=Deviation") && !url.includes("entity=deviation"))).toBe(true);
    expect(called.some((url) => url.includes("/sync/changes") && url.includes(`warehouse=${warehouseId}`))).toBe(true);
    expect(lists.stock.size).toBe(0);
    expect(lists.deviations.size).toBe(0);
  });

  it("pages changes so an upsert on page 1 deleted on page 2 is absent", async () => {
    const sinceCalls: number[] = [];
    const lists = await loadOfficeLists(
      async (url) => {
        if (url.includes("/sync/snapshot")) {
          return { items: [] };
        }
        const since = Number(new URL(url).searchParams.get("since"));
        sinceCalls.push(since);
        if (since === 0) {
          return {
            entries: [
              change({
                seq: 1,
                entity: "stock_balance",
                id: "hu-page",
                op: "upsert",
                warehouse_id: warehouseId,
                payload: {
                  location_id: "loc-a",
                  article_id: "art-1",
                  handling_unit_id: "hu-page",
                  qty_base: "3",
                  reserved_qty_base: "0",
                },
              }),
            ],
          };
        }
        if (since === 1) {
          return {
            entries: [
              change({
                seq: 2,
                entity: "stock_balance",
                id: "hu-page",
                op: "delete",
                warehouse_id: warehouseId,
                payload: { handling_unit_id: "hu-page" },
              }),
            ],
          };
        }
        return { entries: [] };
      },
      "http://sync.test",
      warehouseId,
    );
    expect(sinceCalls).toEqual([0, 1, 2]);
    expect(lists.stock.has("hu-page")).toBe(false);
  });

  it("treats a 500 and a 410 from changes as an error, not an empty list", async () => {
    await expect(
      loadOfficeLists(
        async () => {
          throw new SyncLoadError(500);
        },
        "http://sync.test",
        warehouseId,
      ),
    ).rejects.toMatchObject({ status: 500 });

    await expect(
      loadOfficeLists(
        async (url) => {
          if (url.includes("/sync/snapshot")) {
            return { items: [stockSnapshotItem()] };
          }
          throw new SyncLoadError(410);
        },
        "http://sync.test",
        warehouseId,
      ),
    ).rejects.toMatchObject({ status: 410 });
  });
});
