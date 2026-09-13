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
  await page.route("http://localhost:5101/**", async (route) => {
    const url = route.request().url();
    const method = route.request().method();
    if (url.includes("/signup/slug-available")) {
      await route.fulfill({ json: { available: true, slug: "demo-lager" } });
      return;
    }
    if (url.endsWith("/signup") && method === "POST") {
      await route.fulfill({ json: { signup_id: "s1", slug: "demo-lager" } });
      return;
    }
    if (url.includes("/signup/verify")) {
      await route.fulfill({ status: 204, body: "" });
      return;
    }
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
    if (route.request().url().includes("/warehouses") && route.request().method() === "GET") {
      await route.fulfill({
        json: [{ id: "01900000-0000-7000-8000-000000000001", name: "Dev warehouse" }],
      });
      return;
    }
    await route.fulfill({ status: 201, json: { id: "w1" } });
  });
  await page.route("http://localhost:5103/**", async (route) => {
    if (route.request().url().includes("/snapshot")) {
      await route.fulfill({ json: { items: [] } });
      return;
    }
    await route.fulfill({ status: 200, body: "" });
  });
}

test("signup to task list", async ({ page }) => {
  await mockApis(page);
  await page.goto("/signup");
  await page.getByLabel(/företagsnamn|company name/i).fill("Demo Lager");
  await page.getByLabel(/organisationsnummer|organisation number/i).fill("5566770011");
  await page.getByLabel(/^namn$|^name$/i).fill("Anna");
  await page.getByLabel(/e-post|email/i).fill("anna@demo.local");
  await page.getByLabel(/lösenord|password/i).fill("Passw0rd!");
  await page.getByRole("button", { name: /skapa|create/i }).click();
  await expect(page.getByRole("status")).toBeVisible();

  await page.goto("/verify");
  await page.getByLabel(/kod|code/i).fill("token");
  await page.getByRole("button", { name: /verifiera|verify/i }).click();

  await page.goto("/login");
  await page.getByLabel(/e-post|email/i).fill("anna@demo.local");
  await page.getByLabel(/lösenord|password/i).fill("Passw0rd!");
  await page.getByRole("button", { name: /^fortsätt$|^continue$/i }).click();
  await expect(page).toHaveURL(/\/app\/tasks/);
  await expect(page.getByRole("heading", { name: /uppgifter|tasks/i })).toBeVisible();
});
