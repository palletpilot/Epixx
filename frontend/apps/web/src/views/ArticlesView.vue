<script setup lang="ts">
import { Button, EmptyState, Input } from "@lagerkraft/ui";
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { wmsUrl } from "../env";
import { useAuthStore } from "../stores/auth";

type UnitRow = { id: string; code: string; display_name_sv: string; display_name_en: string };
type ArticleRow = { id: string; sku: string; name: string };

const { t, locale } = useI18n();
const auth = useAuthStore();
const sku = ref("");
const name = ref("");
const unitId = ref("");
const units = ref<UnitRow[]>([]);
const articles = ref<ArticleRow[]>([]);
const error = ref("");
const hasArticles = computed(() => articles.value.length > 0);

function tenantHeaders(): HeadersInit | null {
  if (!auth.tenantId) {
    return null;
  }
  return { "Lagerkraft-Tenant-Id": auth.tenantId };
}

onMounted(async () => {
  const headers = tenantHeaders();
  if (!headers) {
    return;
  }
  const [unitsRes, articlesRes] = await Promise.all([
    fetch(`${wmsUrl()}/units`, { headers }),
    fetch(`${wmsUrl()}/articles`, { headers }),
  ]);
  if (unitsRes.ok) {
    const body: unknown = await unitsRes.json();
    units.value = Array.isArray(body) ? (body as UnitRow[]) : [];
    const st = units.value.find((u) => u.code === "st");
    if (st) {
      unitId.value = st.id;
    }
  }
  if (articlesRes.ok) {
    const body: unknown = await articlesRes.json();
    articles.value = Array.isArray(body) ? (body as ArticleRow[]) : [];
  }
});

async function create() {
  error.value = "";
  const headers = tenantHeaders();
  if (!headers) {
    error.value = t("articles.error");
    return;
  }
  const res = await fetch(`${wmsUrl()}/articles`, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify({
      id: crypto.randomUUID(),
      sku: sku.value,
      name: name.value,
      base_uom_id: unitId.value,
      packaging_level_id: crypto.randomUUID(),
    }),
  });
  if (!res.ok) {
    error.value = t("articles.error");
    return;
  }
  const body = (await res.json()) as ArticleRow;
  articles.value = [body, ...articles.value];
  sku.value = "";
  name.value = "";
}

function unitLabel(unit: UnitRow): string {
  return locale.value === "sv" ? unit.display_name_sv : unit.display_name_en;
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-2xl font-semibold tracking-tight">{{ t("articles.title") }}</h1>
    <EmptyState v-if="!hasArticles" class="mb-6" :title="t('articles.empty')">
      {{ t("articles.emptyAction") }}
    </EmptyState>
    <form class="flex max-w-md flex-col gap-3" @submit.prevent="create">
      <Input id="article-sku" v-model="sku" :label="t('articles.sku')" />
      <Input id="article-name" v-model="name" :label="t('articles.name')" />
      <label class="flex flex-col gap-1 text-sm">
        <span class="font-medium">{{ t("articles.unit") }}</span>
        <select
          id="article-unit"
          v-model="unitId"
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('articles.unit')"
        >
          <option v-for="u in units" :key="u.id" :value="u.id">{{ unitLabel(u) }}</option>
        </select>
      </label>
      <Button type="submit">{{ t("articles.create") }}</Button>
      <p v-if="error" role="alert">{{ error }}</p>
    </form>
    <ul v-if="hasArticles" :aria-label="t('articles.list')" class="mt-6 flex flex-col gap-2">
      <li v-for="row in articles" :key="row.id" class="rounded-md border border-border px-3 py-2">
        {{ row.sku }} {{ row.name }}
      </li>
    </ul>
  </section>
</template>
