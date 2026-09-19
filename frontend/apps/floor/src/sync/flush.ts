import type { FloorDb } from "../db";
import {
  ACKED_RETRY_MS,
  applyIdMap,
  BATCH_SIZE,
  confirmCommand,
  markSent,
  requeueUnconfirmed,
  SENT_RETRY_MS,
  setOutboxState,
  shouldResend,
} from "./outbox";

export type CommandResult = {
  command_id?: string;
  commandId?: string;
  outcome: string;
  code?: string | null;
  details?: { id_map?: Array<{ from: string; to: string }> } | null;
};

export type FlushHttpResult = {
  status: number;
  results?: CommandResult[];
  min_app_version?: number;
  feed_epoch?: string;
};

export type FlushBatch = {
  now: string;
  commands: Array<{
    id: string;
    type: string;
    v: number;
    payload: unknown;
    occurred_at: string;
    device_id: string;
    user_id: string;
  }>;
};

export type FlushDeps = {
  db: FloorDb;
  now: () => Date;
  postCommands: (batch: FlushBatch) => Promise<FlushHttpResult>;
  lock: <T>(name: string, fn: () => Promise<T>) => Promise<T>;
  online?: boolean;
  onUpgradeRequired?: () => void;
  onRevoked?: () => void;
  onResync?: () => Promise<void> | void;
};

export type FlushState = {
  upgradeRequired: boolean;
  unknownBackoffUntil?: number;
};

const UNKNOWN_BACKOFF_MIN = 5_000;
const UNKNOWN_BACKOFF_MAX = 5 * 60_000;

export const flushState: FlushState = {
  upgradeRequired: false,
};

export async function webLock<T>(name: string, fn: () => Promise<T>): Promise<T> {
  const locks = navigator.locks;
  if (!locks?.request) {
    return fn();
  }
  return locks.request(name, () => fn());
}

function resultId(result: CommandResult): string {
  return result.command_id ?? result.commandId ?? "";
}

function outcomeOf(result: CommandResult): string {
  return result.outcome.toLowerCase();
}

export async function flushOutbox(deps: FlushDeps): Promise<void> {
  if (flushState.upgradeRequired) {
    return;
  }
  const unknownUntil = flushState.unknownBackoffUntil;
  if (unknownUntil && deps.now().getTime() < unknownUntil) {
    return;
  }

  await deps.lock("sync", async () => {
    for (;;) {
    const now = deps.now();
    const online = deps.online ?? true;
    const rows = (await deps.db.outbox.toArray())
      .filter((row) => shouldResend(row, now, online))
      .sort((a, b) => a.created_at.localeCompare(b.created_at))
      .slice(0, BATCH_SIZE);
    if (rows.length === 0) {
      return;
    }

    const mapping =
      rows[0]?.type === "CreateLocationBatch" || rows[0]?.type === "SetLocationDimensions";
    const batchRows = rows.slice(0, mapping ? 1 : BATCH_SIZE);
    if (batchRows.length === 0) {
      return;
    }

    const sentAt = now.toISOString();
    await markSent(deps.db, batchRows.map((r) => r.id), sentAt);

    let http: FlushHttpResult;
    try {
      http = await deps.postCommands({
        now: sentAt,
        commands: batchRows.map((row) => ({
          id: row.id,
          type: row.type,
          v: row.v,
          payload: row.payload,
          occurred_at: row.occurred_at,
          device_id: row.device_id,
          user_id: row.user_id,
        })),
      });
    } catch {
      return;
    }

    if (http.status === 410) {
      deps.onRevoked?.();
      return;
    }
    if (http.status === 426) {
      flushState.upgradeRequired = true;
      for (const row of batchRows) {
        await setOutboxState(deps.db, row.id, "pending", { sent_at: undefined });
      }
      deps.onUpgradeRequired?.();
      return;
    }

    const results = http.results ?? [];
    let heldSeen = false;
    let unknownSeen = false;
    for (let i = 0; i < batchRows.length; i++) {
      const row = batchRows[i];
      if (!row) {
        continue;
      }
      if (heldSeen) {
        await setOutboxState(deps.db, row.id, "pending", { sent_at: undefined });
        continue;
      }
      const result = results.find((r) => resultId(r) === row.id) ?? results[i];
      if (!result) {
        unknownSeen = true;
        await setOutboxState(deps.db, row.id, "pending", { sent_at: undefined });
        continue;
      }
      const outcome = outcomeOf(result);
      if (outcome === "applied") {
        const idMap = result.details?.id_map;
        if (idMap && idMap.length > 0) {
          await applyIdMap(deps.db, idMap);
        }
        await setOutboxState(deps.db, row.id, "acked");
        continue;
      }
      if (outcome === "rejected") {
        await setOutboxState(deps.db, row.id, "rejected");
        continue;
      }
      if (outcome === "held") {
        heldSeen = true;
        await setOutboxState(deps.db, row.id, "pending", { sent_at: undefined });
        continue;
      }
      unknownSeen = true;
      await setOutboxState(deps.db, row.id, "pending", { sent_at: undefined });
    }

    if (unknownSeen) {
      const prev = flushState.unknownBackoffUntil
        ? Math.min(UNKNOWN_BACKOFF_MAX, (flushState.unknownBackoffUntil - now.getTime()) * 2)
        : UNKNOWN_BACKOFF_MIN;
      const jitter = Math.floor(Math.random() * 1000);
      flushState.unknownBackoffUntil = now.getTime() + Math.max(UNKNOWN_BACKOFF_MIN, prev) + jitter;
    } else {
      flushState.unknownBackoffUntil = undefined;
    }

    if (!mapping || heldSeen || unknownSeen) {
      return;
    }
    }
  });
}

export async function applyFeedEntries(
  db: FloorDb,
  warehouseId: string,
  entries: Array<{
    seq: number;
    entity: string;
    id: string;
    op: string;
    payload: unknown;
    command_id?: string | null;
  }>,
  feedEpoch: string,
  snapshotSchema: string,
  now: Date,
): Promise<"ok" | "epoch" | "regression"> {
  const existing = await db.cursor.get(warehouseId);
  if (existing && existing.feed_epoch && existing.feed_epoch !== feedEpoch) {
    await requeueUnconfirmed(db);
    await db.transaction("rw", db.cursor, db.tasks, async () => {
      await db.tasks.where("warehouse_id").equals(warehouseId).delete();
      await db.cursor.put({
        warehouse_id: warehouseId,
        seq: 0,
        feed_epoch: feedEpoch,
        snapshot_schema: snapshotSchema,
      });
    });
    return "epoch";
  }

  const incomingMax = entries.reduce((m, e) => Math.max(m, e.seq), 0);
  if (existing && incomingMax > 0 && incomingMax < existing.seq) {
    await requeueUnconfirmed(db);
    return "regression";
  }
  const maxSeq = Math.max(incomingMax, existing?.seq ?? 0);

  await db.transaction("rw", db.outbox, db.confirmed, db.tasks, db.cursor, async () => {
    const ordered = [...entries].sort((a, b) => a.seq - b.seq);
    for (const entry of ordered) {
      if (entry.command_id) {
        await confirmCommand(db, entry.command_id, now.toISOString());
      }
      if (entry.entity.toLowerCase() !== "task") {
        continue;
      }
      if (entry.op === "delete") {
        await db.tasks.delete(entry.id);
        continue;
      }
      const task = asTask(entry);
      if (task) {
        const prev = await db.tasks.get(task.id);
        await db.tasks.put({
          ...task,
          requested_qty_base: task.requested_qty_base ?? prev?.requested_qty_base,
          order_id: task.order_id ?? prev?.order_id,
          tote_id: task.tote_id ?? prev?.tote_id,
          shipped: task.shipped || prev?.shipped,
        });
      }
    }
    await db.cursor.put({
      warehouse_id: warehouseId,
      seq: maxSeq,
      feed_epoch: feedEpoch,
      snapshot_schema: snapshotSchema,
    });
  });
  return "ok";
}

function asTask(entry: {
  id: string;
  payload: unknown;
}): import("../db").TaskRow | null {
  if (typeof entry.payload !== "object" || entry.payload === null) {
    return { id: entry.id, warehouse_id: "", type: "", status: "" };
  }
  const p = entry.payload as Record<string, unknown>;
  return {
    id: typeof p.id === "string" ? p.id : entry.id,
    warehouse_id: String(p.warehouse_id ?? ""),
    type: String(p.type ?? ""),
    status: String(p.status ?? ""),
    assignee_user_id: p.assignee_user_id == null ? null : String(p.assignee_user_id),
    assigned_until: p.assigned_until == null ? null : String(p.assigned_until),
    suggested_location_id: p.suggested_location_id == null ? null : String(p.suggested_location_id),
    created_at: p.created_at == null ? undefined : String(p.created_at),
    requested_qty_base: typeof p.requested_qty_base === "string" ? p.requested_qty_base : undefined,
    order_id: typeof p.order_id === "string" ? p.order_id : undefined,
    tote_id: typeof p.tote_id === "string" ? p.tote_id : undefined,
    shipped: p.shipped === true,
  };
}

export { SENT_RETRY_MS, ACKED_RETRY_MS, BATCH_SIZE };
