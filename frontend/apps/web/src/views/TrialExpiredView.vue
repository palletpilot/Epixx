<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { platformUrl } from "../env";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";

const { t } = useI18n();
const auth = useAuthStore();
const tenant = useTenantStore();
const router = useRouter();
const plan = ref("start");

async function convert() {
  const res = await auth.authedFetch(`${platformUrl()}/lifecycle/convert-trial`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ plan_code: plan.value }),
  });
  if (res.ok) {
    await tenant.refresh();
    await router.push("/app/tasks");
  }
}
</script>

<template>
  <section class="mx-auto mt-16 max-w-md">
    <h1 class="mb-2 text-xl font-semibold">{{ t("trial.expiredTitle") }}</h1>
    <p class="mb-4">{{ t("trial.expiredBody") }}</p>
    <form class="flex flex-col gap-3" @submit.prevent="convert">
      <Input id="plan" v-model="plan" :label="t('trial.plan')" />
      <Button type="submit">{{ t("trial.convert") }}</Button>
    </form>
  </section>
</template>
