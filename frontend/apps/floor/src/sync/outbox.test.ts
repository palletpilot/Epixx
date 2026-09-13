import "fake-indexeddb/auto";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { FloorDb } from "../db";
import { applyFeedEntries, flushOutbox, type FlushHttpResult } from "./flush";
import { enqueueCommand, enqueueWithTask, logoutLeavesOutbox } from "./outbox";

const deviceId = "01900000-0000-7000-8000-000000000010";
const userId = "01900000-0000-7000-8000-000000000011";
const warehouseId = "01900000-0000-7000-8000-000000000001";
const taskId = "01900000-0000-7000-8000-000000000012";

function draft(id: string) {
  return {
    id,
    type: "ClaimTask",
    v: 1,
    payload: { task_id: taskId },
    device_id: deviceId,
    user_id: userId,
    occurred_at: "2026-09-13T09:00:00.000Z",
    created_at: "2026-09-13T09:00:00.000Z",
  };
}

describe("outbox state machine", () => {
  let db: FloorDb;

  beforeEach(async () => {
    db = new FloorDb(`floor-test-${crypto.randomUUID()}`);
    await db.open();
    await db.tasks.put({
      id: taskId,
      warehouse_id: warehouseId,
      type: "pick",
      status: "open",
    });
  });

  afterEach(async () => {
    db.close();
    await indexedDB.deleteDatabase(db.name);
  });

  it("moves pending to sent to acked to confirmed", async () => {
    const row = await enqueueWithTask(db, draft("c1"), taskId, {
      status: "claimed",
      assignee_user_id: userId,
    });
    expect(row.state).toBe("pending");
    expect((await db.tasks.get(taskId))?.status).toBe("claimed");

    await flushOutbox({
      db,
      now: () => new Date("2026-09-13T09:00:10.000Z"),
      lock: async (_n, fn) => fn(),
      postCommands: async () =>
        ({
          status: 200,
          results: [{ command_id: "c1", outcome: "Applied" }],
        }) satisfies FlushHttpResult,
    });
    expect((await db.outbox.get("c1"))?.state).toBe("acked");

    await applyFeedEntries(
      db,
      warehouseId,
      [
        {
          seq: 1,
          entity: "task",
          id: taskId,
          op: "upsert",
          payload: { id: taskId, warehouse_id: warehouseId, type: "pick", status: "claimed" },
          command_id: "c1",
        },
      ],
      "epoch-1",
      "1",
      new Date("2026-09-13T09:00:11.000Z"),
    );
    expect((await db.outbox.get("c1"))?.state).toBe("confirmed");
    expect(await db.confirmed.get("c1")).toBeTruthy();
  });

  it("re-sends sent rows older than 60 seconds", async () => {
    await enqueueCommand(db, draft("c2"));
    await db.outbox.update("c2", { state: "sent", sent_at: "2026-09-13T09:00:00.000Z" });
    const posted: string[] = [];
    await flushOutbox({
      db,
      now: () => new Date("2026-09-13T09:01:05.000Z"),
      lock: async (_n, fn) => fn(),
      postCommands: async (batch) => {
        posted.push(...batch.commands.map((c) => c.id));
        return { status: 200, results: [{ command_id: "c2", outcome: "Applied" }] };
      },
    });
    expect(posted).toEqual(["c2"]);
    expect((await db.outbox.get("c2"))?.state).toBe("acked");
  });

  it("keeps held commands pending", async () => {
    await enqueueCommand(db, draft("c3"));
    await flushOutbox({
      db,
      now: () => new Date("2026-09-13T09:00:10.000Z"),
      lock: async (_n, fn) => fn(),
      postCommands: async () => ({
        status: 200,
        results: [{ command_id: "c3", outcome: "Held" }],
      }),
    });
    const row = await db.outbox.get("c3");
    expect(row?.state).toBe("pending");
    expect(row?.sent_at).toBeUndefined();
  });

  it("re-sends unconfirmed commands on seq regression", async () => {
    await enqueueCommand(db, draft("c4"));
    await db.outbox.update("c4", { state: "acked", sent_at: "2026-09-13T09:00:10.000Z" });
    await db.cursor.put({
      warehouse_id: warehouseId,
      seq: 40,
      feed_epoch: "epoch-1",
      snapshot_schema: "1",
    });
    const result = await applyFeedEntries(
      db,
      warehouseId,
      [{ seq: 12, entity: "task", id: taskId, op: "upsert", payload: {}, command_id: null }],
      "epoch-1",
      "1",
      new Date("2026-09-13T09:10:00.000Z"),
    );
    expect(result).toBe("regression");
    expect((await db.outbox.get("c4"))?.state).toBe("pending");
  });

  it("leaves the outbox intact on logout", async () => {
    await enqueueCommand(db, draft("c5"));
    await db.sessions.put({
      user_id: userId,
      display_name: "Anna",
      encrypted_refresh_token: "x",
      pin_hash: "h",
      failed_attempts: 0,
    });
    await logoutLeavesOutbox(db);
    expect(await db.sessions.count()).toBe(0);
    expect(await db.outbox.count()).toBe(1);
    expect((await db.outbox.get("c5"))?.state).toBe("pending");
  });
});
