import "fake-indexeddb/auto";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getDb, resetSharedDb } from "../db";
import { useFloorStore } from "./floor";

const warehouseA = "01900000-0000-7000-8000-000000000001";
const huA = {
  id: "01900000-0000-7000-8000-000000000401",
  warehouse_id: warehouseA,
  lpn: "LPN-AABBCCDD",
  height_mm: null,
  received_at: "2026-09-19T10:00:00.000Z",
  contents: [
    {
      id: "01900000-0000-7000-8000-000000000411",
      handling_unit_id: "01900000-0000-7000-8000-000000000401",
      article_id: "01900000-0000-7000-8000-000000000201",
      qty_base: "1",
      packaging_level_id: "01900000-0000-7000-8000-000000000301",
    },
  ],
};
const balA = {
  location_id: "01900000-0000-7000-8000-000000000501",
  article_id: "01900000-0000-7000-8000-000000000201",
  handling_unit_id: huA.id,
  qty_base: "1",
  reserved_qty_base: "0",
};

function jsonRes(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json", "Lagerkraft-Feed-Epoch": "epoch-1" },
  });
}

function mockSnapshot(hus: unknown[], bals: unknown[]) {
  const floor = useFloorStore();
  vi.spyOn(floor, "authedFetch").mockImplementation(async (input: string) => {
    const url = String(input);
    if (url.includes("entity=HandlingUnit")) {
      return jsonRes({ items: hus });
    }
    if (url.includes("entity=StockBalance")) {
      return jsonRes({ items: bals });
    }
    return jsonRes({ items: [] });
  });
  return floor;
}

describe("loadSnapshot handling units and stock balances", () => {
  beforeEach(async () => {
    resetSharedDb();
    setActivePinia(createPinia());
    await getDb().open();
  });

  afterEach(async () => {
    const db = getDb();
    db.close();
    await indexedDB.deleteDatabase(db.name);
    resetSharedDb();
    vi.restoreAllMocks();
  });

  it("stores nested HU contents and stamps warehouse_id on balances", async () => {
    const floor = mockSnapshot([huA], [balA]);
    await floor.loadSnapshot(warehouseA);

    expect(floor.authedFetch).toHaveBeenCalledWith(expect.stringContaining("entity=HandlingUnit"));
    expect(floor.authedFetch).toHaveBeenCalledWith(expect.stringContaining("entity=StockBalance"));

    const db = getDb();
    expect(await db.table("handling_units").toArray()).toEqual([huA]);
    expect(await db.table("stock_balances").toArray()).toEqual([{ ...balA, warehouse_id: warehouseA }]);
  });

  it("replaces HU and balances for the warehouse", async () => {
    const floor = mockSnapshot([huA], [balA]);
    await floor.loadSnapshot(warehouseA);
    vi.restoreAllMocks();
    const updated = { ...huA, lpn: "LPN-UPDATED" };
    mockSnapshot([updated], []);
    await floor.loadSnapshot(warehouseA);

    const db = getDb();
    expect(await db.table("handling_units").toArray()).toEqual([updated]);
    expect(await db.table("stock_balances").toArray()).toEqual([]);
  });
});
