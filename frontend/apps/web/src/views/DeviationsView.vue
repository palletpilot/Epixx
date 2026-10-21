<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  deviationDetailParts,
  deviationLocationId,
  occupiedBinReservedQty,
  type DeviationRow,
} from "../client/syncClient";
import { useOfficeLists } from "../composables/useOfficeLists";
import { useTableCursor } from "../composables/useTableCursor";

const { t, locale } = useI18n();
const searchRef = ref<HTMLInputElement | null>(null);
const searchText = ref("");
const { deviationRows, loadError } = useOfficeLists();

const rows = computed(() => {
  const query = searchText.value.trim().toLowerCase();
  const next = query
    ? deviationRows.value.filter((row) => matches(row, query))
    : [...deviationRows.value];
  return next.sort((a, b) => {
    const created = a.created_at.localeCompare(b.created_at);
    if (created !== 0) {
      return created;
    }
    return a.id.localeCompare(b.id);
  });
});

const { cursor, selected, onKeydown } = useTableCursor(computed(() => rows.value.length));

function matches(row: DeviationRow, query: string): boolean {
  return (
    row.kind.toLowerCase().includes(query) ||
    (row.detail.location_id ?? "").toLowerCase().includes(query) ||
    (row.detail.suggested_location_id ?? "").toLowerCase().includes(query) ||
    (row.detail.lpn ?? "").toLowerCase().includes(query) ||
    (row.detail.existing_handling_unit_id ?? "").toLowerCase().includes(query) ||
    (row.detail.incoming_handling_unit_id ?? "").toLowerCase().includes(query)
  );
}

function kindLabel(kind: string): string {
  if (kind === "occupied_bin") {
    return t("deviations.occupiedBin");
  }
  if (kind === "wrong_location") {
    return t("deviations.wrongLocation");
  }
  return kind;
}

function formatDetail(row: DeviationRow): string {
  return deviationDetailParts(row).join(" · ");
}

function formatDate(value: string): string {
  if (!value) {
    return "";
  }
  return new Date(value).toLocaleString(locale.value === "sv" ? "sv-SE" : "en-GB");
}

function onSlash(event: KeyboardEvent): void {
  if (event.key !== "/") {
    return;
  }
  const target = event.target;
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) {
    return;
  }
  event.preventDefault();
  searchRef.value?.focus();
}

onMounted(() => {
  window.addEventListener("keydown", onSlash);
});

onUnmounted(() => {
  window.removeEventListener("keydown", onSlash);
});
</script>

<template>
  <section>
    <p v-if="loadError" role="alert">{{ t("deviations.loadError") }}</p>
    <template v-else>
      <label class="mb-3 block max-w-xs text-sm">
        <span class="sr-only">{{ t("deviations.search") }}</span>
        <input
          ref="searchRef"
          v-model="searchText"
          type="search"
          :placeholder="t('deviations.search')"
          class="h-9 w-full rounded-md border border-input bg-background px-2 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        />
      </label>
      <div class="overflow-auto">
        <table
          class="w-full border-collapse text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
          tabindex="0"
          @keydown="onKeydown"
        >
          <caption class="sr-only">{{ t("deviations.caption") }}</caption>
          <thead class="sticky top-0 bg-background">
            <tr class="border-b border-border">
              <th scope="col" class="sticky left-0 z-10 bg-background px-2 py-1 text-left font-semibold">
                {{ t("deviations.location") }}
              </th>
              <th scope="col" class="sticky left-28 z-10 bg-background px-2 py-1 text-left font-semibold">
                {{ t("deviations.kind") }}
              </th>
              <th scope="col" class="px-2 py-1 text-left font-semibold">{{ t("deviations.detail") }}</th>
              <th scope="col" class="px-2 py-1 text-left font-semibold">{{ t("deviations.createdAt") }}</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(row, index) in rows"
              :key="row.id"
              class="border-b border-border"
              :class="{
                'outline outline-2 outline-offset-[-2px] outline-ring': cursor === index,
                'font-medium': selected === index,
              }"
            >
              <td class="sticky left-0 z-10 bg-background px-2 py-1">{{ deviationLocationId(row) }}</td>
              <td class="sticky left-28 z-10 bg-background px-2 py-1">
                <span
                  class="inline-block rounded bg-destructive/10 px-2 py-0.5 text-xs font-semibold text-destructive"
                >
                  {{ kindLabel(row.kind) }}
                </span>
                <span
                  v-if="occupiedBinReservedQty(row)"
                  class="ml-1 inline-block rounded bg-muted px-2 py-0.5 text-xs font-semibold text-muted-foreground"
                >
                  {{ t("stock.reserved") }} {{ occupiedBinReservedQty(row) }}
                </span>
              </td>
              <td class="px-2 py-1">{{ formatDetail(row) }}</td>
              <td class="px-2 py-1">{{ formatDate(row.created_at) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>
</template>
