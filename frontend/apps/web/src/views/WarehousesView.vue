<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useTenantStore } from "../stores/tenant";

const { t } = useI18n();
const tenant = useTenantStore();
const name = ref("");

onMounted(() => {
  void tenant.refresh();
});

async function create() {
  await tenant.createWarehouse(name.value);
  name.value = "";
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("warehouses.title") }}</h1>
    <form class="mb-6 flex max-w-md gap-2" @submit.prevent="create">
      <Input id="wh-name" v-model="name" :label="t('warehouses.name')" class="flex-1" />
      <Button type="submit" class="self-end">{{ t("warehouses.create") }}</Button>
    </form>
    <ul :aria-label="t('warehouses.list')" class="flex flex-col gap-2">
      <li v-for="w in tenant.warehouses" :key="w.id" class="rounded-md border border-border px-3 py-2">
        {{ w.name }}
      </li>
    </ul>
    <p v-if="tenant.warehouses.length === 0">{{ t("warehouses.empty") }}</p>
  </section>
</template>
