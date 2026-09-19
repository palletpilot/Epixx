import { describe, expect, it } from "vitest";
import type { ChangeEntry } from "@lagerkraft/realtime";
import { applyRealtimeEntries, type TaskRow } from "./useRealtimeFeed";

function entry(seq: number, id: string, status: string, actor = "anna"): ChangeEntry {
  return {
    seq,
    entity: "task",
    id,
    op: "upsert",
    payload: {
      id,
      warehouse_id: "wh",
      type: "pick",
      status,
      created_at: "2026-09-13T08:00:00.000Z",
    },
    occurred_at: `2026-09-13T08:00:0${seq}.000Z`,
    actor,
  };
}

describe("applyRealtimeEntries", () => {
  it("upserts tasks in seq order and records last activity from actor and occurred_at", () => {
    const tasks = new Map<string, TaskRow>();
    const first = applyRealtimeEntries(tasks, [entry(1, "t1", "open"), entry(2, "t1", "claimed")]);
    expect([...first.tasks.values()].map((t) => t.status)).toEqual(["claimed"]);
    expect(first.lastActivity).toEqual({
      actor: "anna",
      occurredAt: "2026-09-13T08:00:02.000Z",
    });

    const second = applyRealtimeEntries(first.tasks, [
      { ...entry(3, "t2", "open"), actor: "bo" },
    ]);
    expect(second.tasks.size).toBe(2);
    expect(second.lastActivity?.actor).toBe("bo");
  });

  it("upserts location bins onto the map", () => {
    const applied = applyRealtimeEntries(new Map(), [
      {
        seq: 1,
        entity: "location",
        id: "b1",
        op: "upsert",
        payload: { id: "b1", warehouse_id: "wh", type: "bin", code: "A-01-01-01", parent_id: "lv1" },
        occurred_at: "2026-09-19T08:00:00.000Z",
        actor: "anna",
      },
    ]);
    expect(applied.locations.get("b1")?.code).toBe("A-01-01-01");
    expect(applied.tasks.size).toBe(0);
  });
});
