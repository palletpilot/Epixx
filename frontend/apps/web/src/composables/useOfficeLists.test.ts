import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useOfficeLists } from "./useOfficeLists";
import { useAuthStore } from "../stores/auth";
import { useSessionStore } from "../stores/session";

const warehouseId = "22222222-2222-7222-8222-222222222222";

function stockItem(hu: string) {
  return {
    id: `bal-${hu}`,
    location_id: "loc-a",
    article_id: "art-1",
    handling_unit_id: hu,
    qty_base: "1",
    reserved_qty_base: "0",
  };
}

function deviationItem(id: string, status = "open") {
  return {
    warehouse_id: warehouseId,
    id,
    kind: "occupied_bin",
    status,
    command_id: "cmd-1",
    detail: { location_id: "loc-a", existing_reserved_qty_base: "0" },
    created_at: "2026-09-19T08:00:00.000Z",
  };
}

function ok(body: unknown) {
  return { ok: true, status: 200, json: async () => body };
}

function snapshotOk(items: unknown[] = [], seq = 1) {
  return ok({ seq, items });
}

function fail(status: number) {
  return { ok: false, status, json: async () => ({ items: [], entries: [] }) };
}

function stubFetch(handler: (url: string) => { ok: boolean; status: number; json: () => Promise<unknown> }) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => handler(String(input))),
  );
}

describe("useOfficeLists HTTP errors", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    sessionStorage.clear();
    vi.unstubAllGlobals();
    const auth = useAuthStore();
    const session = useSessionStore();
    auth.applyTokens("access-token", "refresh-token");
    session.setWarehouse(warehouseId);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("sets loadError on a 410 from changes and a 500 from snapshot, and clears prior lists", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot") && url.includes("entity=StockBalance")) {
        return snapshotOk([stockItem("hu-prev")]);
      }
      if (url.includes("/sync/snapshot")) {
        return snapshotOk();
      }
      if (url.includes("/sync/changes")) {
        return ok({
          entries: [
            {
              seq: 1,
              entity: "deviation",
              id: "d-prev",
              op: "upsert",
              warehouse_id: warehouseId,
              payload: deviationItem("d-prev"),
              command_id: "cmd-1",
              actor: null,
              occurred_at: "2026-09-19T08:00:00.000Z",
              recorded_at: "2026-09-19T08:00:00.100Z",
            },
          ],
        });
      }
      return ok({ entries: [] });
    });

    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(false);
    expect(office.lists.value.stock.has("bal-hu-prev")).toBe(true);
    expect(office.deviationCount.value).toBe(1);

    stubFetch((url) => {
      if (url.includes("/sync/changes")) {
        return fail(410);
      }
      if (url.includes("/sync/snapshot") && url.includes("entity=StockBalance")) {
        return snapshotOk([stockItem("hu-new")]);
      }
      return snapshotOk();
    });
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect({ stock: office.lists.value.stock.size, deviations: office.lists.value.deviations.size }).toEqual({
      stock: 0,
      deviations: 0,
    });

    stubFetch((url) => {
      if (url.includes("/sync/snapshot")) {
        return fail(500);
      }
      return ok({ entries: [] });
    });
    await office.refresh();
    expect(office.loadError.value).toBe(true);
  });

  it("sets loadError for RC id-less StockBalance snapshot items and clears lists", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot") && url.includes("entity=StockBalance")) {
        return snapshotOk([
          {
            location_id: "loc-a",
            article_id: "art-1",
            handling_unit_id: "hu-1",
            qty_base: "4",
            reserved_qty_base: "0",
          },
        ]);
      }
      if (url.includes("/sync/snapshot")) {
        return snapshotOk();
      }
      return ok({ entries: [] });
    });
    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect(office.lists.value.stock.size).toBe(0);
    expect(office.lists.value.deviations.size).toBe(0);
  });

  it("sets loadError when snapshot seq is missing", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot")) {
        return ok({ items: [] });
      }
      return ok({ entries: [] });
    });
    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect(office.lists.value.stock.size).toBe(0);
  });

  it("sets loadError when a deviation snapshot item has no status", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot") && url.includes("entity=Deviation")) {
        return snapshotOk([
          {
            id: "d-1",
            warehouse_id: warehouseId,
            kind: "occupied_bin",
            command_id: "c1",
            detail: { location_id: "loc-a" },
            created_at: "2026-09-19T08:00:00.000Z",
          },
        ]);
      }
      if (url.includes("/sync/snapshot")) {
        return snapshotOk();
      }
      return ok({ entries: [] });
    });
    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect(office.lists.value.deviations.size).toBe(0);
    expect(office.lists.value.stock.size).toBe(0);
  });

  it("sets loadError when a deviation feed payload has no status", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot")) {
        return snapshotOk();
      }
      return ok({
        entries: [
          {
            seq: 1,
            entity: "deviation",
            id: "d-1",
            op: "upsert",
            warehouse_id: warehouseId,
            payload: {
              warehouse_id: warehouseId,
              id: "d-1",
              kind: "occupied_bin",
              command_id: "c1",
              detail: { location_id: "loc-a" },
              created_at: "2026-09-19T08:00:00.000Z",
            },
            command_id: "c1",
            actor: null,
            occurred_at: "2026-09-19T08:00:00.000Z",
            recorded_at: "2026-09-19T08:00:00.100Z",
          },
        ],
      });
    });
    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect(office.lists.value.deviations.size).toBe(0);
    expect(office.lists.value.stock.size).toBe(0);
  });

  it("sets loadError when an occupied_bin snapshot item has no existing_reserved_qty_base", async () => {
    stubFetch((url) => {
      if (url.includes("/sync/snapshot") && url.includes("entity=Deviation")) {
        return snapshotOk([
          {
            id: "d-1",
            warehouse_id: warehouseId,
            kind: "occupied_bin",
            status: "open",
            command_id: "c1",
            detail: { location_id: "loc-a" },
            created_at: "2026-09-19T08:00:00.000Z",
          },
        ]);
      }
      if (url.includes("/sync/snapshot")) {
        return snapshotOk();
      }
      return ok({ entries: [] });
    });
    const office = useOfficeLists();
    await office.refresh();
    expect(office.loadError.value).toBe(true);
    expect(office.lists.value.deviations.size).toBe(0);
    expect(office.lists.value.stock.size).toBe(0);
  });
});
