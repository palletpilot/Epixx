<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useOfficeLists } from "../composables/useOfficeLists";
import { useTableCursor } from "../composables/useTableCursor";

const { t } = useI18n();
const searchRef = ref<HTMLInputElement | null>(null);
const searchText = ref("");
const { stockRows } = useOfficeLists();

const rows = computed(() => {
  const query = searchText.value.trim().toLowerCase();
  const next = query
    ? stockRows.value.filter(
        (row) =>
          row.location_id.toLowerCase().includes(query) ||
          row.handling_unit_id.toLowerCase().includes(query) ||
          (row.article_id ?? "").toLowerCase().includes(query),
      )
    : [...stockRows.value];
  return next.sort((a, b) => {
    const location = a.location_id.localeCompare(b.location_id);
    if (location !== 0) {
      return location;
    }
    return a.id.localeCompare(b.id);
  });
});

const { cursor, selected, onKeydown } = useTableCursor(computed(() => rows.value.length));

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
            :key="row.id"
            class="border-b border-border"
            :class="{
              'outline outline-2 outline-offset-[-2px] outline-ring': cursor === index,
              'font-medium': selected === index,
            }"
          >
            <td class="sticky left-0 z-10 bg-background px-2 py-1">{{ row.location_id }}</td>
            <td class="sticky left-28 z-10 bg-background px-2 py-1">
              {{ row.handling_unit_id }} / {{ row.article_id ?? t("stock.none") }}
            </td>
            <td class="px-2 py-1 text-right tabular-nums">{{ row.qty_base }}</td>
            <td class="px-2 py-1" />
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
