<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { liveQuery } from "dexie";
import { onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRoute, useRouter } from "vue-router";
import { getDb, type TaskRow } from "../db";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const floor = useFloorStore();
const task = ref<TaskRow | undefined>();
const id = String(route.params.id);
const sub = liveQuery(() => getDb().tasks.get(id)).subscribe({
  next: (row) => {
    task.value = row;
  },
});
onUnmounted(() => sub.unsubscribe());

async function claim() {
  if (task.value) {
    await floor.claimTask(task.value);
  }
}

async function complete() {
  if (task.value) {
    await floor.completeTask(task.value);
  }
}
</script>

<template>
  <section v-if="task">
    <h1 class="mb-2 text-xl font-semibold">{{ task.type }}</h1>
    <p class="mb-4" data-testid="task-status">{{ task.status }}</p>
    <div class="flex flex-col gap-2">
      <Button v-if="task.status === 'open'" class="min-h-12" @click="claim">{{ t("floor.claim") }}</Button>
      <Button v-if="task.status === 'claimed'" class="min-h-12" @click="complete">{{ t("floor.complete") }}</Button>
      <Button variant="outline" class="min-h-12" @click="router.push('/tasks')">{{ t("floor.back") }}</Button>
    </div>
  </section>
</template>
