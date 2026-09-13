<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRoute } from "vue-router";
import { platformUrl } from "../env";

const { t } = useI18n();
const route = useRoute();
const token = ref("");
const message = ref("");

onMounted(() => {
  const q = route.query.token;
  if (typeof q === "string") {
    token.value = q;
  }
});

async function submit() {
  const res = await fetch(`${platformUrl()}/signup/verify`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token: token.value }),
  });
  message.value = res.ok ? t("verify.ok") : t("auth.error");
}
</script>

<template>
  <form class="mx-auto mt-16 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("verify.title") }}</h1>
    <Input id="token" v-model="token" :label="t('verify.token')" />
    <p v-if="message" role="status">{{ message }}</p>
    <Button type="submit">{{ t("verify.submit") }}</Button>
  </form>
</template>
