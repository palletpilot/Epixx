<script setup lang="ts">
import { computed, onMounted } from "vue";
import { RouterLink } from "vue-router";
import { useI18n } from "vue-i18n";
import { useLiveTasks } from "../composables/useLiveQuery";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const warehouseId = computed(() => floor.warehouseId);
const tasks = useLiveTasks(warehouseId);

onMounted(() => {
  if (floor.warehouseId) {
    void floor.loadSnapshot(floor.warehouseId);
  }
});
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("floor.tasks") }}</h1>
    <p v-if="!floor.warehouseId">
      <RouterLink to="/warehouses">{{ t("floor.warehouseTitle") }}</RouterLink>
    </p>
    <ul :aria-label="t('tasks.list')" class="flex flex-col gap-2">
      <li v-for="task in tasks" :key="task.id">
        <RouterLink class="block min-h-12 rounded-md border border-border px-3 py-3" :to="`/tasks/${task.id}`">
          {{ task.type }} · {{ task.status }}
        </RouterLink>
      </li>
    </ul>
    <p v-if="tasks.length === 0">{{ t("tasks.empty") }}</p>
  </section>
</template>
