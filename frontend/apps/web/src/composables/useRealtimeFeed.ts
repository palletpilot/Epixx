import { createRealtime, type ChangeEntry, type RealtimeHandle } from "@lagerkraft/realtime";
import { onUnmounted, ref, toValue, watch, type MaybeRefOrGetter, type Ref } from "vue";

export type TaskRow = {
  id: string;
  warehouse_id: string;
  type: string;
  status: string;
  assignee_user_id?: string | null;
  assigned_until?: string | null;
  suggested_location_id?: string | null;
  created_at?: string;
};

export type LastActivity = {
  actor: string;
  occurredAt: string;
};

export function applyRealtimeEntries(
  tasks: Map<string, TaskRow>,
  entries: ChangeEntry[],
): { tasks: Map<string, TaskRow>; lastActivity?: LastActivity } {
  const next = new Map(tasks);
  let lastActivity: LastActivity | undefined;
  const ordered = [...entries].sort((a, b) => a.seq - b.seq);
  for (const entry of ordered) {
    if (entry.actor || entry.occurred_at) {
      lastActivity = {
        actor: String(entry.actor ?? ""),
        occurredAt: entry.occurred_at,
      };
    }
    if (entry.entity.toLowerCase() !== "task") {
      continue;
    }
    if (entry.op === "delete") {
      next.delete(entry.id);
      continue;
    }
    const payload = asTask(entry);
    if (payload) {
      next.set(payload.id, payload);
    }
  }
  return { tasks: next, lastActivity };
}

function asTask(entry: ChangeEntry): TaskRow | null {
  if (typeof entry.payload !== "object" || entry.payload === null) {
    return { id: entry.id, warehouse_id: "", type: "", status: "" };
  }
  const p = entry.payload as Record<string, unknown>;
  const id = typeof p.id === "string" ? p.id : entry.id;
  return {
    id,
    warehouse_id: String(p.warehouse_id ?? ""),
    type: String(p.type ?? ""),
    status: String(p.status ?? ""),
    assignee_user_id: p.assignee_user_id == null ? null : String(p.assignee_user_id),
    assigned_until: p.assigned_until == null ? null : String(p.assigned_until),
    suggested_location_id: p.suggested_location_id == null ? null : String(p.suggested_location_id),
    created_at: p.created_at == null ? undefined : String(p.created_at),
  };
}

export function useRealtimeFeed(opts: {
  url: MaybeRefOrGetter<string>;
  token: MaybeRefOrGetter<string>;
  warehouse?: MaybeRefOrGetter<string | undefined>;
  since?: number;
  enabled?: MaybeRefOrGetter<boolean>;
}): {
  tasks: Ref<Map<string, TaskRow>>;
  lastActivity: Ref<LastActivity | undefined>;
} {
  const tasks = ref(new Map<string, TaskRow>());
  const lastActivity = ref<LastActivity | undefined>();
  let handle: RealtimeHandle | undefined;

  function start(): void {
    handle?.close();
    handle = undefined;
    const token = toValue(opts.token);
    const enabled = opts.enabled === undefined ? true : toValue(opts.enabled);
    if (!enabled || !token) {
      return;
    }
    handle = createRealtime({
      url: toValue(opts.url),
      token,
      warehouse: toValue(opts.warehouse),
      since: opts.since,
      onEntries(entries) {
        const applied = applyRealtimeEntries(tasks.value, entries);
        tasks.value = applied.tasks;
        if (applied.lastActivity) {
          lastActivity.value = applied.lastActivity;
        }
      },
    });
  }

  watch(
    () => [toValue(opts.url), toValue(opts.token), toValue(opts.warehouse), toValue(opts.enabled)],
    start,
    { immediate: true },
  );
  onUnmounted(() => handle?.close());
  return { tasks, lastActivity };
}
