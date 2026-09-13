import vue from "@vitejs/plugin-vue";
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  root: fileURLToPath(new URL(".", import.meta.url)),
  plugins: [
    vue(),
    VitePWA({
      registerType: "prompt",
      workbox: {
        globPatterns: ["**/*.{js,css,html,ico,png,svg,woff2,webmanifest}"],
        cleanupOutdatedCaches: true,
      },
      devOptions: {
        enabled: false,
      },
      manifest: {
        name: "Lagerkraft",
        short_name: "Lagerkraft",
        display: "standalone",
        start_url: "/",
        theme_color: "#111111",
        background_color: "#ffffff",
      },
    }),
  ],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    port: 5174,
  },
});
