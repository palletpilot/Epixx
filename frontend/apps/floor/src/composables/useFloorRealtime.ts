import { createRealtime, type RealtimeHandle } from "@lagerkraft/realtime";
import { onUnmounted, watch } from "vue";
import { getDb } from "../db";
import { syncUrl } from "../env";
import { useFloorStore } from "../stores/floor";

export function useFloorRealtime(): void {
  const floor = useFloorStore();
  let handle: RealtimeHandle | undefined;

  async function start(): Promise<void> {
    handle?.close();
    handle = undefined;
    floor.sseConnected = false;
    if (!floor.unlocked || !floor.warehouseId || !floor.accessToken || !floor.online) {
      return;
    }
    const cursor = await getDb().cursor.get(floor.warehouseId);
    handle = createRealtime({
      url: `${syncUrl()}/realtime`,
      token: floor.accessToken,
      warehouse: floor.warehouseId,
      since: cursor?.seq,
      onEntries(entries) {
        const epoch = cursor?.feed_epoch ?? "";
        void floor.applyEntries(
          entries.map((e) => ({
            seq: e.seq,
            entity: e.entity,
            id: e.id,
            op: e.op,
            payload: e.payload,
            command_id: e.command_id ?? null,
          })),
          epoch,
        );
      },
      onResync() {
        void floor.loadSnapshot(floor.warehouseId);
      },
    });
    floor.sseConnected = true;
  }

  watch(
    () => [floor.unlocked, floor.warehouseId, floor.accessToken, floor.online],
    () => {
      void start();
    },
    { immediate: true },
  );
  onUnmounted(() => handle?.close());
}
