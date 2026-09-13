import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import type { Config } from "tailwindcss";

const dir = dirname(fileURLToPath(import.meta.url)).replace(/\\/g, "/");

export default {
  content: [`${dir}/index.html`, `${dir}/src/**/*.{vue,ts}`, `${dir}/../../packages/ui/src/**/*.{vue,ts}`],
  theme: {
    extend: {
      colors: {
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))",
        },
        border: "hsl(var(--border))",
        input: "hsl(var(--input))",
        ring: "hsl(var(--ring))",
      },
    },
  },
} satisfies Config;
