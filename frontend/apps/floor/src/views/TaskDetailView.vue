<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { liveQuery } from "dexie";
import { computed, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRoute, useRouter } from "vue-router";
import { getDb, type LocationRow, type TaskRow } from "../db";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const floor = useFloorStore();
const task = ref<TaskRow | undefined>();
const locations = ref<LocationRow[]>([]);
const error = ref<string | null>(null);
const id = String(route.params.id);
const taskSub = liveQuery(() => getDb().tasks.get(id)).subscribe({
  next: (row) => {
    task.value = row;
  },
});
const locSub = liveQuery(() => getDb().locations.toArray()).subscribe({
  next: (rows) => {
    locations.value = rows;
  },
});
onUnmounted(() => {
  taskSub.unsubscribe();
  locSub.unsubscribe();
});

const isPutaway = computed(() => task.value?.type === "putaway");
const isPick = computed(() => task.value?.type === "pick");

const mappedBins = computed(() =>
  locations.value
    .filter(
      (l) =>
        l.type === "bin" &&
        !l.is_system &&
        (l.status ?? "active") === "active" &&
        (!task.value || l.warehouse_id === task.value.warehouse_id),
    )
    .slice()
    .sort((a, b) => a.code.localeCompare(b.code)),
);

const suggested = computed(() => {
  const sid = task.value?.suggested_location_id;
  if (sid) {
    return locations.value.find((l) => l.id === sid) ?? mappedBins.value[0];
  }
  return mappedBins.value[0];
});

const heading = computed(() => {
  if (isPick.value) {
    return t("pick.title");
  }
  return task.value?.type ?? "";
});

const canShip = computed(
  () =>
    isPick.value &&
    task.value?.status === "done" &&
    !task.value.shipped &&
    Boolean(task.value.order_id) &&
    Boolean(task.value.tote_id),
);

async function claim() {
  if (task.value) {
    await floor.claimTask(task.value);
  }
}

async function complete() {
  error.value = null;
  if (!task.value) {
    return;
  }
  if (isPutaway.value) {
    const loc = suggested.value;
    if (!loc) {
      error.value = t("floor.error");
      return;
    }
    await floor.confirmPutaway(task.value, loc.id);
    return;
  }
  if (isPick.value) {
    await floor.confirmPick(task.value);
    return;
  }
  await floor.completeTask(task.value);
}

async function ship() {
  error.value = null;
  if (task.value) {
    await floor.shipAsPicked(task.value);
  }
}
</script>

<template>
  <section v-if="task">
    <h1 class="mb-2 text-xl font-semibold">{{ heading }}</h1>
    <p class="mb-4" data-testid="task-status">{{ task.status }}</p>
    <p v-if="isPutaway && suggested" class="mb-4">
      {{ t("putaway.suggested") }}: {{ suggested.code }}
    </p>
    <p v-if="isPick && suggested" class="mb-4">
      {{ t("pick.source") }}: {{ suggested.code }}
    </p>
    <p v-if="error" role="alert" class="mb-4 text-destructive">{{ error }}</p>
    <div class="flex flex-col gap-2">
      <Button v-if="task.status === 'open'" class="min-h-12" @click="claim">{{ t("floor.claim") }}</Button>
      <Button v-if="task.status === 'claimed' && isPutaway" class="min-h-12" @click="complete">
        {{ t("putaway.confirm") }}
      </Button>
      <Button v-if="task.status === 'claimed' && isPick" class="min-h-12" @click="complete">
        {{ t("pick.confirm") }}
      </Button>
      <Button v-if="task.status === 'claimed' && !isPutaway && !isPick" class="min-h-12" @click="complete">
        {{ t("floor.complete") }}
      </Button>
      <Button v-if="canShip" class="min-h-12" @click="ship">{{ t("pick.ship") }}</Button>
      <Button variant="outline" class="min-h-12" @click="router.push('/tasks')">{{ t("floor.back") }}</Button>
    </div>
  </section>
</template>
