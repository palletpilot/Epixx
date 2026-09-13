<script setup lang="ts">
import { Button, Input } from "@lagerkraft/ui";
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();
const selected = ref(floor.sessions[0]?.user_id ?? "");
const pin = ref("");
const message = ref("");

async function submit() {
  const result = await floor.unlock(selected.value, pin.value);
  if (result === "ok") {
    await router.push("/tasks");
    return;
  }
  message.value = t(`floor.unlock_${result}`);
}

function goLogin() {
  void router.push("/login");
}
</script>

<template>
  <form class="mx-auto mt-12 flex max-w-sm flex-col gap-3" @submit.prevent="submit">
    <h1 class="text-xl font-semibold">{{ t("floor.unlockTitle") }}</h1>
    <label class="flex flex-col gap-1 text-sm">
      <span>{{ t("floor.pickName") }}</span>
      <select v-model="selected" class="min-h-12 rounded-md border border-input bg-background px-2" :aria-label="t('floor.pickName')">
        <option v-for="s in floor.sessions" :key="s.user_id" :value="s.user_id">{{ s.display_name }}</option>
      </select>
    </label>
    <Input id="unlock-pin" v-model="pin" type="password" :label="t('floor.pin')" class="min-h-12" />
    <p v-if="message" role="alert">{{ message }}</p>
    <Button type="submit" class="min-h-12">{{ t("floor.unlock") }}</Button>
    <Button type="button" variant="outline" class="min-h-12" @click="goLogin">{{ t("floor.fullLogin") }}</Button>
  </form>
</template>
