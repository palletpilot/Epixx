<script setup lang="ts">
import { FloorButton, Input, NumberStepper } from "@lagerkraft/ui";
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { DEFAULT_DIMS, DEFAULT_PATTERN, generateAisle, type Dims } from "../map/generateAisle";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();

type Step = "aisle" | "racks" | "levels" | "bins" | "same" | "levelDims" | "confirm";

const step = ref<Step>("aisle");
const aisle = ref("A");
const racks = ref(2);
const levels = ref(3);
const bins = ref(2);
const sameDims = ref(true);
const levelIndex = ref(0);
const levelDims = ref<Dims[]>([]);
const error = ref<string | null>(null);

onMounted(() => {
  if (!floor.warehouseId) {
    void router.push("/warehouses");
    return;
  }
  if (floor.warehouses.length === 0) {
    void floor.loadWarehouses();
  }
});

const warehouse = computed(
  () => floor.warehouses.find((w) => w.id === floor.warehouseId) ?? floor.warehouses[0],
);
const pattern = computed(() => warehouse.value?.code_pattern || DEFAULT_PATTERN);

const preview = computed(() =>
  generateAisle({
    pattern: pattern.value,
    aisle: aisle.value || "A",
    racks: racks.value,
    levels: levels.value,
    bins: bins.value,
  }),
);

function ensureLevelDims(): void {
  while (levelDims.value.length < levels.value) {
    levelDims.value.push({ ...DEFAULT_DIMS });
  }
  if (levelDims.value.length > levels.value) {
    levelDims.value = levelDims.value.slice(0, levels.value);
  }
}

function goNext(): void {
  error.value = null;
  if (step.value === "aisle") {
    if (!/^[A-Za-z0-9]+$/.test(aisle.value.trim())) {
      error.value = t("floor.error");
      return;
    }
    aisle.value = aisle.value.trim().toUpperCase();
    step.value = "racks";
    return;
  }
  if (step.value === "racks") {
    step.value = "levels";
    return;
  }
  if (step.value === "levels") {
    step.value = "bins";
    return;
  }
  if (step.value === "bins") {
    step.value = "same";
    return;
  }
  if (step.value === "same") {
    step.value = "confirm";
  }
}

function chooseSame(value: boolean): void {
  sameDims.value = value;
  if (value) {
    step.value = "confirm";
    return;
  }
  ensureLevelDims();
  levelIndex.value = 0;
  step.value = "levelDims";
}

function nextLevelDims(): void {
  ensureLevelDims();
  if (levelIndex.value + 1 < levels.value) {
    levelIndex.value += 1;
    return;
  }
  step.value = "confirm";
}

function goBack(): void {
  error.value = null;
  if (step.value === "racks") {
    step.value = "aisle";
    return;
  }
  if (step.value === "levels") {
    step.value = "racks";
    return;
  }
  if (step.value === "bins") {
    step.value = "levels";
    return;
  }
  if (step.value === "same") {
    step.value = "bins";
    return;
  }
  if (step.value === "levelDims") {
    if (levelIndex.value > 0) {
      levelIndex.value -= 1;
      return;
    }
    step.value = "same";
    return;
  }
  if (step.value === "confirm") {
    step.value = sameDims.value ? "same" : "levelDims";
  }
}

async function confirm(): Promise<void> {
  error.value = null;
  const dims = sameDims.value ? [DEFAULT_DIMS] : levelDims.value;
  const ok = await floor.mapAisle({
    aisle: aisle.value,
    racks: racks.value,
    levels: levels.value,
    bins: bins.value,
    dimsByLevel: dims,
  });
  if (!ok) {
    error.value = t("floor.error");
    return;
  }
  await router.push("/tasks");
}
</script>

<template>
  <section class="mx-auto flex max-w-md flex-col gap-4">
    <h1 class="text-xl font-semibold">{{ t("map.start") }}</h1>
    <p v-if="error" role="alert" class="text-destructive">{{ error }}</p>

    <template v-if="step === 'aisle'">
      <Input id="map-aisle" v-model="aisle" :label="t('map.aisle')" />
      <FloorButton variant="primary" @click="goNext">{{ t("map.next") }}</FloorButton>
    </template>

    <template v-else-if="step === 'racks'">
      <NumberStepper id="map-racks" v-model="racks" :label="t('map.racks')" />
      <FloorButton variant="primary" @click="goNext">{{ t("map.next") }}</FloorButton>
      <FloorButton @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>

    <template v-else-if="step === 'levels'">
      <NumberStepper id="map-levels" v-model="levels" :label="t('map.levels')" />
      <FloorButton variant="primary" @click="goNext">{{ t("map.next") }}</FloorButton>
      <FloorButton @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>

    <template v-else-if="step === 'bins'">
      <NumberStepper id="map-bins" v-model="bins" :label="t('map.bins')" />
      <FloorButton variant="primary" @click="goNext">{{ t("map.next") }}</FloorButton>
      <FloorButton @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>

    <template v-else-if="step === 'same'">
      <p class="text-lg">{{ t("map.sameDims") }}</p>
      <FloorButton variant="primary" @click="chooseSame(true)">{{ t("map.yes") }}</FloorButton>
      <FloorButton @click="chooseSame(false)">{{ t("map.no") }}</FloorButton>
      <FloorButton variant="outline" @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>

    <template v-else-if="step === 'levelDims'">
      <p class="text-lg">{{ t("map.levelDims", { n: levelIndex + 1 }) }}</p>
      <NumberStepper
        id="map-h"
        v-model="levelDims[levelIndex]!.height_mm"
        :min="1"
        :max="10000"
        :step="10"
        :label="t('map.height')"
      />
      <NumberStepper
        id="map-w"
        v-model="levelDims[levelIndex]!.width_mm"
        :min="1"
        :max="10000"
        :step="10"
        :label="t('map.width')"
      />
      <NumberStepper
        id="map-d"
        v-model="levelDims[levelIndex]!.depth_mm"
        :min="1"
        :max="10000"
        :step="10"
        :label="t('map.depth')"
      />
      <NumberStepper
        id="map-g"
        v-model="levelDims[levelIndex]!.max_weight_g"
        :min="1"
        :max="2000000"
        :step="1000"
        :label="t('map.weight')"
      />
      <FloorButton variant="primary" @click="nextLevelDims">{{ t("map.next") }}</FloorButton>
      <FloorButton @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>

    <template v-else>
      <p>
        {{ t("map.summary", { count: preview.count, first: preview.firstBin, last: preview.lastBin }) }}
      </p>
      <FloorButton variant="primary" @click="confirm">{{ t("map.confirm") }}</FloorButton>
      <FloorButton @click="goBack">{{ t("map.back") }}</FloorButton>
    </template>
  </section>
</template>
