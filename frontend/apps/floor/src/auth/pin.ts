const enc = new TextEncoder();

function hex(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  return [...view].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function fromHex(value: string): Uint8Array {
  const bytes = new Uint8Array(value.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = Number.parseInt(value.slice(i * 2, i * 2 + 2), 16);
  }
  return bytes;
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  const copy = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(copy).set(bytes);
  return copy;
}

async function derive(pin: string, salt: Uint8Array): Promise<ArrayBuffer> {
  const key = await crypto.subtle.importKey("raw", enc.encode(pin), "PBKDF2", false, ["deriveBits"]);
  return crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: toArrayBuffer(salt), iterations: 100_000 },
    key,
    256,
  );
}

export async function hashPin(pin: string): Promise<string> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const bits = await derive(pin, salt);
  return `${hex(salt)}.${hex(bits)}`;
}

export async function verifyPin(pin: string, stored: string): Promise<boolean> {
  const [saltHex, hashHex] = stored.split(".");
  if (!saltHex || !hashHex) {
    return false;
  }
  const bits = await derive(pin, fromHex(saltHex));
  return hex(bits) === hashHex;
}

export const PIN_LOCK_AFTER = 5;
export const PIN_FULL_LOGIN_AFTER = 10;
export const PIN_LOCK_MS = 15 * 60 * 1000;
export const IDLE_LOCK_MS = 5 * 60 * 1000;
export const EXPIRY_WARN_MS = 60 * 60 * 1000;
export const OFFLINE_WARN_MS = 4 * 60 * 60 * 1000;
