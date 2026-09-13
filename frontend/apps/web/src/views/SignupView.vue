<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { platformUrl } from "../env";

const { t } = useI18n();
const company = ref("");
const orgNumber = ref("");
const displayName = ref("");
const email = ref("");
const password = ref("");
const slug = ref("");
const message = ref("");
const joinCompany = ref("");

watch(company, async (value) => {
  if (!value) {
    return;
  }
  const proposed = value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "")
    .slice(0, 30);
  if (proposed.length < 3) {
    return;
  }
  const res = await fetch(
    `${platformUrl()}/signup/slug-available?slug=${encodeURIComponent(proposed)}`,
  );
  if (!res.ok) {
    return;
  }
  const body = (await res.json()) as { available: boolean; slug: string };
  slug.value = body.slug;
});

async function submit(joinRequest = false) {
  message.value = "";
  const res = await fetch(`${platformUrl()}/signup`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      company_name: company.value,
      org_number: orgNumber.value,
      email: email.value,
      display_name: displayName.value,
      password: password.value,
      slug: slug.value || null,
      join_request: joinRequest,
    }),
  });
  const body = (await res.json().catch(() => ({}))) as {
    company_name?: string;
    slug?: string;
    error?: string;
  };
  if (res.status === 409 && body.company_name) {
    joinCompany.value = body.company_name;
    return;
  }
  if (!res.ok) {
    message.value = body.error ?? t("auth.error");
    return;
  }
  if (joinRequest) {
    message.value = t("signup.joinSent");
    return;
  }
  message.value = t("signup.done");
}
</script>

<template>
  <form class="mx-auto mt-12 flex max-w-sm flex-col gap-3" @submit.prevent="submit(false)">
    <h1 class="text-xl font-semibold">{{ t("signup.title") }}</h1>
    <Input id="company" v-model="company" :label="t('signup.company')" />
    <Input id="org" v-model="orgNumber" :label="t('signup.orgNumber')" />
    <Input id="name" v-model="displayName" :label="t('signup.name')" />
    <Input id="email" v-model="email" type="email" :label="t('auth.email')" />
    <Input id="password" v-model="password" type="password" :label="t('auth.password')" />
    <Input id="slug" v-model="slug" :label="t('signup.slug')" />
    <p class="text-sm text-foreground/70">{{ t("signup.slugHint") }}</p>
    <template v-if="joinCompany">
      <p role="status">{{ t("signup.joinTitle", { company: joinCompany }) }}</p>
      <p>{{ t("signup.joinHint") }}</p>
      <Button type="button" @click="submit(true)">{{ t("signup.joinSubmit") }}</Button>
    </template>
    <p v-if="message" role="status">{{ message }}</p>
    <Button v-if="!joinCompany" type="submit">{{ t("signup.submit") }}</Button>
    <RouterLink class="text-sm underline" to="/login">{{ t("signup.loginLink") }}</RouterLink>
  </form>
</template>
