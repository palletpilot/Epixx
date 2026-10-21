<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { compareDecimalStrings, compareStockLocations, formatDecimalSvSe } from "../client/syncClient";
import { useOfficeLists } from "../composables/useOfficeLists";
import { useTableCursor } from "../composables/useTableCursor";

const { t } = useI18n();
const searchRef = ref<HTMLInputElement | null>(null);
const searchText = ref("");
const { stockRows, loadError } = useOfficeLists();

const rows = computed(() => {
  const query = searchText.value.trim().toLowerCase();
  const next = query
    ? stockRows.value.filter(
        (row) =>
          locationLabel(row.location_id).toLowerCase().includes(query) ||
          row.handling_unit_id.toLowerCase().includes(query) ||
          row.article_id.toLowerCase().includes(query),
      )
    : [...stockRows.value];
  return next.sort((a, b) => {
    const location = compareStockLocations(a.location_id, b.location_id);
    if (location !== 0) {
      return location;
    }
    const qty = compareDecimalStrings(a.qty_base, b.qty_base);
    if (qty !== 0) {
      return qty;
    }
    return a.handling_unit_id.localeCompare(b.handling_unit_id);
  });
});

const { cursor, selected, onKeydown } = useTableCursor(computed(() => rows.value.length));

function locationLabel(locationId: string | null): string {
  return locationId ?? t("stock.noLocation");
}

function reservedHint(value: string): string {
  if (value === "0" || value === "0.0" || /^0(\.0+)?$/.test(value)) {
    return "";
  }
  return formatDecimalSvSe(value);
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
    <p v-if="loadError" role="alert">{{ t("stock.loadError") }}</p>
    <template v-else>
      <label class="mb-3 block max-w-xs text-sm">
        <span class="sr-only">{{ t("stock.search") }}</span>
        <input
          ref="searchRef"
          v-model="searchText"
          type="search"
          :placeholder="t('stock.search')"
          class="h-9 w-full rounded-md border border-input bg-background px-2 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        />
      </label>
      <div class="overflow-auto">
        <table
          class="w-full border-collapse text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
          tabindex="0"
          @keydown="onKeydown"
        >
          <caption class="sr-only">{{ t("stock.caption") }}</caption>
          <thead class="sticky top-0 bg-background">
            <tr class="border-b border-border">
              <th scope="col" class="sticky left-0 z-10 bg-background px-2 py-1 text-left font-semibold">
                {{ t("stock.location") }}
              </th>
              <th scope="col" class="sticky left-28 z-10 bg-background px-2 py-1 text-left font-semibold">
                {{ t("stock.huSku") }}
              </th>
              <th scope="col" class="px-2 py-1 text-right font-semibold">{{ t("stock.quantity") }}</th>
              <th scope="col" class="px-2 py-1 text-left font-semibold">{{ t("stock.reserved") }}</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="(row, index) in rows"
              :key="row.handling_unit_id"
              class="border-b border-border"
              :class="{
                'outline outline-2 outline-offset-[-2px] outline-ring': cursor === index,
                'font-medium': selected === index,
              }"
            >
              <td class="sticky left-0 z-10 bg-background px-2 py-1">{{ locationLabel(row.location_id) }}</td>
              <td class="sticky left-28 z-10 bg-background px-2 py-1">
                {{ row.handling_unit_id }} / {{ row.article_id }}
              </td>
              <td class="px-2 py-1 text-right tabular-nums">{{ formatDecimalSvSe(row.qty_base) }}</td>
              <td class="px-2 py-1 tabular-nums">{{ reservedHint(row.reserved_qty_base) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>
</template>
