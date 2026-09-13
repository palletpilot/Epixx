<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { platformUrl } from "../env";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";

type UserRow = {
  user_id: string;
  email: string;
  is_owner: boolean;
  roles: { role: string; warehouse_id: string | null }[];
};

const { t } = useI18n();
const auth = useAuthStore();
const tenant = useTenantStore();
const users = ref<UserRow[]>([]);
const email = ref("");
const role = ref("floor_worker");
const warehouseId = ref("");

onMounted(async () => {
  await tenant.refresh();
  await load();
});

async function load() {
  const res = await auth.authedFetch(`${platformUrl()}/users`);
  if (res.ok) {
    users.value = (await res.json()) as UserRow[];
  }
}

async function invite() {
  await auth.authedFetch(`${platformUrl()}/users/invitations`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      email: email.value,
      role: role.value,
      warehouse_ids: warehouseId.value ? [warehouseId.value] : [],
    }),
  });
  email.value = "";
  await load();
}

async function assign(userId: string) {
  await auth.authedFetch(`${platformUrl()}/users/${userId}/roles`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      role: role.value,
      warehouse_id: warehouseId.value || null,
    }),
  });
  await load();
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("users.title") }}</h1>
    <form class="mb-6 flex max-w-xl flex-wrap items-end gap-2" @submit.prevent="invite">
      <Input id="invite-email" v-model="email" type="email" :label="t('auth.email')" />
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("users.role") }}</span>
        <select v-model="role" class="h-9 rounded-md border border-input px-2" :aria-label="t('users.role')">
          <option value="tenant_admin">tenant_admin</option>
          <option value="warehouse_manager">warehouse_manager</option>
          <option value="floor_worker">floor_worker</option>
          <option value="viewer">viewer</option>
        </select>
      </label>
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("users.warehouse") }}</span>
        <select
          v-model="warehouseId"
          class="h-9 rounded-md border border-input px-2"
          :aria-label="t('users.warehouse')"
        >
          <option value="">—</option>
          <option v-for="w in tenant.warehouses" :key="w.id" :value="w.id">{{ w.name }}</option>
        </select>
      </label>
      <Button type="submit">{{ t("users.invite") }}</Button>
    </form>
    <ul class="flex flex-col gap-2">
      <li v-for="u in users" :key="u.user_id" class="flex items-center gap-2 rounded-md border border-border px-3 py-2">
        <span>{{ u.email }}</span>
        <span class="text-sm">{{ u.roles.map((r) => r.role).join(", ") }}</span>
        <Button type="button" size="sm" variant="outline" @click="assign(u.user_id)">
          {{ t("users.role") }}
        </Button>
      </li>
    </ul>
  </section>
</template>
