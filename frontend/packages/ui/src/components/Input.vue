<script setup lang="ts">
import { computed } from "vue";
import { cn } from "../lib/utils";

defineOptions({ inheritAttrs: false });

const props = withDefaults(
  defineProps<{
    id: string;
    label?: string;
    modelValue?: string;
    type?: "text" | "password" | "email" | "search" | "number";
    disabled?: boolean;
    class?: string;
  }>(),
  {
    type: "text",
    modelValue: "",
  },
);

const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const classes = computed(() =>
  cn(
    "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
    props.class,
  ),
);

function onInput(event: Event): void {
  emit("update:modelValue", (event.target as HTMLInputElement).value);
}
</script>

<template>
  <div class="flex flex-col gap-1">
    <label v-if="label" :for="id" class="text-sm font-medium">{{ label }}</label>
    <input
      :id="id"
      :type="type"
      :value="modelValue"
      :disabled="disabled"
      :class="classes"
      v-bind="$attrs"
      @input="onInput"
    />
  </div>
</template>
