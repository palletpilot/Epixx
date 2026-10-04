<script setup lang="ts">
import { onMounted } from "vue";
import { RouterLink, RouterView } from "vue-router";
import { useI18n } from "vue-i18n";
import { storeToRefs } from "pinia";
import { useOfficeLists } from "../composables/useOfficeLists";
import { useSessionStore } from "../stores/session";

const { t } = useI18n();
const session = useSessionStore();
const { selectedWarehouseId, warehouses } = storeToRefs(session);
const { openDeviationCount, refresh } = useOfficeLists();

onMounted(async () => {
  await session.ensureWarehouse();
  await refresh();
});
</script>

<template>
  <section>
    <header class="mb-4 flex flex-wrap items-end justify-between gap-3">
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("stock.warehouse") }}</span>
        <select
          v-model="selectedWarehouseId"
          class="h-9 rounded-md border border-input bg-background px-2 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
          :aria-label="t('stock.warehouse')"
        >
          <option v-for="warehouse in warehouses" :key="warehouse.id" :value="warehouse.id">
            {{ warehouse.name }}
          </option>
        </select>
      </label>
    </header>

    <nav class="mb-4 inline-flex rounded-md border border-border p-0.5" :aria-label="t('stock.sections')">
      <RouterLink
        to="/app/stock"
        class="rounded px-3 py-1.5 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        active-class="bg-background font-semibold shadow-sm"
      >
        {{ t("stock.title") }}
      </RouterLink>
      <RouterLink
        to="/app/deviations"
        class="inline-flex items-center gap-2 rounded px-3 py-1.5 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        active-class="bg-background font-semibold shadow-sm"
      >
        {{ t("deviations.title") }}
        <span
          v-if="openDeviationCount > 0"
          class="inline-flex min-w-5 items-center justify-center rounded-full bg-destructive px-1.5 text-xs font-semibold text-destructive-foreground"
        >
          {{ openDeviationCount }}
        </span>
      </RouterLink>
    </nav>

    <RouterView />
  </section>
</template>
