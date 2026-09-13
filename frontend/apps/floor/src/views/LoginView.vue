<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();
const email = ref("");
const password = ref("");
const totp = ref("");
const pin = ref("");
const showTotp = ref(false);
const showPin = ref(false);

const needsPin = computed(
  () =>
    showPin.value ||
    (floor.unlocked && !(floor.sessions.find((s) => s.user_id === floor.unlockedUserId)?.pin_hash)),
);

async function submit() {
  if (needsPin.value) {
    await floor.setPin(pin.value);
    await router.push("/warehouses");
    return;
  }
  const result = await floor.login(email.value, password.value, totp.value || undefined);
  if (result === "totp") {
    showTotp.value = true;
    return;
  }
  if (result === "set-pin" || floor.unlocked) {
    showPin.value = true;
  }
}
</script>

<template>
  <form class="mx-auto mt-12 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("floor.login") }}</h1>
    <template v-if="!needsPin">
      <Input id="email" v-model="email" type="email" :label="t('auth.email')" autocomplete="username" class="min-h-12" />
      <Input
        id="password"
        v-model="password"
        type="password"
        :label="t('auth.password')"
        autocomplete="current-password"
        class="min-h-12"
      />
      <Input v-if="showTotp" id="totp" v-model="totp" :label="t('auth.totp')" class="min-h-12" />
    </template>
    <Input v-else id="pin" v-model="pin" type="password" :label="t('floor.pin')" class="min-h-12" />
    <p v-if="floor.lastError && !needsPin" role="alert">{{ t("floor.error") }}</p>
    <Button type="submit" class="min-h-12">{{ t("auth.submit") }}</Button>
  </form>
</template>
