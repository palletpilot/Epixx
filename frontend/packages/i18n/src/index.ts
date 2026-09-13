import { createI18n } from "vue-i18n";
import en from "./locales/en.json";
import sv from "./locales/sv.json";

export function createLagerkraftI18n() {
  return createI18n({
    legacy: false,
    locale: "sv",
    fallbackLocale: "en",
    messages: { sv, en },
  });
}
