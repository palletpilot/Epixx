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

function deviationItem(id: string) {
  return {
    warehouse_id: warehouseId,
    id,
    kind: "occupied_bin",
    command_id: "cmd-1",
    detail: { location_id: "loc-a" },
    created_at: "2026-09-19T08:00:00.000Z",
  };
}

function ok(body: unknown) {
  return { ok: true, status: 200, json: async () => body };
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
        return ok({ items: [stockItem("hu-prev")] });
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
      return ok({ items: [stockItem("hu-new")] });
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
});
