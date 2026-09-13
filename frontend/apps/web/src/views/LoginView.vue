<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { platformUrl } from "../env";
import { useAuthStore } from "../stores/auth";

const { t } = useI18n();
const router = useRouter();
const auth = useAuthStore();
const email = ref("");
const password = ref("");
const totp = ref("");
const showTotp = ref(false);
const message = ref("");
const slug = ref("");

async function submit() {
  const result = await auth.login(email.value, password.value, totp.value || undefined);
  if (result === "totp") {
    showTotp.value = true;
    return;
  }
  if (result === "chooser") {
    await router.push("/choose-tenant");
    return;
  }
  if (result === "provisioning") {
    message.value = t("auth.provisioning");
    window.setTimeout(() => {
      void submit();
    }, 2000);
    return;
  }
  if (result === "authenticated") {
    await router.push("/app/tasks");
    return;
  }
  message.value = t("auth.error");
}

async function startSso() {
  const res = await fetch(`${platformUrl()}/auth/oidc/start`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email: email.value || null, slug: slug.value || null }),
  });
  if (!res.ok) {
    message.value = t("auth.error");
    return;
  }
  const body = (await res.json()) as { authorize_url: string };
  window.location.href = body.authorize_url;
}
</script>

<template>
  <form class="mx-auto mt-16 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("auth.login") }}</h1>
    <Input id="email" v-model="email" type="email" :label="t('auth.email')" autocomplete="username" />
    <Input
      id="password"
      v-model="password"
      type="password"
      :label="t('auth.password')"
      autocomplete="current-password"
    />
    <Input v-if="showTotp" id="totp" v-model="totp" :label="t('auth.totp')" autocomplete="one-time-code" />
    <p v-if="message" role="status">{{ message }}</p>
    <Button type="submit">{{ t("auth.submit") }}</Button>
    <Input id="slug" v-model="slug" :label="t('signup.slug')" />
    <Button type="button" variant="outline" @click="startSso">{{ t("auth.sso") }}</Button>
    <RouterLink class="text-sm underline" to="/signup">{{ t("auth.signupLink") }}</RouterLink>
  </form>
</template>
