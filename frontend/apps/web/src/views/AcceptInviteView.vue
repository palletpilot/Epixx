<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRoute, useRouter } from "vue-router";
import { platformUrl } from "../env";

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const token = ref("");
const password = ref("");

onMounted(() => {
  const q = route.query.token;
  if (typeof q === "string") {
    token.value = q;
  }
});

async function submit() {
  const res = await fetch(`${platformUrl()}/users/invitations/accept`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token: token.value, password: password.value }),
  });
  if (res.ok) {
    await router.push("/login");
  }
}
</script>

<template>
  <form class="mx-auto mt-16 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("invite.title") }}</h1>
    <Input id="token" v-model="token" :label="t('invite.token')" />
    <Input id="password" v-model="password" type="password" :label="t('invite.password')" />
    <Button type="submit">{{ t("invite.submit") }}</Button>
  </form>
</template>
