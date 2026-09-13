<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { computed, onMounted } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();

const options = computed(() => {
  const ids = floor.device?.warehouse_ids ?? [];
  if (ids.length === 0) {
    return ["01900000-0000-7000-8000-000000000001"];
  }
  return ids;
});

onMounted(() => {
  if (options.value[0] && !floor.warehouseId) {
    floor.warehouseId = options.value[0];
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
      <li v-for="id in options" :key="id">
        <Button class="min-h-12 w-full" @click="pick(id)">{{ id }}</Button>
      </li>
    </ul>
  </section>
</template>
