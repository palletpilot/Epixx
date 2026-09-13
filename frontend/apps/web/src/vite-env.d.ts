/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_PLATFORM_URL?: string;
  readonly VITE_SYNC_URL?: string;
  readonly VITE_WMS_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
