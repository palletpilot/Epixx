import { defineStore } from "pinia";
import { useAuthStore } from "./auth";
import { platformUrl, wmsUrl } from "../env";

export type Warehouse = { id: string; name: string };

export type TenantContext = {
  lifecycle_state?: string;
  trial_ends_at?: string | null;
  company_name?: string;
  slug?: string;
  maintenance?: boolean;
};

export const useTenantStore = defineStore("tenant", {
  state: () => ({
    context: {} as TenantContext,
    warehouses: [] as Warehouse[],
  }),
  getters: {
    trialDaysLeft(state): number | null {
      const raw = state.context.trial_ends_at;
      if (!raw) {
        return null;
      }
      const ms = Date.parse(raw) - Date.now();
      return Math.ceil(ms / 86_400_000);
    },
    isTrialExpired(state): boolean {
      return state.context.lifecycle_state === "TrialExpired";
    },
    showTrialBanner(): boolean {
      if (this.isTrialExpired) {
        return false;
      }
      const days = this.trialDaysLeft;
      return days != null && days <= 10;
    },
  },
  actions: {
    async refresh(): Promise<void> {
      const auth = useAuthStore();
      if (!auth.accessToken || !auth.tenantId) {
        return;
      }
      const me = await auth.authedFetch(`${platformUrl()}/me`);
      if (me.ok) {
        this.context = (await me.json()) as TenantContext;
      }
      const list = await fetch(`${wmsUrl()}/warehouses`, {
        headers: { "Lagerkraft-Tenant-Id": auth.tenantId },
      });
      if (list.ok) {
        const body = (await list.json()) as Warehouse[] | { items?: Warehouse[] };
        this.warehouses = Array.isArray(body) ? body : (body.items ?? []);
      }
    },
    async createWarehouse(name: string): Promise<void> {
      const auth = useAuthStore();
      if (!auth.tenantId) {
        return;
      }
      const id = crypto.randomUUID();
      const res = await fetch(`${wmsUrl()}/warehouses`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Lagerkraft-Tenant-Id": auth.tenantId,
        },
        body: JSON.stringify({
          id,
          name,
          code_pattern: "A-01-01",
          claim_minutes: 30,
          night_shift: false,
          blind_count: false,
          zone_picking: false,
        }),
      });
      if (res.ok) {
        this.warehouses = [...this.warehouses, { id, name }];
      }
    },
  },
});
