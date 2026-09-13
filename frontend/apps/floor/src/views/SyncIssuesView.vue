<script setup lang="ts">
import { useI18n } from "vue-i18n";
import { useLiveIssues } from "../composables/useLiveQuery";

const { t } = useI18n();
const issues = useLiveIssues();
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("floor.issues") }}</h1>
    <ul :aria-label="t('floor.issues')" class="flex flex-col gap-2">
      <li v-for="row in issues" :key="row.id" class="rounded-md border border-border px-3 py-2">
        {{ row.type }} · {{ row.id }}
      </li>
    </ul>
    <p v-if="issues.length === 0">{{ t("floor.noIssues") }}</p>
  </section>
</template>
