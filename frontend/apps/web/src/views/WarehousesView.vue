<script setup lang="ts">
import { AisleMap, Button, EmptyState, Input } from "@lagerkraft/ui";
import { useQuery } from "@tanstack/vue-query";
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { applyRealtimeEntries, useRealtimeFeed, type LocationRow } from "../composables/useRealtimeFeed";
import { syncUrl } from "../env";
import { DEFAULT_PATTERN, previewLocationCode } from "../layout/previewLocationCode";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";

const { t } = useI18n();
const tenant = useTenantStore();
const auth = useAuthStore();
const name = ref("");
const pattern = ref(DEFAULT_PATTERN);
const more = ref(false);
const claimMinutes = ref("30");
const nightShift = ref(false);
const blindCount = ref(false);
const zonePicking = ref(false);
const selectedId = ref("");
const example = computed(() => previewLocationCode(pattern.value));
const enabled = computed(() => Boolean(auth.accessToken && selectedId.value));
const selected = computed(() => tenant.warehouses.find((w) => w.id === selectedId.value));

onMounted(async () => {
  await tenant.refresh();
  selectedId.value = tenant.warehouses[0]?.id ?? "";
});

watch(
  () => tenant.warehouses,
  (list) => {
    if (!selectedId.value && list[0]) {
      selectedId.value = list[0].id;
    }
  },
);

const snapshot = useQuery({
  queryKey: computed(() => ["locations", selectedId.value]),
  enabled,
  queryFn: async () => {
    const res = await auth.authedFetch(
      `${syncUrl()}/sync/snapshot?warehouse=${selectedId.value}&entity=Location`,
    );
    if (!res.ok) {
      return [] as LocationRow[];
    }
    const body = (await res.json()) as { items?: LocationRow[] };
    return body.items ?? [];
  },
});

const feed = useRealtimeFeed({
  url: `${syncUrl()}/realtime`,
  token: () => auth.accessToken ?? "",
  warehouse: selectedId,
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
      entity: "location",
      id: item.id,
      op: "upsert",
      payload: item,
      occurred_at: "",
      actor: null,
    }));
    const applied = applyRealtimeEntries(new Map(), mapped, new Map());
    feed.locations.value = applied.locations;
  },
);

const mapLocations = computed(() =>
  [...feed.locations.value.values()].filter((l) => l.warehouse_id === selectedId.value),
);

async function create() {
  await tenant.createWarehouse({
    name: name.value,
    code_pattern: pattern.value,
    claim_minutes: Number(claimMinutes.value) || 30,
    night_shift: nightShift.value,
    blind_count: blindCount.value,
    zone_picking: zonePicking.value,
  });
  name.value = "";
  pattern.value = DEFAULT_PATTERN;
  if (!selectedId.value && tenant.warehouses[0]) {
    selectedId.value = tenant.warehouses[0].id;
  }
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("warehouses.title") }}</h1>
    <form class="mb-6 flex max-w-md flex-col gap-3" @submit.prevent="create">
      <Input id="wh-name" v-model="name" :label="t('warehouses.name')" />
      <Input id="wh-pattern" v-model="pattern" :label="t('warehouses.pattern')" />
      <p class="text-sm text-foreground/70">{{ t("warehouses.example", { code: example }) }}</p>
      <details class="text-sm" :open="more" @toggle="more = ($event.target as HTMLDetailsElement).open">
        <summary>{{ t("warehouses.more") }}</summary>
        <div class="mt-2 flex flex-col gap-2">
          <Input id="wh-claim" v-model="claimMinutes" type="number" :label="t('warehouses.claimMinutes')" />
          <label class="flex items-center gap-2">
            <input v-model="nightShift" type="checkbox" />
            {{ t("warehouses.nightShift") }}
          </label>
          <label class="flex items-center gap-2">
            <input v-model="blindCount" type="checkbox" />
            {{ t("warehouses.blindCount") }}
          </label>
          <label class="flex items-center gap-2">
            <input v-model="zonePicking" type="checkbox" />
            {{ t("warehouses.zonePicking") }}
          </label>
        </div>
      </details>
      <Button type="submit">{{ t("warehouses.create") }}</Button>
    </form>

    <EmptyState v-if="tenant.warehouses.length === 0" :title="t('warehouses.empty')">
      {{ t("warehouses.emptyAction") }}
    </EmptyState>

    <ul v-else :aria-label="t('warehouses.list')" class="mb-6 flex flex-col gap-2">
      <li v-for="w in tenant.warehouses" :key="w.id">
        <button
          type="button"
          class="w-full rounded-md border border-border px-3 py-2 text-left"
          :aria-current="w.id === selectedId ? 'true' : undefined"
          @click="selectedId = w.id"
        >
          {{ w.name }}
        </button>
      </li>
    </ul>

    <AisleMap
      v-if="selected"
      :locations="mapLocations"
      :empty="t('warehouses.mapEmpty')"
    />
  </section>
</template>
