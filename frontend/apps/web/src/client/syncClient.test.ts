import { describe, expect, it } from "vitest";
import {
  applyChanges,
  asDecimalString,
  changeMatchesSelectedWarehouse,
  compareDecimalStrings,
  compareStockLocationOrder,
  deviationDetailParts,
  deviationLocationId,
  emptyLists,
  formatDecimalSvSe,
  isUnlocatedSystemLocation,
  listsFromSnapshots,
  listsFromStockSnapshot,
  loadOfficeLists,
  mapChangeRows,
  mapDeviationSnapshot,
  mapLocationSnapshot,
  mapStockSnapshot,
  SyncLoadError,
  type ChangeRow,
  type LocationRow,
  type StockRow,
} from "./syncClient";

const warehouseId = "wh-1";
const otherWarehouse = "wh-2";
const huId = "hu-1";

function stockSnapshotItem(overrides: Partial<StockRow> = {}): StockRow {
  return {
    id: "bal-1",
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
    expect(lists.locations.size).toBe(0);
  });

  it("maps open and pending_approval deviation snapshot rows and skips resolved", () => {
    const rows = mapDeviationSnapshot({
      snapshot_schema: "ignore-me",
      items: [
        {
          id: "d-open",
          warehouse_id: warehouseId,
          kind: "occupied_bin",
          status: "open",
          command_id: "c1",
          detail: { location_id: "loc-a" },
          created_at: "2026-09-19T08:00:00.000Z",
        },
        {
          id: "d-pending",
          warehouse_id: warehouseId,
          kind: "wrong_location",
          status: "pending_approval",
          command_id: "c2",
          detail: { actual_location_id: "loc-b" },
          created_at: "2026-09-19T08:00:01.000Z",
        },
        {
          id: "d-resolved",
          warehouse_id: warehouseId,
          kind: "occupied_bin",
          status: "resolved",
          command_id: "c3",
          detail: { location_id: "loc-c" },
          created_at: "2026-09-19T08:00:02.000Z",
        },
        {
          id: "task-shaped",
          warehouse_id: warehouseId,
          type: "putaway",
          status: "open",
        },
      ],
    });
    expect(rows.map((row) => row.id).sort()).toEqual(["d-open", "d-pending"]);
    const lists = listsFromSnapshots({ items: [] }, { items: [] }, {
      items: [
        {
          id: "d-open",
          warehouse_id: warehouseId,
          kind: "occupied_bin",
          status: "open",
          command_id: "c1",
          detail: {},
          created_at: "2026-09-19T08:00:00.000Z",
        },
      ],
    });
    expect(lists.deviations.get("d-open")?.kind).toBe("occupied_bin");
  });

  it("maps Location snapshot is_system and code without using a display name", () => {
    const rows = mapLocationSnapshot({
      snapshot_schema: "ignore-me",
      items: [
        { id: "loc-unlocated", warehouse_id: warehouseId, code: "UNLOCATED", is_system: true, name: "No location" },
        { id: "loc-bin", warehouse_id: warehouseId, code: "A-01-01-01", is_system: false },
      ],
    });
    expect(rows).toEqual([
      { id: "loc-unlocated", warehouse_id: warehouseId, code: "UNLOCATED", is_system: true },
      { id: "loc-bin", warehouse_id: warehouseId, code: "A-01-01-01", is_system: false },
    ]);
    expect(isUnlocatedSystemLocation(rows[0])).toBe(true);
    expect(isUnlocatedSystemLocation(rows[1])).toBe(false);
    expect(isUnlocatedSystemLocation({ is_system: true, code: "RECEIVING" })).toBe(false);
    expect(isUnlocatedSystemLocation({ is_system: false, code: "UNLOCATED" })).toBe(false);
    expect(isUnlocatedSystemLocation({ is_system: true, code: "No location" })).toBe(false);
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
          id: "bal-2",
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
    expect(applied.stock.get("bal-2")?.qty_base).toBe("2.000000");
    expect(applied.stock.get("bal-2")?.reserved_qty_base).toBe("0");
    expect(applied.stock.get("bal-2")?.handling_unit_id).toBe("hu-2");
  });
});

describe("applyChanges", () => {
  it("applies a change whose warehouse_id is null as tenant-wide", () => {
    const entry = change({
      seq: 1,
      entity: "stock_balance",
      id: "bal-tenant",
      op: "upsert",
      warehouse_id: null,
      payload: {
        location_id: "loc-t",
        article_id: "art-t",
        handling_unit_id: "hu-t",
        qty_base: "3",
        reserved_qty_base: "0",
      },
    });
    expect(changeMatchesSelectedWarehouse(entry, warehouseId)).toBe(true);
    const next = applyChanges(emptyLists(), [entry], warehouseId);
    expect(next.stock.get("bal-tenant")?.qty_base).toBe("3");
  });

  it("drops a change whose warehouse_id is a different warehouse", () => {
    const entry = change({
      seq: 1,
      entity: "stock_balance",
      id: "bal-other",
      op: "upsert",
      warehouse_id: otherWarehouse,
      payload: {
        location_id: "loc-z",
        article_id: "art-z",
        handling_unit_id: "hu-other",
        qty_base: "9",
        reserved_qty_base: "0",
      },
    });
    expect(changeMatchesSelectedWarehouse(entry, warehouseId)).toBe(false);
    const next = applyChanges(emptyLists(), [entry], warehouseId);
    expect(next.stock.has("bal-other")).toBe(false);
  });

  it("replaces and then removes a snapshot stock row when later changes use the same opaque id", () => {
    const start = listsFromStockSnapshot({
      items: [stockSnapshotItem({ id: "bal-1", qty_base: "4", location_id: "loc-a" })],
    });
    expect(start.stock.get("bal-1")?.qty_base).toBe("4");
    const upserted = applyChanges(
      start,
      [
        change({
          seq: 1,
          entity: "stock_balance",
          id: "bal-1",
          op: "upsert",
          warehouse_id: warehouseId,
          payload: {
            location_id: "loc-b",
            article_id: "art-1",
            handling_unit_id: huId,
            qty_base: "7",
            reserved_qty_base: "0",
          },
        }),
      ],
      warehouseId,
    );
    expect(upserted.stock.size).toBe(1);
    expect(upserted.stock.get("bal-1")).toMatchObject({ id: "bal-1", location_id: "loc-b", qty_base: "7" });
    const deleted = applyChanges(
      upserted,
      [
        change({
          seq: 2,
          entity: "stock_balance",
          id: "bal-1",
          op: "delete",
          warehouse_id: warehouseId,
          payload: {},
        }),
      ],
      warehouseId,
    );
    expect(deleted.stock.has("bal-1")).toBe(false);
  });

  it("keeps an empty warehouse empty when there are no matching changes", () => {
    const next = applyChanges(emptyLists(), [], warehouseId);
    expect(next.stock.size).toBe(0);
    expect(next.deviations.size).toBe(0);
  });

  it("applies occupied-bin as five upserts under one command_id", () => {
    const incomingHu = huId;
    const existingHu = "hu-old";
    const incomingBalanceId = "sb-in";
    const existingBalanceId = "sb-ex";
    const binId = "loc-bin";
    const unlocatedId = "loc-unlocated";
    const existingQty = "4.250000";
    const incomingQty = "2.000000";
    const commandId = "cmd-occ-1";
    const locations: LocationRow[] = [
      { id: "loc-from", warehouse_id: warehouseId, code: "A-01-01-01", is_system: false },
      { id: binId, warehouse_id: warehouseId, code: "A-01-01-02", is_system: false },
      { id: unlocatedId, warehouse_id: warehouseId, code: "UNLOCATED", is_system: true },
    ];
    const start = listsFromSnapshots(
      {
        items: [
          stockSnapshotItem({
            id: incomingBalanceId,
            location_id: "loc-from",
            handling_unit_id: incomingHu,
            qty_base: incomingQty,
            reserved_qty_base: "0",
          }),
          stockSnapshotItem({
            id: existingBalanceId,
            location_id: binId,
            handling_unit_id: existingHu,
            article_id: "art-old",
            qty_base: existingQty,
            reserved_qty_base: "0",
          }),
        ],
      },
      { items: locations },
    );
    const next = applyChanges(
      start,
      [
        change({
          seq: 10,
          entity: "handling_unit",
          id: incomingHu,
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: { id: incomingHu, warehouse_id: warehouseId, lpn: "LPN-in" },
        }),
        change({
          seq: 11,
          entity: "stock_balance",
          id: incomingBalanceId,
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            location_id: binId,
            article_id: "art-1",
            handling_unit_id: incomingHu,
            qty_base: incomingQty,
            reserved_qty_base: "0",
          },
        }),
        change({
          seq: 12,
          entity: "stock_balance",
          id: existingBalanceId,
          op: "upsert",
          command_id: commandId,
          warehouse_id: warehouseId,
          payload: {
            location_id: unlocatedId,
            article_id: "art-old",
            handling_unit_id: existingHu,
            qty_base: existingQty,
            reserved_qty_base: "0",
          },
        }),
        change({
          seq: 13,
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
            detail: {
              location_id: binId,
              existing_handling_unit_id: existingHu,
              incoming_handling_unit_id: incomingHu,
              movement_id: "mov-in",
              displaced_movement_id: "mov-old",
              lpn: "LPN-in",
            },
            created_at: "2026-09-19T08:00:01.000Z",
          },
        }),
        change({
          seq: 14,
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

    const inBin = [...next.stock.values()].filter((row) => row.location_id === binId);
    expect(inBin.map((row) => row.handling_unit_id)).toEqual([incomingHu]);
    expect(next.stock.get(incomingBalanceId)).toMatchObject({
      id: incomingBalanceId,
      location_id: binId,
      handling_unit_id: incomingHu,
      qty_base: incomingQty,
    });
    expect(next.stock.get(existingBalanceId)).toMatchObject({
      id: existingBalanceId,
      location_id: unlocatedId,
      handling_unit_id: existingHu,
      qty_base: existingQty,
    });
    expect(next.stock.get(existingBalanceId)?.qty_base).toBe(existingQty);
    expect(next.stock.has(incomingHu)).toBe(false);
    expect(next.stock.has(existingHu)).toBe(false);
    expect(isUnlocatedSystemLocation(next.locations.get(unlocatedId))).toBe(true);
    expect(compareStockLocationOrder(binId, unlocatedId, next.locations)).toBeLessThan(0);

    const deviation = next.deviations.get("d-new");
    expect(next.deviations.size).toBe(1);
    expect(deviation).toMatchObject({
      kind: "occupied_bin",
      command_id: commandId,
      warehouse_id: warehouseId,
      detail: {
        location_id: binId,
        existing_handling_unit_id: existingHu,
        incoming_handling_unit_id: incomingHu,
        movement_id: "mov-in",
        displaced_movement_id: "mov-old",
        lpn: "LPN-in",
      },
    });
    const visible = deviationDetailParts(deviation!);
    expect(visible).toEqual(expect.arrayContaining([existingHu, incomingHu]));
    expect(visible.join(" ")).not.toContain("mov-in");
    expect(visible.join(" ")).not.toContain("mov-old");
  });

  it("applies location upserts from the change feed and joins UNLOCATED on location_id", () => {
    const start = listsFromStockSnapshot({
      items: [stockSnapshotItem({ id: "bal-1", location_id: "loc-u" })],
    });
    const next = applyChanges(
      start,
      [
        change({
          seq: 1,
          entity: "location",
          id: "loc-u",
          op: "upsert",
          warehouse_id: warehouseId,
          payload: {
            id: "loc-u",
            warehouse_id: warehouseId,
            code: "UNLOCATED",
            is_system: true,
            name: "No location",
          },
        }),
      ],
      warehouseId,
    );
    expect(next.stock.get("bal-1")?.location_id).toBe("loc-u");
    expect(isUnlocatedSystemLocation(next.locations.get("loc-u"))).toBe(true);
  });

  it("keeps two stock_balance rows for the same HU when ids differ, and an upsert to one does not overwrite the other", () => {
    const start = listsFromStockSnapshot({
      items: [
        stockSnapshotItem({
          id: "bal-a",
          handling_unit_id: huId,
          article_id: "art-1",
          qty_base: "1.000000",
          reserved_qty_base: "0",
        }),
        stockSnapshotItem({
          id: "bal-b",
          handling_unit_id: huId,
          article_id: "art-2",
          qty_base: "2.000000",
          reserved_qty_base: "0",
        }),
      ],
    });
    expect(start.stock.size).toBe(2);
    expect(start.stock.get("bal-a")?.article_id).toBe("art-1");
    expect(start.stock.get("bal-b")?.article_id).toBe("art-2");
    const next = applyChanges(
      start,
      [
        change({
          seq: 1,
          entity: "stock_balance",
          id: "bal-a",
          op: "upsert",
          warehouse_id: warehouseId,
          payload: {
            location_id: "loc-a",
            article_id: "art-1",
            handling_unit_id: huId,
            qty_base: "9.000000",
            reserved_qty_base: "0",
          },
        }),
      ],
      warehouseId,
    );
    expect(next.stock.size).toBe(2);
    expect(next.stock.get("bal-a")).toMatchObject({
      id: "bal-a",
      handling_unit_id: huId,
      article_id: "art-1",
      qty_base: "9.000000",
    });
    expect(next.stock.get("bal-b")).toMatchObject({
      id: "bal-b",
      handling_unit_id: huId,
      article_id: "art-2",
      qty_base: "2.000000",
    });
  });

  it("sorts UNLOCATED system locations after real bins", () => {
    const locations = new Map<string, LocationRow>([
      ["loc-a", { id: "loc-a", warehouse_id: warehouseId, code: "Z-99", is_system: false }],
      ["loc-unlocated", { id: "loc-unlocated", warehouse_id: warehouseId, code: "UNLOCATED", is_system: true }],
      ["loc-receiving", { id: "loc-receiving", warehouse_id: warehouseId, code: "RECEIVING", is_system: true }],
    ]);
    expect(compareStockLocationOrder("loc-a", "loc-unlocated", locations)).toBeLessThan(0);
    expect(compareStockLocationOrder("loc-unlocated", "loc-a", locations)).toBeGreaterThan(0);
    expect(compareStockLocationOrder("loc-receiving", "loc-unlocated", locations)).toBeLessThan(0);
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
  it("loads StockBalance, Location, and Deviation snapshots and applies a tenant-wide change", async () => {
    const called: string[] = [];
    const lists = await loadOfficeLists(
      async (url) => {
        called.push(url);
        if (url.includes("/sync/snapshot") && url.includes("entity=Deviation")) {
          return {
            snapshot_schema: "do-not-use",
            items: [
              {
                id: "d-open",
                warehouse_id: warehouseId,
                kind: "occupied_bin",
                status: "open",
                command_id: "c1",
                detail: { location_id: "loc-a" },
                created_at: "2026-09-19T08:00:00.000Z",
              },
              {
                id: "d-resolved",
                warehouse_id: warehouseId,
                kind: "occupied_bin",
                status: "resolved",
                command_id: "c2",
                detail: {},
                created_at: "2026-09-19T08:00:01.000Z",
              },
            ],
          };
        }
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
              id: "bal-tenant",
              op: "upsert",
              payload: {
                location_id: "loc-a",
                article_id: "art-1",
                handling_unit_id: "hu-guess",
                qty_base: "1",
                reserved_qty_base: "0",
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
    expect(called.some((url) => url.includes("/sync/snapshot") && url.includes("entity=Location"))).toBe(true);
    expect(called.some((url) => url.includes("/sync/snapshot") && url.includes("entity=Deviation"))).toBe(true);
    expect(called.some((url) => url.includes("/sync/changes") && url.includes(`warehouse=${warehouseId}`))).toBe(true);
    expect(lists.stock.get("bal-tenant")?.qty_base).toBe("1");
    expect(lists.deviations.has("d-open")).toBe(true);
    expect(lists.deviations.has("d-resolved")).toBe(false);
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
                id: "bal-page",
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
                id: "bal-page",
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
    expect(lists.stock.has("bal-page")).toBe(false);
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
