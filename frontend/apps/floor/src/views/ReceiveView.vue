<script setup lang="ts">
import { FloorButton, Input } from "@lagerkraft/ui";
import { liveQuery } from "dexie";
import { onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { getDb, type ArticleRow } from "../db";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();
const articles = ref<ArticleRow[]>([]);
const articleId = ref("");
const qty = ref("1");
const error = ref<string | null>(null);

const sub = liveQuery(() => getDb().articles.toArray()).subscribe({
  next: (rows) => {
    articles.value = rows;
    if (!articleId.value && rows[0]) {
      articleId.value = rows[0].id;
    }
  },
});
onUnmounted(() => sub.unsubscribe());

onMounted(() => {
  if (!floor.warehouseId) {
    void router.push("/warehouses");
    return;
  }
  void floor.loadSnapshot(floor.warehouseId);
});

async function submit(): Promise<void> {
  error.value = null;
  if (!articleId.value || !qty.value.trim()) {
    error.value = t("floor.error");
    return;
  }
  const taskId = await floor.receiveHandlingUnit({ articleId: articleId.value, qty: qty.value.trim() });
  if (!taskId) {
    error.value = t("floor.error");
    return;
  }
  await router.push(`/tasks/${taskId}`);
}
</script>

<template>
  <section class="mx-auto flex max-w-md flex-col gap-4">
    <h1 class="text-xl font-semibold">{{ t("floor.receiveTitle") }}</h1>
    <p v-if="error" role="alert" class="text-destructive">{{ error }}</p>
    <p v-if="articles.length === 0">{{ t("articles.empty") }}</p>
    <form v-else class="flex flex-col gap-4" @submit.prevent="submit">
      <label class="flex flex-col gap-1 text-sm">
        <span class="font-medium">{{ t("articles.sku") }}</span>
        <select
          id="receive-article"
          v-model="articleId"
          class="min-h-12 rounded-md border border-input bg-background px-2"
          :aria-label="t('articles.sku')"
        >
          <option v-for="row in articles" :key="row.id" :value="row.id">{{ row.sku }} {{ row.name }}</option>
        </select>
      </label>
      <Input id="receive-qty" v-model="qty" :label="t('floor.receiveQty')" />
      <FloorButton variant="primary" type="submit">{{ t("floor.receiveSubmit") }}</FloorButton>
    </form>
    <FloorButton variant="outline" @click="router.push('/tasks')">{{ t("floor.back") }}</FloorButton>
  </section>
</template>
