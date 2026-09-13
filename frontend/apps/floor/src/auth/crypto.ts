export function isStandalone(): boolean {
  const nav = navigator as Navigator & { standalone?: boolean };
  if (nav.standalone === true) {
    return true;
  }
  return window.matchMedia("(display-mode: standalone)").matches;
}

export async function requestPersistentStorage(): Promise<void> {
  try {
    await navigator.storage?.persist();
  } catch {
    // iOS / unsupported
  }
}

export async function createDeviceKey(): Promise<CryptoKey> {
  return crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
}

export async function encryptToken(key: CryptoKey, token: string): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const cipher = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, new TextEncoder().encode(token));
  const packed = new Uint8Array(iv.length + cipher.byteLength);
  packed.set(iv, 0);
  packed.set(new Uint8Array(cipher), iv.length);
  let binary = "";
  packed.forEach((b) => {
    binary += String.fromCharCode(b);
  });
  return btoa(binary);
}

export async function decryptToken(key: CryptoKey, packed: string): Promise<string> {
  const raw = Uint8Array.from(atob(packed), (c) => c.charCodeAt(0));
  const iv = raw.slice(0, 12);
  const data = raw.slice(12);
  const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv }, key, data);
  return new TextDecoder().decode(plain);
}
