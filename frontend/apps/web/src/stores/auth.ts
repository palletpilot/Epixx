import { can as domainCan, parseRoleAssignments, type RoleAssignment } from "@lagerkraft/domain";
import { defineStore } from "pinia";
import { platformUrl } from "../env";

const ACCESS = "lk.access";
const REFRESH = "lk.refresh";

export type MembershipChoice = {
  tenant_id: string;
  company_name: string;
  slug: string;
};

export type LoginResult = "authenticated" | "chooser" | "totp" | "provisioning" | "sso" | "error";

export type JwtClaims = {
  sub: string;
  tid?: string;
  own?: unknown;
  ra?: unknown;
  sv?: unknown;
  amr?: string;
  purpose?: string;
};

function readSession(key: string): string | null {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeSession(key: string, value: string | null): void {
  try {
    if (value === null) {
      sessionStorage.removeItem(key);
    } else {
      sessionStorage.setItem(key, value);
    }
  } catch {
    // private mode
  }
}

export function parseJwt(token: string): JwtClaims {
  const parts = token.split(".");
  if (parts.length < 2 || !parts[1]) {
    return { sub: "" };
  }
  const json = atob(parts[1].replace(/-/g, "+").replace(/_/g, "/"));
  const raw = JSON.parse(json) as Record<string, unknown>;
  return {
    sub: unquote(raw.sub) ?? "",
    tid: unquote(raw.tid),
    own: raw.own,
    ra: parseRa(raw.ra),
    sv: raw.sv,
    amr: unquote(raw.amr),
    purpose: unquote(raw.purpose),
  };
}

function unquote(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return value == null ? undefined : String(value);
  }
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed === "string") {
      return parsed;
    }
  } catch {
    // not a JSON string
  }
  return value.replace(/^"+|"+$/g, "");
}

function parseRa(raw: unknown): unknown {
  if (typeof raw !== "string") {
    return raw;
  }
  try {
    const once: unknown = JSON.parse(raw);
    return typeof once === "string" ? JSON.parse(once) : once;
  } catch {
    return [];
  }
}

function isOwnerValue(own: unknown): boolean {
  if (own === true || own === "true") {
    return true;
  }
  if (typeof own === "string") {
    return unquote(own) === "true";
  }
  return false;
}

async function readErrorCode(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { code?: string; error?: string };
    return body.code ?? body.error ?? `http_${res.status}`;
  } catch {
    return `http_${res.status}`;
  }
}

export const useAuthStore = defineStore("auth", {
  state: () => ({
    accessToken: readSession(ACCESS),
    refreshToken: readSession(REFRESH),
    chooserToken: null as string | null,
    memberships: [] as MembershipChoice[],
    lastError: null as string | null,
  }),
  getters: {
    claims(state): JwtClaims | null {
      return state.accessToken ? parseJwt(state.accessToken) : null;
    },
    isAuthenticated(state): boolean {
      return Boolean(state.accessToken);
    },
    tenantId(): string | undefined {
      return this.claims?.tid;
    },
    userId(): string | undefined {
      return this.claims?.sub;
    },
    isOwner(): boolean {
      return isOwnerValue(this.claims?.own);
    },
    assignments(): RoleAssignment[] {
      return parseRoleAssignments(this.claims?.ra);
    },
  },
  actions: {
    applyTokens(access: string, refresh: string): void {
      this.accessToken = access;
      this.refreshToken = refresh;
      this.chooserToken = null;
      this.memberships = [];
      this.lastError = null;
      writeSession(ACCESS, access);
      writeSession(REFRESH, refresh);
    },
    can(permission: string, warehouseId?: string): boolean {
      return domainCan(this.assignments, permission, warehouseId);
    },
    async login(email: string, password: string, totp?: string): Promise<LoginResult> {
      this.lastError = null;
      const res = await fetch(`${platformUrl()}/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password, totp: totp || null }),
      });
      if (res.status === 401) {
        const code = await readErrorCode(res);
        this.lastError = code;
        if (code === "totp_invalid" || code === "sso_enforced_owner_totp") {
          return "totp";
        }
        if (code === "no_membership") {
          return "provisioning";
        }
        if (code === "sso_enforced") {
          return "sso";
        }
        return "error";
      }
      if (!res.ok) {
        this.lastError = await readErrorCode(res);
        return "error";
      }
      const body = (await res.json()) as {
        access_token?: string;
        refresh_token?: string;
        chooser_token?: string;
        memberships?: MembershipChoice[];
      };
      if (body.chooser_token && body.memberships) {
        this.chooserToken = body.chooser_token;
        this.memberships = body.memberships;
        return "chooser";
      }
      if (body.access_token && body.refresh_token) {
        this.applyTokens(body.access_token, body.refresh_token);
        return "authenticated";
      }
      this.lastError = "invalid_response";
      return "error";
    },
    async chooseTenant(tenantId: string): Promise<LoginResult> {
      if (!this.chooserToken) {
        return "error";
      }
      const res = await fetch(`${platformUrl()}/auth/choose-tenant`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ chooser_token: this.chooserToken, tenant_id: tenantId }),
      });
      if (!res.ok) {
        this.lastError = await readErrorCode(res);
        return "error";
      }
      const body = (await res.json()) as { access_token: string; refresh_token: string };
      this.applyTokens(body.access_token, body.refresh_token);
      return "authenticated";
    },
    async switchTenant(tenantId: string): Promise<boolean> {
      if (!this.accessToken) {
        return false;
      }
      const res = await fetch(`${platformUrl()}/auth/switch-tenant`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${this.accessToken}`,
        },
        body: JSON.stringify({ tenant_id: tenantId }),
      });
      if (!res.ok) {
        this.lastError = await readErrorCode(res);
        return false;
      }
      const body = (await res.json()) as { access_token: string; refresh_token: string };
      this.applyTokens(body.access_token, body.refresh_token);
      return true;
    },
    async logout(): Promise<void> {
      const refresh = this.refreshToken;
      if (refresh) {
        try {
          await fetch(`${platformUrl()}/auth/logout`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ refresh_token: refresh }),
          });
        } catch {
          // still clear local session
        }
      }
      this.accessToken = null;
      this.refreshToken = null;
      this.chooserToken = null;
      this.memberships = [];
      writeSession(ACCESS, null);
      writeSession(REFRESH, null);
    },
    async authedFetch(input: string, init: RequestInit = {}): Promise<Response> {
      const headers = new Headers(init.headers);
      if (this.accessToken) {
        headers.set("Authorization", `Bearer ${this.accessToken}`);
      }
      return fetch(input, { ...init, headers });
    },
  },
});
