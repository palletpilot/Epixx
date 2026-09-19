import "fake-indexeddb/auto";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { FloorDb } from "../db";
import { applyIdMap, enqueueCommand } from "./outbox";

const deviceId = "01900000-0000-7000-8000-000000000010";
const userId = "01900000-0000-7000-8000-000000000011";
const fromId = "aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa";
const toId = "bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb";

describe("id_map rewrite", () => {
  let db: FloorDb;

  beforeEach(async () => {
    db = new FloorDb(`floor-idmap-${crypto.randomUUID()}`);
    await db.open();
  });

  afterEach(async () => {
    db.close();
    await indexedDB.deleteDatabase(db.name);
  });

  it("queued SetLocationDimensions ids follow id_map", async () => {
    await enqueueCommand(db, {
      id: "dims-1",
      type: "SetLocationDimensions",
      v: 1,
      payload: { ids: [fromId], height_mm: 1200 },
      device_id: deviceId,
      user_id: userId,
    });
    await db.locations.put({
      id: fromId,
      warehouse_id: "wh",
      type: "bin",
      code: "A-01-01-01",
    });

    await applyIdMap(db, [{ from: fromId, to: toId }]);

    const queued = await db.outbox.get("dims-1");
    const payload = queued?.payload as { ids: string[] };
    expect(payload.ids).toEqual([toId]);
    expect(await db.locations.get(fromId)).toBeUndefined();
    expect((await db.locations.get(toId))?.code).toBe("A-01-01-01");
  });
});
