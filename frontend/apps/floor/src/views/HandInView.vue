<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useFloorStore } from "../stores/floor";

const { t } = useI18n();
const floor = useFloorStore();
const router = useRouter();

async function flush() {
  await floor.flush();
}

function login() {
  void router.push("/login");
}
</script>

<template>
  <section class="mx-auto mt-12 max-w-sm">
    <h1 class="mb-4 text-xl font-semibold">{{ t("floor.handInTitle") }}</h1>
    <p class="mb-4">{{ t("floor.handInBody") }}</p>
    <Button class="min-h-12 w-full" @click="flush">{{ t("floor.handInFlush") }}</Button>
    <Button class="mt-2 min-h-12 w-full" variant="outline" @click="login">{{ t("floor.fullLogin") }}</Button>
  </section>
</template>
