import { expect, test, type Page } from "@playwright/test";

const warehouseId = "01900000-0000-7000-8000-000000000001";
const deviceId = "01900000-0000-7000-8000-000000000010";
const articleId = "01900000-0000-7000-8000-000000000201";
const binId = "01900000-0000-7000-8000-000000000501";

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

async function mockApis(page: Page, posted: string[]): Promise<void> {
  const token = jwt();
  const warehouse = { id: warehouseId, name: "Dev warehouse" };
  const article = {
    id: articleId,
    sku: "KAFFE-500",
    name: "Kaffe",
    status: "active",
    base_uom_id: "01900000-0000-7000-8000-000000000101",
    quantity_precision: 0,
    quantity_step: "1",
    packaging_levels: [{ id: "01900000-0000-7000-8000-000000000301", rank: 1, name: "st", qty_in_base: "1" }],
  };
  const bin = {
    id: binId,
    warehouse_id: warehouseId,
    parent_id: null,
    type: "bin",
    code: "A-01-01-01",
    status: "active",
    is_system: false,
  };

  await page.route("http://localhost:5101/**", async (route) => {
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
  await page.route("http://localhost:5102/**", async (route) => {
    if (route.request().url().includes("/warehouses") && route.request().method() === "GET") {
      await route.fulfill({ json: [warehouse] });
      return;
    }
    await route.fulfill({ status: 201, json: warehouse });
  });
  await page.route("http://localhost:5103/**", async (route) => {
    const url = route.request().url();
    if (url.includes("/sync/commands") && route.request().method() === "POST") {
      const body = route.request().postDataJSON() as { commands?: Array<{ id: string; type: string }> };
      for (const command of body.commands ?? []) {
        posted.push(command.type);
      }
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
        await route.fulfill({ json: { feed_epoch: "epoch-1", items: [bin] } });
        return;
      }
      if (entity === "Article") {
        await route.fulfill({ json: { feed_epoch: "epoch-1", items: [article] } });
        return;
      }
      await route.fulfill({ json: { feed_epoch: "epoch-1", items: [] } });
      return;
    }
    await route.fulfill({ status: 204, body: "" });
  });
}

test("receive KAFFE-500 then confirm putaway", async ({ page }) => {
  const posted: string[] = [];
  await mockStandalone(page);
  await mockApis(page, posted);

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

  await page.getByRole("button", { name: "Dev warehouse" }).click();
  await expect(page.getByRole("heading", { name: /uppgifter|tasks/i })).toBeVisible();
  await page.getByRole("link", { name: /ta emot|receive/i }).click();

  await expect(page.getByRole("heading", { name: /ta emot|receive/i })).toBeVisible();
  await expect(page.locator("#receive-article")).toContainText(/KAFFE-500/);
  const receivePosted = page.waitForRequest(
    (req) => req.url().includes("/sync/commands") && req.method() === "POST",
  );
  await page.getByRole("button", { name: /ta emot|receive/i }).click();
  await receivePosted;

  await expect(page.getByRole("heading", { name: /^putaway$/i })).toBeVisible();
  await page.getByRole("button", { name: /ta uppgift|claim task/i }).click();
  const putawayPosted = page.waitForRequest(
    (req) =>
      req.url().includes("/sync/commands") &&
      req.method() === "POST" &&
      JSON.stringify(req.postDataJSON()).includes("ConfirmPutaway"),
  );
  await page.getByRole("button", { name: /bekräfta inlagring|confirm putaway/i }).click();
  await putawayPosted;
  await expect(page.getByTestId("task-status")).toHaveText("done");
  expect(posted).toContain("ReceiveHandlingUnit");
  expect(posted).toContain("ConfirmPutaway");
});
