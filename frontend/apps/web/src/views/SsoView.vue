<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { platformUrl } from "../env";
import { useAuthStore } from "../stores/auth";

const { t } = useI18n();
const auth = useAuthStore();
const issuer = ref("");
const clientId = ref("");
const clientSecret = ref("");
const domainHint = ref("");
const jit = ref("off");
const idToken = ref("");
const enforced = ref(false);
const message = ref("");

onMounted(async () => {
  const res = await auth.authedFetch(`${platformUrl()}/oidc/provider`);
  if (res.ok) {
    const body = (await res.json()) as {
      issuer: string;
      client_id: string;
      domain_hint?: string;
      jit_provisioning: string;
      enforced: boolean;
    };
    issuer.value = body.issuer;
    clientId.value = body.client_id;
    domainHint.value = body.domain_hint ?? "";
    jit.value = body.jit_provisioning;
    enforced.value = body.enforced;
  }
});

async function save() {
  await auth.authedFetch(`${platformUrl()}/oidc/provider`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      issuer: issuer.value,
      client_id: clientId.value,
      client_secret: clientSecret.value,
      domain_hint: domainHint.value || null,
      jit_provisioning: jit.value,
    }),
  });
}

async function setEnforced() {
  const res = await auth.authedFetch(`${platformUrl()}/oidc/provider/enforced`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enforced: !enforced.value }),
  });
  if (res.ok) {
    enforced.value = !enforced.value;
  } else {
    message.value = t("auth.error");
  }
}

async function testLogin() {
  await auth.authedFetch(`${platformUrl()}/oidc/provider/test-login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id_token: idToken.value }),
  });
}
</script>

<template>
  <section class="max-w-lg">
    <h1 class="mb-4 text-xl font-semibold">{{ t("sso.title") }}</h1>
    <form class="flex flex-col gap-3" @submit.prevent="save">
      <Input id="issuer" v-model="issuer" :label="t('sso.issuer')" />
      <Input id="client-id" v-model="clientId" :label="t('sso.clientId')" />
      <Input id="client-secret" v-model="clientSecret" type="password" :label="t('sso.clientSecret')" />
      <Input id="domain" v-model="domainHint" :label="t('sso.domainHint')" />
      <label class="flex flex-col gap-1 text-sm">
        <span>{{ t("sso.jit") }}</span>
        <select v-model="jit" class="h-9 rounded-md border border-input px-2" :aria-label="t('sso.jit')">
          <option value="off">off</option>
          <option value="mapped">mapped</option>
          <option value="all">all</option>
        </select>
      </label>
      <Button type="submit">{{ t("sso.save") }}</Button>
    </form>
    <p class="mt-4 text-sm">{{ t("sso.enforcedHint") }}</p>
    <Button class="mt-2" type="button" variant="outline" @click="setEnforced">{{ t("sso.enforced") }}</Button>
    <Input id="id-token" v-model="idToken" class="mt-4" :label="t('sso.idToken')" />
    <Button class="mt-2" type="button" variant="outline" @click="testLogin">{{ t("sso.testLogin") }}</Button>
    <p v-if="message" role="status">{{ message }}</p>
  </section>
</template>
