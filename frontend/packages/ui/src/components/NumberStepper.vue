<script setup lang="ts">
import { computed } from "vue";

const props = withDefaults(
  defineProps<{
    id: string;
    label?: string;
    modelValue: number;
    min?: number;
    max?: number;
    step?: number;
    minusLabel?: string;
    plusLabel?: string;
  }>(),
  { min: 1, max: 99, step: 1, minusLabel: "-", plusLabel: "+" },
);

const emit = defineEmits<{
  "update:modelValue": [value: number];
}>();

const display = computed(() => String(props.modelValue));

function clamp(n: number): number {
  return Math.min(props.max, Math.max(props.min, n));
}

function bump(dir: number): void {
  emit("update:modelValue", clamp(props.modelValue + dir * props.step));
}

function onInput(event: Event): void {
  const raw = (event.target as HTMLInputElement).value;
  const n = Number(raw);
  if (!Number.isFinite(n)) {
    return;
  }
  emit("update:modelValue", clamp(n));
}
</script>

<template>
  <div class="flex flex-col gap-1">
    <label v-if="label" :for="id" class="text-sm font-medium">{{ label }}</label>
    <div class="flex items-stretch gap-2">
      <button type="button" class="min-h-12 min-w-12 rounded-md border border-input" :aria-label="minusLabel" @click="bump(-1)">
        -
      </button>
      <input
        :id="id"
        :value="display"
        inputmode="numeric"
        class="min-h-12 w-full rounded-md border border-input bg-background text-center text-lg"
        @input="onInput"
      />
      <button type="button" class="min-h-12 min-w-12 rounded-md border border-input" :aria-label="plusLabel" @click="bump(1)">
        +
      </button>
    </div>
  </div>
</template>
