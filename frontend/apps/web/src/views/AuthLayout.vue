<script setup lang="ts">
defineProps<{
  title: string;
  lead?: string;
  brand: string;
}>();
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-foreground px-4 py-12">
    <div class="w-full max-w-sm rounded-lg bg-white p-6 shadow-sm">
      <img
        src="/lagerkraft-logo.png"
        :alt="brand"
        class="mx-auto mb-5 h-24 w-auto object-contain"
      />
      <h1 class="text-xl font-semibold text-foreground">{{ title }}</h1>
      <p v-if="lead" class="mt-1 text-sm text-foreground/70">{{ lead }}</p>
      <div class="mt-5 [&_button]:w-full">
        <slot />
      </div>
    </div>
  </div>
</template>
