<script setup lang="ts">
import { Banner } from "@lagerkraft/ui";
import { computed } from "vue";
import { RouterLink, RouterView } from "vue-router";
import { useI18n } from "vue-i18n";
import { useAuthStore } from "../stores/auth";
import { useTenantStore } from "../stores/tenant";
import { router } from "../router";

const { t } = useI18n();
const auth = useAuthStore();
const tenant = useTenantStore();
const days = computed(() => tenant.trialDaysLeft);

async function logout() {
  await auth.logout();
  await router.push("/login");
}
</script>

<template>
  <div class="min-h-screen">
    <header class="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
      <nav class="flex flex-wrap gap-3" :aria-label="t('nav.tenant')">
        <RouterLink to="/app/warehouses">{{ t("nav.warehouses") }}</RouterLink>
        <RouterLink to="/app/tasks">{{ t("nav.tasks") }}</RouterLink>
        <RouterLink to="/app/devices">{{ t("nav.devices") }}</RouterLink>
        <RouterLink to="/app/users">{{ t("nav.users") }}</RouterLink>
        <RouterLink to="/app/sso">{{ t("nav.sso") }}</RouterLink>
      </nav>
      <label class="ml-auto flex items-center gap-2 text-sm">
        <span>{{ t("nav.tenant") }}</span>
        <select
          class="h-9 rounded-md border border-input bg-background px-2"
          :aria-label="t('nav.tenant')"
          :value="auth.tenantId"
          @change="auth.switchTenant(($event.target as HTMLSelectElement).value)"
        >
          <option v-for="m in auth.memberships" :key="m.tenant_id" :value="m.tenant_id">
            {{ m.company_name }}
          </option>
          <option v-if="auth.memberships.length === 0 && auth.tenantId" :value="auth.tenantId">
            {{ tenant.context.slug ?? auth.tenantId }}
          </option>
        </select>
      </label>
      <button type="button" class="text-sm underline" @click="logout">{{ t("nav.logout") }}</button>
    </header>
    <Banner v-if="tenant.showTrialBanner && days != null" class="m-4" variant="warning">
      {{ t("trial.banner", { days }) }}
    </Banner>
    <main class="p-4">
      <RouterView />
    </main>
  </div>
</template>
