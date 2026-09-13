import eslint from "@eslint/js";
import eslintConfigPrettier from "eslint-config-prettier";
import pluginVue from "eslint-plugin-vue";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: [
      "**/node_modules/**",
      "**/dist/**",
      "**/generated/**",
      "tools/**",
      "scripts/**",
    "**/e2e/**",
    "**/playwright.config.ts",
    "**/test-results/**",
    "**/playwright-report/**",
    ],
  },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs["flat/recommended"],
  eslintConfigPrettier,
  {
    files: ["**/*.vue"],
    languageOptions: {
      parserOptions: {
        parser: tseslint.parser,
      },
    },
  },
  {
    files: ["**/*.ts", "**/*.vue"],
    rules: {
      "@typescript-eslint/no-explicit-any": "error",
      "no-undef": "off",
      "vue/multi-word-component-names": "off",
      "vue/require-default-prop": "off",
    },
  },
);
