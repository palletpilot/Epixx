export type JwtClaims = {
  sub: string;
  tid?: string;
  exp?: number;
  dev?: string;
  own?: unknown;
  ra?: unknown;
};

function unquote(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return value == null ? undefined : String(value);
  }
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed === "string") {
      return parsed;
    }
  } catch {
    // not JSON
  }
  return value.replace(/^"+|"+$/g, "");
}

export function parseJwt(token: string): JwtClaims {
  const parts = token.split(".");
  if (parts.length < 2 || !parts[1]) {
    return { sub: "" };
  }
  const json = atob(parts[1].replace(/-/g, "+").replace(/_/g, "/"));
  const raw = JSON.parse(json) as Record<string, unknown>;
  return {
    sub: unquote(raw.sub) ?? "",
    tid: unquote(raw.tid),
    exp: typeof raw.exp === "number" ? raw.exp : undefined,
    dev: unquote(raw.dev),
    own: raw.own,
    ra: raw.ra,
  };
}
