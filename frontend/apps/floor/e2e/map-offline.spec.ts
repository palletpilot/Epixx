import { expect, test, type Page } from "@playwright/test";

const warehouseId = "01900000-0000-7000-8000-000000000001";
const deviceId = "01900000-0000-7000-8000-000000000010";

function jwt(): string {
  const payload = {
    sub: "11111111-1111-7111-8111-111111111111",
    tid: "22222222-2222-7222-8222-222222222222",
    own: "true",
    ra: JSON.stringify([{ r: "tenant_admin", w: "*" }]),
    sv: 1,
    amr: "pwd",
    dev: deviceId,
  };
  return `eyJhbGciOiJub25lIn0.${Buffer.from(JSON.stringify(payload)).toString("base64url")}.sig`;
}

type Loc = {
  id: string;
  warehouse_id: string;
  parent_id: string | null;
  type: string;
  code: string;
};

function aisleTree(): Loc[] {
  const wh = warehouseId;
  const rows: Loc[] = [];
  const aisleId = "aisle-a";
  rows.push({ id: aisleId, warehouse_id: wh, parent_id: null, type: "aisle", code: "A" });
  for (let r = 1; r <= 2; r++) {
    const rackId = `rack-${r}`;
    const rackCode = `A-0${r}`;
    rows.push({ id: rackId, warehouse_id: wh, parent_id: aisleId, type: "rack", code: rackCode });
    for (let lv = 1; lv <= 3; lv++) {
      const levelId = `level-${r}-${lv}`;
      rows.push({
        id: levelId,
        warehouse_id: wh,
        parent_id: rackId,
        type: "level",
        code: `${rackCode}-0${lv}`,
      });
      for (let b = 1; b <= 2; b++) {
        rows.push({
          id: `bin-${r}-${lv}-${b}`,
          warehouse_id: wh,
          parent_id: levelId,
          type: "bin",
          code: `${rackCode}-0${lv}-0${b}`,
        });
      }
    }
  }
  return rows;
}

async function mockStandalone(page: Page): Promise<void> {
  await page.addInitScript(() => {
    Object.defineProperty(window, "matchMedia", {
      writable: true,
      value: (query: string) => ({
        matches: query.includes("standalone") || query.includes("display-mode"),
        media: query,
        addEventListener() {},
        removeEventListener() {},
        addListener() {},
        removeListener() {},
        dispatchEvent() {
          return false;
        },
        onchange: null,
      }),
    });
    Object.defineProperty(navigator, "storage", {
      value: {
        persist: async () => true,
        persisted: async () => true,
      },
    });
  });
}

async function mockApis(page: Page, state: { mapped: boolean }): Promise<void> {
  const token = jwt();
  const warehouse = {
    id: warehouseId,
    name: "Huvudlager",
    code_pattern: "{aisle}-{rack:2}-{level:2}-{bin:2}",
  };
  await page.route(/:5101\//, async (route) => {
    const url = route.request().url();
    if (url.includes("/devices/enroll")) {
      await route.fulfill({
        json: {
          device_id: deviceId,
          device_secret: "secret",
          warehouse_ids: [warehouseId],
          tenant_id: "22222222-2222-7222-8222-222222222222",
        },
      });
      return;
    }
    if (url.includes("/auth/login") || url.includes("/auth/pin-unlock")) {
      await route.fulfill({
        json: { access_token: token, refresh_token: "refresh", token_type: "Bearer", expires_in: 900 },
      });
      return;
    }
    if (url.endsWith("/me")) {
      await route.fulfill({ json: { lifecycle_state: "Trialing", trial_ends_at: null, slug: "demo" } });
      return;
    }
    await route.fulfill({ status: 204, body: "" });
  });
  await page.route(/:5102\//, async (route) => {
    if (route.request().url().includes("/warehouses") && route.request().method() === "GET") {
      await route.fulfill({ json: [warehouse] });
      return;
    }
    await route.fulfill({ status: 201, json: warehouse });
  });
  await page.route(/:5103\//, async (route) => {
    const url = route.request().url();
    if (url.includes("/sync/commands") && route.request().method() === "POST") {
      state.mapped = true;
      const body = route.request().postDataJSON() as { commands?: Array<{ id: string }> };
      await route.fulfill({
        json: {
          results: (body.commands ?? []).map((c) => ({ command_id: c.id, outcome: "Applied" })),
        },
      });
      return;
    }
    if (url.includes("/snapshot")) {
      const entity = new URL(url).searchParams.get("entity");
      if (entity === "Warehouse") {
        await route.fulfill({ json: { feed_epoch: "epoch-1", items: [warehouse] } });
        return;
      }
      if (entity === "Location") {
        await route.fulfill({
          json: { feed_epoch: "epoch-1", items: state.mapped ? aisleTree() : [] },
        });
        return;
      }
      await route.fulfill({ json: { feed_epoch: "epoch-1", items: [] } });
      return;
    }
    await route.fulfill({ status: 204, body: "" });
  });
}

test("map an aisle offline then see 12 bins on the office schematic", async ({ page, context, browser }) => {
  const state = { mapped: false };
  await mockStandalone(page);
  await mockApis(page, state);

  await page.goto("/enroll");
  await page.getByLabel(/anslutningskod|enrollment code/i).fill("123456");
  await page.getByLabel(/enhetsnamn|device name/i).fill("Scanner 3");
  await page.getByRole("button", { name: /anslut|enroll/i }).click();

  await page.getByLabel(/e-post|email/i).fill("anna@demo.local");
  await page.getByLabel(/lösenord|password/i).fill("Passw0rd!");
  await page.getByRole("button", { name: /^fortsätt$|^continue$/i }).click();
  await expect(page.getByLabel(/^pin$/i)).toBeVisible();
  await page.getByLabel(/^pin$/i).fill("1234");
  await page.getByRole("button", { name: /^fortsätt$|^continue$/i }).click();

  await page.getByRole("button", { name: "Huvudlager" }).click();
  await expect(page.getByRole("heading", { name: /uppgifter|tasks/i })).toBeVisible();
  await page.getByRole("link", { name: /kartlägg gång|map aisle/i }).first().click();
  await expect(page.getByLabel(/vilken gång|which aisle/i)).toBeVisible();
  await context.setOffline(true);
  await page.getByRole("button", { name: /nästa|next/i }).click();
  await page.getByRole("button", { name: /nästa|next/i }).click();
  await page.getByRole("button", { name: /nästa|next/i }).click();
  await page.getByRole("button", { name: /nästa|next/i }).click();
  await page.getByRole("button", { name: /^ja$|^yes$/i }).click();
  await expect(page.getByText(/A-01-01-01/)).toBeVisible();
  await page.getByRole("button", { name: /bekräfta|confirm/i }).click();
  await expect(page.getByRole("heading", { name: /uppgifter|tasks/i })).toBeVisible();

  const posted = page.waitForRequest(
    (req) => req.url().includes("/sync/commands") && req.method() === "POST",
  );
  await context.setOffline(false);
  await posted;

  const web = await browser.newPage();
  await mockApis(web, state);
  await web.goto("http://127.0.0.1:5173/login");
  await web.getByLabel(/e-post|email/i).fill("anna@demo.local");
  await web.getByLabel(/lösenord|password/i).fill("Passw0rd!");
  await web.getByRole("button", { name: /^fortsätt$|^continue$/i }).click();
  await expect(web).toHaveURL(/\/app\/warehouses/);
  await web.getByRole("button", { name: "Huvudlager" }).click();
  await expect(web.locator("[data-code]")).toHaveCount(12);
  await web.close();
});
