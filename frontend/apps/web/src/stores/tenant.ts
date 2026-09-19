import { defineStore } from "pinia";
import { useAuthStore } from "./auth";
import { platformUrl, wmsUrl } from "../env";

export type Warehouse = { id: string; name: string; code_pattern?: string | null; activated_at?: string | null };

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
    async createWarehouse(input: {
      name: string;
      code_pattern?: string;
      claim_minutes?: number;
      night_shift?: boolean;
      blind_count?: boolean;
      zone_picking?: boolean;
    }): Promise<void> {
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
          name: input.name,
          code_pattern: input.code_pattern || null,
          claim_minutes: input.claim_minutes ?? 30,
          night_shift: input.night_shift ?? false,
          blind_count: input.blind_count ?? false,
          zone_picking: input.zone_picking ?? false,
        }),
      });
      if (res.ok) {
        const body = (await res.json()) as Warehouse;
        this.warehouses = [...this.warehouses, body];
      }
    },
  },
});
