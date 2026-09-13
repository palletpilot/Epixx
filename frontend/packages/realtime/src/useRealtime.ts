import { onUnmounted } from "vue";
import { createRealtime, type RealtimeHandle, type RealtimeOptions } from "./createRealtime";

export function useRealtime(opts: RealtimeOptions): RealtimeHandle {
  const handle = createRealtime(opts);
  onUnmounted(() => handle.close());
  return handle;
}
