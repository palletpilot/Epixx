import { defineConfig, devices } from "@playwright/test";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const dir = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(dir, "../..");

export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  use: {
    baseURL: "http://127.0.0.1:5174",
    trace: "on-first-retry",
  },
  webServer: [
    {
      command: "npx vite --config apps/floor/vite.config.ts --host 127.0.0.1 --port 5174",
      cwd: frontend,
      url: "http://127.0.0.1:5174",
      reuseExistingServer: true,
      timeout: 60_000,
    },
    {
      command: "npx vite --config apps/web/vite.config.ts --host 127.0.0.1 --port 5173",
      cwd: frontend,
      url: "http://127.0.0.1:5173",
      reuseExistingServer: true,
      timeout: 60_000,
    },
  ],
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
