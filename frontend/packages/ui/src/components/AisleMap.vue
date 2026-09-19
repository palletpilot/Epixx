<script setup lang="ts">
import { computed } from "vue";

export type MapLocation = {
  id: string;
  code: string;
  type: string;
  parent_id?: string | null;
};

const props = defineProps<{
  locations: MapLocation[];
  empty?: string;
}>();

function byCode(a: MapLocation, b: MapLocation): number {
  return a.code.localeCompare(b.code);
}

const aisles = computed(() => {
  const list = props.locations;
  const aisleRows = list.filter((l) => l.type === "aisle").sort(byCode);
  const racks = list.filter((l) => l.type === "rack");
  const levels = list.filter((l) => l.type === "level");
  const bins = list.filter((l) => l.type === "bin");
  return aisleRows.map((aisle) => {
    const aisleRacks = racks.filter((r) => r.parent_id === aisle.id).sort(byCode);
    const columns = aisleRacks.map((rack) => {
      const rackLevels = levels.filter((lv) => lv.parent_id === rack.id).sort(byCode);
      return {
        rack,
        rows: rackLevels.map((level) => ({
          level,
          bins: bins.filter((b) => b.parent_id === level.id).sort(byCode),
        })),
      };
    });
    const rowCount = Math.max(0, ...columns.map((c) => c.rows.length));
    return { aisle, columns, rowCount };
  });
});
</script>

<template>
  <div v-if="aisles.length === 0" class="text-sm text-foreground/70">{{ empty }}</div>
  <div v-else class="flex flex-col gap-8">
    <section v-for="block in aisles" :key="block.aisle.id">
      <h2 class="mb-3 text-sm font-medium">{{ block.aisle.code }}</h2>
      <div
        class="grid gap-3"
        :style="{ gridTemplateColumns: `repeat(${Math.max(block.columns.length, 1)}, minmax(5rem, 1fr))` }"
      >
        <div
          v-for="col in block.columns"
          :key="col.rack.id"
          class="text-center font-mono text-xs font-medium text-foreground/70"
        >
          {{ col.rack.code }}
        </div>
        <template v-for="rowIndex in block.rowCount" :key="`${block.aisle.id}-${rowIndex}`">
          <div v-for="col in block.columns" :key="`${col.rack.id}-${rowIndex}`" class="flex flex-col gap-1">
            <div
              v-for="bin in col.rows[rowIndex - 1]?.bins ?? []"
              :key="bin.id"
              class="flex min-h-12 items-center justify-center rounded-md border border-border bg-primary/15 px-1 py-1 text-center font-mono text-xs"
              :data-location-id="bin.id"
              :data-code="bin.code"
            >
              {{ bin.code }}
            </div>
          </div>
        </template>
      </div>
    </section>
  </div>
</template>
