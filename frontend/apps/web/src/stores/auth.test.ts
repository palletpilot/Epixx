import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useAuthStore } from "./auth";

function jwt(payload: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: "none", typ: "JWT" })).toString("base64url");
  const body = Buffer.from(JSON.stringify(payload)).toString("base64url");
  return `${header}.${body}.sig`;
}

const tenantId = "11111111-1111-7111-8111-111111111111";
const warehouseId = "22222222-2222-7222-8222-222222222222";
const access = jwt({
  sub: "user-1",
  tid: tenantId,
  own: "true",
  ra: JSON.stringify([{ r: "tenant_admin", w: "*" }]),
  sv: 1,
  amr: "pwd",
});

describe("auth store", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    sessionStorage.clear();
    vi.unstubAllGlobals();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("stores tokens and derived claims on a single-membership login", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({
          access_token: access,
          refresh_token: "refresh-1",
          token_type: "Bearer",
          expires_in: 900,
        }),
      }),
    );

    const auth = useAuthStore();
    const result = await auth.login("owner@demo.local", "Passw0rd!");
    expect(result).toBe("authenticated");
    expect(auth.isAuthenticated).toBe(true);
    expect(auth.tenantId).toBe(tenantId);
    expect(auth.isOwner).toBe(true);
    expect(auth.can("inventory.move", warehouseId)).toBe(true);
    expect(sessionStorage.getItem("lk.access")).toBe(access);
  });

  it("returns chooser instead of authenticating when login lists memberships", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({
          chooser_token: "chooser",
          memberships: [
            { tenant_id: tenantId, company_name: "A", slug: "a" },
            { tenant_id: "33333333-3333-7333-8333-333333333333", company_name: "B", slug: "b" },
          ],
        }),
      }),
    );

    const auth = useAuthStore();
    const result = await auth.login("multi@demo.local", "Passw0rd!");
    expect(result).toBe("chooser");
    expect(auth.isAuthenticated).toBe(false);
    expect(auth.memberships).toHaveLength(2);
  });

  it("asks for TOTP when the server returns totp_invalid", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: false,
        status: 401,
        json: async () => ({ code: "totp_invalid" }),
      }),
    );

    const auth = useAuthStore();
    const result = await auth.login("mfa@demo.local", "Passw0rd!");
    expect(result).toBe("totp");
    expect(auth.isAuthenticated).toBe(false);
  });

  it("clears session on logout", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 204,
        json: async () => ({}),
      }),
    );
    const auth = useAuthStore();
    auth.applyTokens(access, "refresh-1");
    await auth.logout();
    expect(auth.isAuthenticated).toBe(false);
    expect(sessionStorage.getItem("lk.access")).toBeNull();
  });
});
