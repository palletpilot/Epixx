import createClient from "openapi-fetch";
import type { paths as PlatformPaths } from "./generated/platform";
import type { paths as SyncGatewayPaths } from "./generated/sync-gateway";

export type { PlatformPaths, SyncGatewayPaths };

export function createPlatformClient(opts: {
  baseUrl: string;
  headers?: HeadersInit;
  fetch?: typeof globalThis.fetch;
}) {
  return createClient<PlatformPaths>({
    baseUrl: opts.baseUrl,
    headers: opts.headers,
    fetch: opts.fetch,
  });
}

export function createSyncGatewayClient(opts: {
  baseUrl: string;
  headers?: HeadersInit;
  fetch?: typeof globalThis.fetch;
}) {
  return createClient<SyncGatewayPaths>({
    baseUrl: opts.baseUrl,
    headers: opts.headers,
    fetch: opts.fetch,
  });
}
