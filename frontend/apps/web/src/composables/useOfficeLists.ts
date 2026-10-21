import { computed, ref, watch } from "vue";
import { emptyLists, loadOfficeLists, SyncLoadError, type OfficeLists } from "../client/syncClient";
import { syncUrl } from "../env";
import { useAuthStore } from "../stores/auth";
import { useSessionStore } from "../stores/session";

const lists = ref<OfficeLists>(emptyLists());
const loading = ref(false);
const loadError = ref(false);
let watching = false;

export function useOfficeLists() {
  const auth = useAuthStore();
  const session = useSessionStore();

  async function refresh(): Promise<void> {
    const warehouseId = session.selectedWarehouseId;
    if (!warehouseId || !auth.accessToken) {
      lists.value = emptyLists();
      loadError.value = false;
      return;
    }
    loading.value = true;
    try {
      lists.value = await loadOfficeLists(
        async (url) => {
          const res = await auth.authedFetch(url);
          if (!res.ok) {
            throw new SyncLoadError(res.status);
          }
          return res.json();
        },
        syncUrl(),
        warehouseId,
      );
      loadError.value = false;
    } catch {
      loadError.value = true;
      lists.value = emptyLists();
    } finally {
      loading.value = false;
    }
  }

  if (!watching) {
    watching = true;
    watch(
      () => session.selectedWarehouseId,
      () => {
        void refresh();
      },
    );
  }

  return {
    lists,
    stockRows: computed(() => [...lists.value.stock.values()]),
    deviationRows: computed(() => [...lists.value.deviations.values()]),
    deviationCount: computed(() => lists.value.deviations.size),
    loading,
    loadError,
    refresh,
  };
}
