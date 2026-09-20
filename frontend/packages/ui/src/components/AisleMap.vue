<script setup lang="ts">
import { computed } from "vue";
import {
  formatMapLabel,
  groupAisleMap,
  lastSegment,
  type AisleBlock,
  type AisleRackColumn,
  type MapLocation,
} from "./aisleMapLayout";

export type { MapLocation };

const props = defineProps<{
  locations: MapLocation[];
  empty?: string;
  aisleLabel?: string;
  rackLabel?: string;
  levelLabel?: string;
  legend?: string;
}>();

const aisles = computed(() => groupAisleMap(props.locations));

function binsAt(col: AisleRackColumn, rowIndex: number): MapLocation[] {
  return col.rows[rowIndex]?.bins ?? [];
}

function levelHeading(block: AisleBlock, rowIndex: number): string {
  for (const col of block.columns) {
    const row = col.rows[rowIndex];
    if (row) {
      return formatMapLabel(props.levelLabel, lastSegment(row.level.code));
    }
  }
  return "";
}
</script>

<template>
  <div v-if="aisles.length === 0" class="text-sm text-foreground/70">{{ empty }}</div>
  <div v-else class="flex flex-col gap-8">
    <p v-if="legend" class="text-sm text-foreground/70">{{ legend }}</p>
    <section v-for="block in aisles" :key="block.aisle.id">
      <h2 class="mb-3 text-sm font-medium">
        {{ formatMapLabel(aisleLabel, block.aisle.code) }}
      </h2>
      <div class="overflow-x-auto">
        <table class="border-separate border-spacing-2">
        <thead>
          <tr>
            <th class="w-0"></th>
            <th
              v-for="col in block.columns"
              :key="col.rack.id"
              class="text-center text-xs font-medium text-foreground/70"
            >
              {{ formatMapLabel(rackLabel, lastSegment(col.rack.code)) }}
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="rowIndex in block.rowCount" :key="`${block.aisle.id}-${rowIndex}`">
            <th class="whitespace-nowrap pr-2 text-left text-xs font-medium text-foreground/70" scope="row">
              {{ levelHeading(block, rowIndex - 1) }}
            </th>
            <td v-for="col in block.columns" :key="`${col.rack.id}-${rowIndex}`">
              <div class="flex flex-nowrap justify-center gap-1">
                <div
                  v-for="bin in binsAt(col, rowIndex - 1)"
                  :key="bin.id"
                  class="flex h-12 w-12 shrink-0 items-center justify-center rounded-md border border-border bg-primary/15 font-mono text-xs"
                  :data-location-id="bin.id"
                  :data-code="bin.code"
                  :title="bin.code"
                  :aria-label="bin.code"
                >
                  {{ lastSegment(bin.code) }}
                </div>
              </div>
            </td>
          </tr>
        </tbody>
        </table>
      </div>
    </section>
  </div>
</template>
