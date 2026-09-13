export function platformUrl(): string {
  return import.meta.env.VITE_PLATFORM_URL ?? "http://localhost:5101";
}

export function syncUrl(): string {
  return import.meta.env.VITE_SYNC_URL ?? "http://localhost:5103";
}

export function wmsUrl(): string {
  return import.meta.env.VITE_WMS_URL ?? "http://localhost:5102";
}
