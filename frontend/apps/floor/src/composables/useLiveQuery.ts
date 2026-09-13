import { liveQuery } from "dexie";
import { onUnmounted, ref, watch, type Ref } from "vue";
import { getDb, type OutboxRow, type TaskRow } from "../db";

export function useLiveTasks(warehouseId: Ref<string>): Ref<TaskRow[]> {
  const tasks = ref<TaskRow[]>([]);
  let sub: { unsubscribe(): void } | undefined;

  function start(): void {
    sub?.unsubscribe();
    const id = warehouseId.value;
    if (!id) {
      tasks.value = [];
      return;
    }
    sub = liveQuery(() => getDb().tasks.where("warehouse_id").equals(id).toArray()).subscribe({
      next: (rows) => {
        tasks.value = rows;
      },
    });
  }

  watch(warehouseId, start, { immediate: true });
  onUnmounted(() => sub?.unsubscribe());
  return tasks;
}

export function useLiveIssues(): Ref<OutboxRow[]> {
  const rows = ref<OutboxRow[]>([]);
  const sub = liveQuery(() => getDb().outbox.where("state").equals("rejected").toArray()).subscribe({
    next: (value) => {
      rows.value = value;
    },
  });
  onUnmounted(() => sub.unsubscribe());
  return rows;
}

export function useLivePending(): Ref<number> {
  const count = ref(0);
  const sub = liveQuery(async () => {
    const all = await getDb().outbox.toArray();
    return all.filter((r) => r.state === "pending" || r.state === "sent" || r.state === "acked").length;
  }).subscribe({
    next: (value) => {
      count.value = value;
    },
  });
  onUnmounted(() => sub.unsubscribe());
  return count;
}
