<script setup lang="ts">
import { computed } from "vue";
import { cn } from "../lib/utils";

const props = withDefaults(
  defineProps<{
    variant?: "default" | "primary" | "outline";
    type?: "button" | "submit";
    disabled?: boolean;
    class?: string;
  }>(),
  { variant: "default", type: "button" },
);

const classes = computed(() =>
  cn(
    "inline-flex w-full items-center justify-center rounded-md px-4 text-base font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50 min-h-12",
    props.variant === "primary" && "min-h-16 bg-primary text-primary-foreground hover:bg-primary/90",
    props.variant === "default" && "bg-foreground/5 hover:bg-foreground/10",
    props.variant === "outline" && "border border-input bg-background hover:bg-foreground/5",
    props.class,
  ),
);
</script>

<template>
  <button :type="type" :disabled="disabled" :class="classes">
    <slot />
  </button>
</template>
