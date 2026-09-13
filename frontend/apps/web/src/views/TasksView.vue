<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { useQuery } from "@tanstack/vue-query";
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { applyRealtimeEntries, useRealtimeFeed, type TaskRow } from "../composables/useRealtimeFeed";
import { syncUrl } from "../env";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";

const { t, locale } = useI18n();
const auth = useAuthStore();
const tenant = useTenantStore();
const warehouseId = ref("");
const type = ref("pick");
const enabled = computed(() => Boolean(auth.accessToken && warehouseId.value));

onMounted(async () => {
  await tenant.refresh();
  warehouseId.value = tenant.warehouses[0]?.id ?? "";
});

const snapshot = useQuery({
  queryKey: computed(() => ["tasks", warehouseId.value]),
  enabled,
  queryFn: async () => {
    const res = await auth.authedFetch(
      `${syncUrl()}/sync/snapshot?warehouse=${warehouseId.value}&entity=Task`,
    );
    if (!res.ok) {
      return [] as TaskRow[];
    }
    const body = (await res.json()) as { items?: TaskRow[] };
    return body.items ?? [];
  },
});

const feed = useRealtimeFeed({
  url: `${syncUrl()}/realtime`,
  token: () => auth.accessToken ?? "",
  warehouse: warehouseId,
  enabled,
});

watch(
  () => snapshot.data.value,
  (items) => {
    if (!items) {
      return;
    }
    const mapped = items.map((item) => ({
      seq: 0,
      entity: "task",
      id: item.id,
      op: "upsert",
      payload: item,
      occurred_at: item.created_at ?? "",
      actor: null,
    }));
    const applied = applyRealtimeEntries(new Map(), mapped);
    feed.tasks.value = applied.tasks;
  },
);

const rows = computed(() => [...feed.tasks.value.values()]);
const lastLine = computed(() => {
  const last = feed.lastActivity.value;
  if (!last) {
    return t("tasks.lastActivityNone");
  }
  const time = new Date(last.occurredAt).toLocaleString(locale.value === "sv" ? "sv-SE" : "en-GB");
  return t("tasks.lastActivity", { actor: last.actor, time });
});

async function createTask() {
  if (!auth.userId || !warehouseId.value) {
    return;
  }
  const id = crypto.randomUUID();
  const taskId = crypto.randomUUID();
  await auth.authedFetch(`${syncUrl()}/sync/commands`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      now: new Date().toISOString(),
      commands: [
        {
          id,
          type: "CreateTask",
          v: 1,
          payload: { id: taskId, warehouse_id: warehouseId.value, type: type.value },
          occurred_at: new Date().toISOString(),
          device_id: "00000000-0000-0000-0000-000000000000",
          user_id: auth.userId,
        },
      ],
    }),
  });
  await snapshot.refetch();
}
</script>

<template>
  <section>
    <h1 class="mb-2 text-xl font-semibold">{{ t("tasks.title") }}</h1>
    <p class="mb-4 text-sm" role="status">{{ lastLine }}</p>
    <form class="mb-6 flex flex-wrap items-end gap-2" @submit.prevent="createTask">
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("tasks.warehouse") }}</span>
        <select
          v-model="warehouseId"
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('tasks.warehouse')"
        >
          <option v-for="w in tenant.warehouses" :key="w.id" :value="w.id">{{ w.name }}</option>
        </select>
      </label>
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("tasks.type") }}</span>
        <select
          v-model="type"
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('tasks.type')"
        >
          <option value="putaway">putaway</option>
          <option value="pick">pick</option>
          <option value="move">move</option>
          <option value="count">count</option>
        </select>
      </label>
      <Button type="submit">{{ t("tasks.create") }}</Button>
    </form>
    <ul :aria-label="t('tasks.list')" class="flex flex-col gap-2">
      <li v-for="row in rows" :key="row.id" class="rounded-md border border-border px-3 py-2">
        {{ row.type }} · {{ row.status }}
      </li>
    </ul>
    <p v-if="rows.length === 0">{{ t("tasks.empty") }}</p>
  </section>
</template>
