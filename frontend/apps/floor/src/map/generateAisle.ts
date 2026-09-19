import { uuidv7 } from "../uuid";

export type MapNode = { id: string; code: string; parent_id: string | null };

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

function join(tokens: Token[], parts: string[]): string {
  return tokens.map((token, i) => `${token.sepBefore}${parts[i] ?? ""}`).join("");
}

export const DEFAULT_PATTERN = "{aisle}-{rack:2}-{level:2}-{bin:2}";
// ponytail: hardcoded until L6 warehouse form stores default dims on the warehouse
export const DEFAULT_DIMS = { height_mm: 1200, width_mm: 800, depth_mm: 400, max_weight_g: 500000 };

export type Dims = {
  height_mm: number;
  width_mm: number;
  depth_mm: number;
  max_weight_g: number;
};

export function generateAisle(opts: {
  pattern: string;
  aisle: string;
  racks: number;
  levels: number;
  bins: number;
  newId?: () => string;
}): {
  aisle: MapNode[];
  racks: MapNode[];
  levels: MapNode[];
  bins: MapNode[];
  count: number;
  firstBin: string;
  lastBin: string;
} {
  const tokens = parse(opts.pattern);
  if (tokens.length < 4) {
    throw new Error("invalid pattern");
  }
  const newId = opts.newId ?? uuidv7;
  const aisleCode = formatToken(tokens[0]!, opts.aisle.trim());
  const aisleId = newId();
  const aisle: MapNode[] = [{ id: aisleId, code: aisleCode, parent_id: null }];
  const racks: MapNode[] = [];
  const levels: MapNode[] = [];
  const bins: MapNode[] = [];
  for (let r = 1; r <= opts.racks; r++) {
    const rackCode = join(tokens.slice(0, 2), [aisleCode, formatToken(tokens[1]!, r)]);
    const rackId = newId();
    racks.push({ id: rackId, code: rackCode, parent_id: aisleId });
    for (let lv = 1; lv <= opts.levels; lv++) {
      const levelCode = join(tokens.slice(0, 3), [
        aisleCode,
        formatToken(tokens[1]!, r),
        formatToken(tokens[2]!, lv),
      ]);
      const levelId = newId();
      levels.push({ id: levelId, code: levelCode, parent_id: rackId });
      for (let b = 1; b <= opts.bins; b++) {
        const binCode = join(tokens.slice(0, 4), [
          aisleCode,
          formatToken(tokens[1]!, r),
          formatToken(tokens[2]!, lv),
          formatToken(tokens[3]!, b),
        ]);
        bins.push({ id: newId(), code: binCode, parent_id: levelId });
      }
    }
  }
  return {
    aisle,
    racks,
    levels,
    bins,
    count: aisle.length + racks.length + levels.length + bins.length,
    firstBin: bins[0]?.code ?? "",
    lastBin: bins.at(-1)?.code ?? "",
  };
}

export type GeneratedAisle = ReturnType<typeof generateAisle>;
