<script setup lang="ts">
defineProps<{ title: string }>();
</script>

<template>
  <div class="rounded-md border border-dashed border-border px-4 py-8 text-center">
    <p class="font-medium">{{ title }}</p>
    <div v-if="$slots.default" class="mt-2 text-sm text-foreground/70">
      <slot />
    </div>
  </div>
</template>
