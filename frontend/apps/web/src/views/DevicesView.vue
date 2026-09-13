<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { platformUrl } from "../env";
import { useAuthStore } from "../stores/auth";

type Device = {
  id: string;
  name: string;
  pending_count: number;
  revoked_at: string | null;
};

const { t } = useI18n();
const auth = useAuthStore();
const devices = ref<Device[]>([]);
const code = ref("");

onMounted(() => {
  void load();
});

async function load() {
  const res = await auth.authedFetch(`${platformUrl()}/devices`);
  if (res.ok) {
    devices.value = (await res.json()) as Device[];
  }
}

async function enroll() {
  const res = await auth.authedFetch(`${platformUrl()}/devices/enrollment-codes`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: "{}",
  });
  if (res.ok) {
    const body = (await res.json()) as { code: string };
    code.value = body.code;
  }
}

async function revoke(id: string) {
  await auth.authedFetch(`${platformUrl()}/devices/${id}/revoke`, { method: "POST" });
  await load();
}

async function remove(id: string, confirmLoss: boolean) {
  await auth.authedFetch(`${platformUrl()}/devices/${id}`, {
    method: "DELETE",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ confirm_loss: confirmLoss }),
  });
  await load();
}
</script>

<template>
  <section>
    <h1 class="mb-4 text-xl font-semibold">{{ t("devices.title") }}</h1>
    <Button type="button" @click="enroll">{{ t("devices.code") }}</Button>
    <p v-if="code" class="mt-2" role="status">{{ t("devices.codeValue", { code }) }}</p>
    <ul class="mt-4 flex flex-col gap-2">
      <li v-for="d in devices" :key="d.id" class="flex flex-wrap items-center gap-2 rounded-md border border-border px-3 py-2">
        <span>{{ d.name }}</span>
        <span class="text-sm">{{ t("devices.pending", { count: d.pending_count }) }}</span>
        <Button type="button" size="sm" variant="outline" @click="revoke(d.id)">
          {{ t("devices.revoke") }}
        </Button>
        <Button type="button" size="sm" variant="destructive" @click="remove(d.id, false)">
          {{ t("devices.remove") }}
        </Button>
        <Button type="button" size="sm" variant="ghost" @click="remove(d.id, true)">
          {{ t("devices.confirmLoss") }}
        </Button>
      </li>
    </ul>
  </section>
</template>
