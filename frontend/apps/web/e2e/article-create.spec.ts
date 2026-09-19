import { expect, test, type Page } from "@playwright/test";

function jwt(): string {
  const payload = {
    sub: "user-1",
    tid: "11111111-1111-7111-8111-111111111111",
    own: "true",
    ra: JSON.stringify([{ r: "tenant_admin", w: "*" }]),
    sv: 1,
    amr: "pwd",
  };
  return `eyJhbGciOiJub25lIn0.${Buffer.from(JSON.stringify(payload)).toString("base64url")}.sig`;
}

async function mockApis(page: Page): Promise<void> {
  const token = jwt();
  const st = {
    id: "01900000-0000-7000-8000-000000000101",
    code: "st",
    dimension: "count",
    factor_to_dimension_base: "1",
    display_name_sv: "st",
    display_name_en: "st",
  };
  let created: { id: string; sku: string; name: string } | null = null;

  await page.route("http://localhost:5101/**", async (route) => {
    const url = route.request().url();
    if (url.includes("/auth/login")) {
      await route.fulfill({
        json: {
          access_token: token,
          refresh_token: "refresh",
          token_type: "Bearer",
          expires_in: 900,
        },
      });
      return;
    }
    if (url.endsWith("/me")) {
      await route.fulfill({
        json: { lifecycle_state: "Trialing", trial_ends_at: null, slug: "demo-lager" },
      });
      return;
    }
    await route.fulfill({ status: 404, body: "" });
  });

  await page.route("http://localhost:5102/**", async (route) => {
    const url = route.request().url();
    const method = route.request().method();
    if (url.includes("/warehouses") && method === "GET") {
      await route.fulfill({
        json: [{ id: "01900000-0000-7000-8000-000000000001", name: "Dev warehouse" }],
      });
      return;
    }
    if (url.includes("/units") && method === "GET") {
      await route.fulfill({ json: [st] });
      return;
    }
    if (url.includes("/articles") && method === "GET") {
      await route.fulfill({ json: created ? [created] : [] });
      return;
    }
    if (url.includes("/articles") && method === "POST") {
      const body = (route.request().postDataJSON() ?? {}) as {
        id?: string;
        sku?: string;
        name?: string;
      };
      created = {
        id: body.id ?? "01900000-0000-7000-8000-000000000201",
        sku: body.sku ?? "KAFFE-500",
        name: body.name ?? "Kaffe",
      };
      await route.fulfill({ status: 201, json: created });
      return;
    }
    await route.fulfill({ status: 404, body: "" });
  });
}

test("create article then list shows it", async ({ page }) => {
  await mockApis(page);
  await page.goto("/login");
  await page.getByLabel(/e-post|email/i).fill("anna@demo.local");
  await page.getByLabel(/lösenord|password/i).fill("Passw0rd!");
  await page.getByRole("button", { name: /^fortsätt$|^continue$/i }).click();
  await expect(page).toHaveURL(/\/app\/warehouses/);

  await page.goto("/app/articles");
  await expect(page.getByRole("heading", { name: /artiklar|artikel|articles/i })).toBeVisible();

  await page.getByLabel(/artikelnummer|sku/i).fill("KAFFE-500");
  await page.getByLabel(/^namn$|^name$/i).fill("Kaffe");
  await page.getByRole("button", { name: /skapa artikel|create article/i }).click();

  await expect(page.getByText("KAFFE-500")).toBeVisible();
});
