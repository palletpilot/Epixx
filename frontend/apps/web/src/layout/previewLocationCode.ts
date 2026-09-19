export const DEFAULT_PATTERN = "{aisle}-{rack:2}-{level:2}-{bin:2}";

type Token = { width: number | null; sepBefore: string };

function parse(pattern: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;
  let sep = "";
  while (i < pattern.length) {
    if (pattern[i] !== "{") {
      return [];
    }
    const close = pattern.indexOf("}", i);
    if (close < 0) {
      return [];
    }
    const inner = pattern.slice(i + 1, close);
    const colon = inner.lastIndexOf(":");
    let width: number | null = null;
    if (colon >= 0) {
      const parsed = Number(inner.slice(colon + 1));
      if (Number.isFinite(parsed)) {
        width = parsed;
      }
    }
    tokens.push({ width, sepBefore: sep });
    i = close + 1;
    const next = pattern.indexOf("{", i);
    if (next < 0) {
      break;
    }
    sep = pattern.slice(i, next);
    i = next;
  }
  return tokens;
}

function formatToken(token: Token, value: string | number): string {
  if (token.width) {
    return String(value).padStart(token.width, "0");
  }
  return String(value);
}

export function previewLocationCode(pattern: string): string {
  const tokens = parse(pattern.trim() || DEFAULT_PATTERN);
  if (tokens.length < 4) {
    return "";
  }
  const parts = [
    formatToken(tokens[0]!, "A"),
    formatToken(tokens[1]!, 1),
    formatToken(tokens[2]!, 3),
    formatToken(tokens[3]!, 2),
  ];
  return tokens.map((token, i) => `${token.sepBefore}${parts[i] ?? ""}`).join("");
}
