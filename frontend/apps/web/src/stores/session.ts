import { defineStore } from "pinia";
import { computed, ref } from "vue";
import { useTenantStore } from "./tenant";

export const useSessionStore = defineStore("session", () => {
  const tenant = useTenantStore();
  const selectedWarehouseId = ref<string | null>(null);

  const warehouses = computed(() => tenant.warehouses);

  async function ensureWarehouse(): Promise<void> {
    await tenant.refresh();
    if (!selectedWarehouseId.value) {
      selectedWarehouseId.value = tenant.warehouses[0]?.id ?? null;
    }
  }

  function setWarehouse(id: string): void {
    selectedWarehouseId.value = id;
  }

  return { selectedWarehouseId, warehouses, ensureWarehouse, setWarehouse };
});
