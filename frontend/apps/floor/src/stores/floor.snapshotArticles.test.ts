import "fake-indexeddb/auto";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getDb, resetSharedDb } from "../db";
import { useFloorStore } from "./floor";

const warehouseA = "01900000-0000-7000-8000-000000000001";
const warehouseB = "01900000-0000-7000-8000-000000000002";
const articleA = {
  id: "01900000-0000-7000-8000-000000000201",
  sku: "KAFFE-500",
  name: "Kaffe",
  status: "active",
  base_uom_id: "01900000-0000-7000-8000-000000000101",
  quantity_precision: 0,
  quantity_step: "1",
  packaging_levels: [
    { id: "01900000-0000-7000-8000-000000000301", rank: 0, name: "st", qty_in_base: "1" },
  ],
};
const articleB = {
  ...articleA,
  id: "01900000-0000-7000-8000-000000000202",
  sku: "TE-100",
  name: "Te",
};
const stUnit = {
  id: "01900000-0000-7000-8000-000000000101",
  code: "st",
  dimension: "count",
  factor_to_dimension_base: "1",
  display_name_sv: "st",
  display_name_en: "st",
};
const kgUnit = {
  id: "01900000-0000-7000-8000-000000000102",
  code: "kg",
  dimension: "mass",
  factor_to_dimension_base: "1",
  display_name_sv: "kg",
  display_name_en: "kg",
};

function jsonRes(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json", "Lagerkraft-Feed-Epoch": "epoch-1" },
  });
}

function mockSnapshot(articles: unknown[], units: unknown[]) {
  const floor = useFloorStore();
  vi.spyOn(floor, "authedFetch").mockImplementation(async (input: string) => {
    const url = String(input);
    if (url.includes("entity=Task")) {
      return jsonRes({ items: [], feed_epoch: "epoch-1", snapshot_schema: "1" });
    }
    if (url.includes("entity=Location")) {
      return jsonRes({ items: [] });
    }
    if (url.includes("entity=Article")) {
      return jsonRes({ items: articles });
    }
    if (url.includes("entity=UnitOfMeasure")) {
      return jsonRes({ items: units });
    }
    if (url.includes("entity=HandlingUnit") || url.includes("entity=StockBalance")) {
      return jsonRes({ items: [] });
    }
    throw new Error(url);
  });
  return floor;
}

describe("loadSnapshot articles and units", () => {
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

  it("stores article sku, nested packaging_levels, and unit snake_case fields", async () => {
    const floor = mockSnapshot([articleA], [stUnit]);
    await floor.loadSnapshot(warehouseA);

    expect(floor.authedFetch).toHaveBeenCalledWith(
      expect.stringContaining("entity=Article"),
    );
    expect(floor.authedFetch).toHaveBeenCalledWith(
      expect.stringContaining("entity=UnitOfMeasure"),
    );

    const db = getDb();
    expect(await db.table("articles").toArray()).toEqual([articleA]);
    expect(await db.table("units").toArray()).toEqual([stUnit]);
  });

  it("replaces whole articles and units tables even when warehouse changes", async () => {
    const floor = mockSnapshot([articleA], [stUnit]);
    await floor.loadSnapshot(warehouseA);
    vi.restoreAllMocks();
    mockSnapshot([articleB], [kgUnit]);
    await floor.loadSnapshot(warehouseB);

    const db = getDb();
    expect(await db.table("articles").toArray()).toEqual([articleB]);
    expect(await db.table("units").toArray()).toEqual([kgUnit]);
  });
});
