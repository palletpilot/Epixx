<script setup lang="ts">
defineProps<{
  title: string;
  lead?: string;
  brand: string;
}>();
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-foreground/[0.04] px-4 py-12">
    <div class="w-full max-w-sm rounded-lg border border-border bg-background p-6 shadow-sm">
      <p class="mb-4 text-sm font-semibold tracking-wide">{{ brand }}</p>
      <h1 class="text-xl font-semibold">{{ title }}</h1>
      <p v-if="lead" class="mt-1 text-sm text-foreground/70">{{ lead }}</p>
      <div class="mt-5 [&_button]:w-full">
        <slot />
      </div>
    </div>
  </div>
</template>
