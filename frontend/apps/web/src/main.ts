import { createApp } from "vue";
import { createPinia } from "pinia";
import { VueQueryPlugin } from "@tanstack/vue-query";
import { createLagerkraftI18n } from "@lagerkraft/i18n";
import App from "./App.vue";
import { router } from "./router";
import "@lagerkraft/ui/style.css";
import "./style.css";

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.use(createLagerkraftI18n());
app.use(VueQueryPlugin);
app.mount("#app");
