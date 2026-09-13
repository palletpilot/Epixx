<script setup lang="ts">
import { Banner } from "@lagerkraft/ui";
import { computed, onMounted, onUnmounted } from "vue";
import { RouterLink, RouterView, useRoute, useRouter } from "vue-router";
import { useI18n } from "vue-i18n";
import { useFloorRealtime } from "./composables/useFloorRealtime";
import { useLivePending } from "./composables/useLiveQuery";
import { useFloorStore } from "./stores/floor";
import { applyUpdate, isAtRest } from "./update/isAtRest";

const { t } = useI18n();
const floor = useFloorStore();
const route = useRoute();
const router = useRouter();
const pending = useLivePending();
useFloorRealtime();

const home = computed(() => Boolean(route.meta.home));
const atRest = computed(() =>
  isAtRest({
    home: home.value,
    taskOpen: Boolean(route.meta.task),
    formDirty: floor.formDirty,
    printInFlight: floor.printInFlight,
    outboxFlushed: pending.value === 0,
    upgradeBlocked: floor.upgradeRequired,
    offline: !floor.online,
  }),
);

let beaconTimer = 0;
let updateTimer = 0;

function onOnline(): void {
  floor.online = true;
  void floor.flush();
  void floor.sendBeacon();
}
function onOffline(): void {
  floor.online = false;
}
function onPointer(): void {
  floor.touch();
}

onMounted(() => {
  window.addEventListener("online", onOnline);
  window.addEventListener("offline", onOffline);
  window.addEventListener("pointerdown", onPointer);
  beaconTimer = window.setInterval(() => {
    if (floor.online && floor.unlocked) {
      void floor.sendBeacon();
    }
  }, 60_000);
  updateTimer = window.setInterval(() => {
    if (floor.idleLocked) {
      floor.lock();
      void router.push("/unlock");
    }
    void applyUpdate(floor.updateReady, atRest.value, () => window.location.reload());
  }, 5_000);
});

onUnmounted(() => {
  window.removeEventListener("online", onOnline);
  window.removeEventListener("offline", onOffline);
  window.removeEventListener("pointerdown", onPointer);
  window.clearInterval(beaconTimer);
  window.clearInterval(updateTimer);
});
</script>

<template>
  <div class="min-h-screen" @keydown="floor.touch()">
    <header v-if="floor.unlocked" class="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
      <RouterLink to="/tasks">{{ t("floor.tasks") }}</RouterLink>
      <RouterLink to="/sync-issues">{{ t("floor.issues") }}</RouterLink>
      <span class="ml-auto text-sm" role="status">{{ t("floor.unsynced", { count: pending }) }}</span>
      <button
        type="button"
        class="min-h-12 text-sm underline"
        @click="floor.logout().then(() => router.push('/unlock'))"
      >
        {{ t("floor.logout") }}
      </button>
    </header>
    <Banner v-if="floor.offlineWarning" class="m-4" variant="warning">{{ t("floor.offlineWarn") }}</Banner>
    <Banner v-if="floor.expiryWarning" class="m-4" variant="warning">{{ t("floor.expiryWarn") }}</Banner>
    <Banner v-if="floor.upgradeRequired" class="m-4" variant="error">{{ t("floor.updateRequired") }}</Banner>
    <Banner v-if="floor.updateReady && !atRest" class="m-4" variant="info">{{ t("floor.updateReady") }}</Banner>
    <main class="p-4">
      <RouterView />
    </main>
  </div>
</template>
