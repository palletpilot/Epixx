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
const tenantLabel = computed(() => {
  const fromContext = tenant.context.company_name || tenant.context.slug;
  if (fromContext) {
    return fromContext;
  }
  const match = auth.memberships.find((m) => m.tenant_id === auth.tenantId);
  return match?.company_name || match?.slug || t("nav.tenant");
});
const navClass =
  "text-sm [&.router-link-active]:underline [&.router-link-active]:underline-offset-4";

async function logout() {
  await auth.logout();
  await router.push("/login");
}
</script>

<template>
  <div class="min-h-screen">
    <header class="flex flex-wrap items-center gap-3 bg-foreground px-6 py-4 text-white">
      <img
        src="/lagerkraft-logo.png"
        :alt="t('brand.name')"
        class="h-12 w-auto object-contain"
      />
      <nav class="flex flex-wrap gap-4" :aria-label="t('nav.tenant')">
        <RouterLink :class="navClass" to="/app/warehouses">{{ t("nav.warehouses") }}</RouterLink>
        <RouterLink :class="navClass" to="/app/articles">{{ t("nav.articles") }}</RouterLink>
        <RouterLink :class="navClass" to="/app/orders">{{ t("nav.orders") }}</RouterLink>
        <RouterLink :class="navClass" to="/app/tasks">{{ t("nav.tasks") }}</RouterLink>
        <RouterLink :class="[navClass, 'text-white/50']" to="/app/devices">{{ t("nav.devices") }}</RouterLink>
        <RouterLink :class="[navClass, 'text-white/50']" to="/app/users">{{ t("nav.users") }}</RouterLink>
        <RouterLink :class="[navClass, 'text-white/50']" to="/app/sso">{{ t("nav.sso") }}</RouterLink>
      </nav>
      <label class="ml-auto flex items-center gap-2 text-sm">
        <span>{{ t("nav.tenant") }}</span>
        <select
          class="h-9 rounded-md border border-white/20 bg-white px-2 text-foreground"
          :aria-label="t('nav.tenant')"
          :value="auth.tenantId"
          @change="auth.switchTenant(($event.target as HTMLSelectElement).value)"
        >
          <option v-for="m in auth.memberships" :key="m.tenant_id" :value="m.tenant_id">
            {{ m.company_name }}
          </option>
          <option v-if="auth.memberships.length === 0 && auth.tenantId" :value="auth.tenantId">
            {{ tenantLabel }}
          </option>
        </select>
      </label>
      <button type="button" class="text-sm underline" @click="logout">{{ t("nav.logout") }}</button>
    </header>
    <Banner v-if="tenant.showTrialBanner && days != null" class="mx-6 mt-4" variant="warning">
      {{ t("trial.banner", { days }) }}
    </Banner>
    <main class="mx-auto max-w-6xl px-6 py-8">
      <RouterView />
    </main>
  </div>
</template>
