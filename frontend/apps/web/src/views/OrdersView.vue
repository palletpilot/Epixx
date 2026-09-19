<script setup lang="ts">
import { Button, EmptyState, Input } from "@lagerkraft/ui";
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { wmsUrl } from "../env";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";

type ArticleRow = { id: string; sku: string; name: string };
type OrderLineRow = { id: string; article_id: string; requested_qty_base: string; status: string };
type OrderRow = {
  id: string;
  warehouse_id: string;
  external_ref: string;
  status: string;
  lines: OrderLineRow[];
};

const { t } = useI18n();
const auth = useAuthStore();
const tenant = useTenantStore();
const warehouseId = ref("");
const articleId = ref("");
const qty = ref("1");
const articles = ref<ArticleRow[]>([]);
const orders = ref<OrderRow[]>([]);
const error = ref("");
const hasOrders = computed(() => orders.value.length > 0);

function tenantHeaders(): HeadersInit | null {
  if (!auth.tenantId) {
    return null;
  }
  return { "Lagerkraft-Tenant-Id": auth.tenantId };
}

async function loadOrders(headers: HeadersInit): Promise<void> {
  const qs = warehouseId.value ? `?warehouse=${warehouseId.value}` : "";
  const res = await fetch(`${wmsUrl()}/orders${qs}`, { headers });
  if (res.ok) {
    const body: unknown = await res.json();
    orders.value = Array.isArray(body) ? (body as OrderRow[]) : [];
  }
}

onMounted(async () => {
  await tenant.refresh();
  warehouseId.value = tenant.warehouses[0]?.id ?? "";
  const headers = tenantHeaders();
  if (!headers) {
    return;
  }
  const articlesRes = await fetch(`${wmsUrl()}/articles`, { headers });
  if (articlesRes.ok) {
    const body: unknown = await articlesRes.json();
    articles.value = Array.isArray(body) ? (body as ArticleRow[]) : [];
    articleId.value = articles.value[0]?.id ?? "";
  }
  await loadOrders(headers);
});

async function create() {
  error.value = "";
  const headers = tenantHeaders();
  if (!headers) {
    error.value = t("orders.error");
    return;
  }
  const res = await fetch(`${wmsUrl()}/orders`, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify({
      id: crypto.randomUUID(),
      warehouse_id: warehouseId.value,
      article_id: articleId.value,
      qty_base: qty.value,
    }),
  });
  if (!res.ok) {
    error.value = t("orders.error");
    return;
  }
  await loadOrders(headers);
}

function lineLabel(row: OrderRow): string {
  const line = row.lines[0];
  const article = articles.value.find((a) => a.id === line?.article_id);
  const sku = article?.sku ?? line?.article_id ?? "";
  return `${row.external_ref} ${sku} ${line?.requested_qty_base ?? ""} ${row.status}`;
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-2xl font-semibold tracking-tight">{{ t("orders.title") }}</h1>
    <EmptyState v-if="!hasOrders" class="mb-6" :title="t('orders.empty')">
      {{ t("orders.emptyAction") }}
    </EmptyState>
    <form class="flex max-w-md flex-col gap-3" @submit.prevent="create">
      <label class="flex flex-col gap-1 text-sm">
        <span class="font-medium">{{ t("orders.warehouse") }}</span>
        <select
          id="order-warehouse"
          v-model="warehouseId"
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('orders.warehouse')"
        >
          <option v-for="w in tenant.warehouses" :key="w.id" :value="w.id">{{ w.name }}</option>
        </select>
      </label>
      <label class="flex flex-col gap-1 text-sm">
        <span class="font-medium">{{ t("orders.article") }}</span>
        <select
          id="order-article"
          v-model="articleId"
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('orders.article')"
        >
          <option v-for="a in articles" :key="a.id" :value="a.id">{{ a.sku }} {{ a.name }}</option>
        </select>
      </label>
      <Input id="order-qty" v-model="qty" :label="t('orders.qty')" />
      <Button type="submit">{{ t("orders.create") }}</Button>
      <p v-if="error" role="alert">{{ error }}</p>
    </form>
    <ul v-if="hasOrders" :aria-label="t('orders.list')" class="mt-6 flex flex-col gap-2">
      <li v-for="row in orders" :key="row.id" class="rounded-md border border-border px-3 py-2">
        {{ lineLabel(row) }}
      </li>
    </ul>
  </section>
</template>
