<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { isStandalone } from "../auth/crypto";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();
const code = ref("");
const name = ref("");
const standalone = isStandalone();

async function submit() {
  const ok = await floor.enroll(code.value, name.value);
  if (ok) {
    await router.push("/login");
  }
}
</script>

<template>
  <form class="mx-auto mt-12 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("floor.enrollTitle") }}</h1>
    <p v-if="!standalone" role="status">{{ t("floor.addToHome") }}</p>
    <Input id="enroll-code" v-model="code" :label="t('floor.enrollCode')" :disabled="!standalone" class="min-h-12" />
    <Input id="enroll-name" v-model="name" :label="t('floor.deviceName')" :disabled="!standalone" class="min-h-12" />
    <p v-if="floor.lastError" role="alert">{{ t("floor.error") }}</p>
    <Button type="submit" class="min-h-12" :disabled="!standalone">{{ t("floor.enrollSubmit") }}</Button>
  </form>
</template>
