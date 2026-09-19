<script setup lang="ts">
import { Button } from "@lagerkraft/ui";
import { useI18n } from "vue-i18n";
import { useRouter } from "vue-router";
import { useAuthStore } from "../stores/auth";
import AuthLayout from "./AuthLayout.vue";

const { t } = useI18n();
const auth = useAuthStore();
const router = useRouter();

async function choose(tenantId: string) {
  const result = await auth.chooseTenant(tenantId);
  if (result === "authenticated") {
    await router.push("/app/warehouses");
  }
}
</script>

<template>
  <AuthLayout :brand="t('brand.name')" :title="t('auth.chooserTitle')" :lead="t('brand.lead')">
    <ul class="flex flex-col gap-2">
      <li v-for="m in auth.memberships" :key="m.tenant_id">
        <Button class="w-full" type="button" @click="choose(m.tenant_id)">
          {{ m.company_name }}
        </Button>
      </li>
    </ul>
  </AuthLayout>
</template>
