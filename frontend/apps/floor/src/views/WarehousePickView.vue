<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { computed, onMounted } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useFloorStore } from "../stores/floor";
import { warehousePickOptions } from "../stores/warehousePick";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();

const options = computed(() => warehousePickOptions(floor.device?.warehouse_ids, floor.warehouses));

onMounted(async () => {
  await floor.loadWarehouses();
  const first = options.value[0];
  if (first && !floor.warehouseId) {
    floor.warehouseId = first.id;
  }
});

async function pick(id: string) {
  floor.warehouseId = id;
  await floor.loadSnapshot(id);
  await router.push("/tasks");
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("floor.warehouseTitle") }}</h1>
    <ul class="flex flex-col gap-2">
      <li v-for="w in options" :key="w.id">
        <Button class="min-h-12 w-full" @click="pick(w.id)">{{ w.name }}</Button>
      </li>
    </ul>
  </section>
</template>
