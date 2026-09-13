<script setup lang="ts">
import { computed } from "vue";
import { cn } from "../lib/utils";

const props = withDefaults(
  defineProps<{
    variant?: "info" | "warning" | "error";
    class?: string;
  }>(),
  { variant: "info" },
);

const role = computed(() => (props.variant === "error" ? "alert" : "status"));

const classes = computed(() =>
  cn(
    "flex items-start gap-2 rounded-md border px-3 py-2 text-sm",
    props.variant === "info" && "border-border bg-background",
    props.variant === "warning" && "border-foreground/30 bg-foreground/5",
    props.variant === "error" && "border-destructive bg-destructive/10 text-destructive",
    props.class,
  ),
);
</script>

<template>
  <div :role="role" :class="classes">
    <svg
      aria-hidden="true"
      class="mt-0.5 h-4 w-4 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
    >
      <circle v-if="variant === 'info'" cx="12" cy="12" r="10" />
      <path v-if="variant === 'info'" d="M12 16v-4M12 8h.01" />
      <path
        v-if="variant === 'warning'"
        d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"
      />
      <path v-if="variant === 'warning'" d="M12 9v4M12 17h.01" />
      <circle v-if="variant === 'error'" cx="12" cy="12" r="10" />
      <path v-if="variant === 'error'" d="m15 9-6 6M9 9l6 6" />
    </svg>
    <div class="min-w-0">
      <slot />
    </div>
  </div>
</template>
