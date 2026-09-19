import type { FloorDb, OutboxRow, OutboxState, TaskRow } from "../db";
import { uuidv7 } from "../uuid";

export type CommandDraft = {
  id?: string;
  type: string;
  v: number;
  payload: unknown;
  device_id: string;
  user_id: string;
  occurred_at?: string;
  created_at?: string;
};

const CONFIRMED_MS = 7 * 24 * 60 * 60 * 1000;
export const SENT_RETRY_MS = 60_000;
export const ACKED_RETRY_MS = 10 * 60_000;
export const BATCH_SIZE = 50;

export async function enqueueCommand(db: FloorDb, draft: CommandDraft): Promise<OutboxRow> {
  const row: OutboxRow = {
    id: draft.id ?? uuidv7(),
    type: draft.type,
    v: draft.v,
    payload: draft.payload,
    created_at: draft.created_at ?? new Date().toISOString(),
    occurred_at: draft.occurred_at ?? new Date().toISOString(),
    device_id: draft.device_id,
    user_id: draft.user_id,
    state: "pending",
  };
  await db.outbox.add(row);
  return row;
}

export async function enqueueWithTask(
  db: FloorDb,
  draft: CommandDraft,
  taskId: string,
  patch: Partial<TaskRow>,
): Promise<OutboxRow> {
  let row: OutboxRow | undefined;
  await db.transaction("rw", db.outbox, db.tasks, async () => {
    row = await enqueueCommand(db, draft);
    const existing = await db.tasks.get(taskId);
    if (existing) {
      await db.tasks.update(taskId, patch);
    }
  });
  if (!row) {
    throw new Error("enqueue failed");
  }
  return row;
}

export function shouldResend(row: OutboxRow, now: Date, online: boolean): boolean {
  if (row.state === "pending") {
    return true;
  }
  if (row.state === "sent" && row.sent_at) {
    return now.getTime() - Date.parse(row.sent_at) >= SENT_RETRY_MS;
  }
  if (row.state === "acked" && online && row.sent_at) {
    return now.getTime() - Date.parse(row.sent_at) >= ACKED_RETRY_MS;
  }
  return false;
}

export async function markSent(db: FloorDb, ids: string[], sentAt: string): Promise<void> {
  await db.transaction("rw", db.outbox, async () => {
    for (const id of ids) {
      await db.outbox.update(id, { state: "sent" satisfies OutboxState, sent_at: sentAt });
    }
  });
}

export async function setOutboxState(
  db: FloorDb,
  id: string,
  state: OutboxState,
  extra?: Partial<OutboxRow>,
): Promise<void> {
  const row = await db.outbox.get(id);
  if (!row) {
    return;
  }
  const next: OutboxRow = { ...row, ...extra, state };
  if (extra && "sent_at" in extra && extra.sent_at === undefined) {
    delete next.sent_at;
  }
  await db.outbox.put(next);
}

export async function confirmCommand(db: FloorDb, commandId: string, confirmedAt: string): Promise<void> {
  await db.transaction("rw", db.outbox, db.confirmed, async () => {
    const row = await db.outbox.get(commandId);
    if (!row || row.state === "rejected") {
      return;
    }
    await db.outbox.update(commandId, { state: "confirmed" });
    await db.confirmed.put({ id: commandId, confirmed_at: confirmedAt });
    const cutoff = Date.parse(confirmedAt) - CONFIRMED_MS;
    await db.confirmed.where("confirmed_at").below(new Date(cutoff).toISOString()).delete();
    const stale = await db.outbox.where("state").equals("confirmed").toArray();
    for (const item of stale) {
      if (Date.parse(item.created_at) < cutoff) {
        await db.outbox.delete(item.id);
      }
    }
  });
}

export async function requeueUnconfirmed(db: FloorDb): Promise<void> {
  await db.transaction("rw", db.outbox, async () => {
    const rows = await db.outbox.where("state").anyOf("acked", "sent").toArray();
    for (const row of rows) {
      await db.outbox.update(row.id, { state: "pending", sent_at: undefined });
    }
  });
}

export async function logoutLeavesOutbox(db: FloorDb): Promise<void> {
  await db.sessions.clear();
}

export async function pendingCount(db: FloorDb): Promise<number> {
  const rows = await db.outbox.toArray();
  return rows.filter((r) => r.state === "pending" || r.state === "sent" || r.state === "acked").length;
}

export async function oldestPendingAt(db: FloorDb): Promise<string | null> {
  const rows = await db.outbox
    .toArray()
    .then((all) => all.filter((r) => r.state === "pending" || r.state === "sent" || r.state === "acked"));
  if (rows.length === 0) {
    return null;
  }
  return rows.map((r) => r.occurred_at).sort()[0] ?? null;
}

export async function syncIssues(db: FloorDb): Promise<OutboxRow[]> {
  return db.outbox.where("state").equals("rejected").toArray();
}

export type IdMapEntry = { from: string; to: string };

function rewriteValue(value: unknown, map: Map<string, string>): unknown {
  if (typeof value === "string") {
    return map.get(value) ?? value;
  }
  if (Array.isArray(value)) {
    return value.map((item) => rewriteValue(item, map));
  }
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
      out[key] = rewriteValue(nested, map);
    }
    return out;
  }
  return value;
}

export async function applyIdMap(db: FloorDb, entries: IdMapEntry[]): Promise<void> {
  if (entries.length === 0) {
    return;
  }
  const map = new Map(entries.map((e) => [e.from, e.to]));
  await db.transaction("rw", db.outbox, db.locations, async () => {
    const rows = await db.outbox.toArray();
    for (const row of rows) {
      if (row.state === "confirmed" || row.state === "rejected") {
        continue;
      }
      await db.outbox.update(row.id, { payload: rewriteValue(row.payload, map) });
    }
    for (const [from, to] of map) {
      const loc = await db.locations.get(from);
      if (!loc) {
        continue;
      }
      await db.locations.delete(from);
      await db.locations.put({ ...loc, id: to });
    }
    const all = await db.locations.toArray();
    for (const loc of all) {
      if (loc.parent_id && map.has(loc.parent_id)) {
        await db.locations.update(loc.id, { parent_id: map.get(loc.parent_id) });
      }
    }
  });
}
