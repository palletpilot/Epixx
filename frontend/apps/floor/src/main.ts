import { createApp } from "vue";
import { createPinia } from "pinia";
import { createLagerkraftI18n } from "@lagerkraft/i18n";
import App from "./App.vue";
import { router } from "./router";
import { useFloorStore } from "./stores/floor";
import "@lagerkraft/ui/style.css";
import "./style.css";

const app = createApp(App);
const pinia = createPinia();
app.use(pinia);
app.use(router);
app.use(createLagerkraftI18n());
app.mount("#app");

void import("virtual:pwa-register")
  .then(({ registerSW }) => {
    registerSW({
      onNeedRefresh() {
        useFloorStore(pinia).updateReady = true;
      },
    });
  })
  .catch(() => {
    // vite-plugin-pwa virtual module is build-time only when the plugin is off
  });
