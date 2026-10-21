export type StockRow = {
  /** Opaque stock_balance identity from the snapshot item or change id. Never handling_unit_id. Provisional on snapshot until StockBalanceDto includes id. */
  id: string;
  location_id: string;
  article_id: string;
  handling_unit_id: string;
  qty_base: string;
  reserved_qty_base: string;
};

/** Provisional: occupied_bin movement ids until Sync/WmsCore lock the payload. */
export type DeviationDetail = {
  location_id?: string;
  actual_location_id?: string;
  suggested_location_id?: string;
  existing_handling_unit_id?: string;
  incoming_handling_unit_id?: string;
  movement_id?: string;
  displaced_movement_id?: string;
  lpn?: string;
};

/**
 * Provisional join key for the warehouse UNLOCATED system location.
 * RC Location snapshot already has `is_system` and `code`; StockBalance does not.
 * RC does not yet seed code UNLOCATED.
 */
export const UNLOCATED_CODE = "UNLOCATED";

export type LocationRow = {
  id: string;
  warehouse_id: string;
  code: string;
  is_system: boolean;
};

export type DeviationRow = {
  warehouse_id: string;
  id: string;
  kind: string;
  command_id: string;
  detail: DeviationDetail;
  created_at: string;
};

export type ChangeRow = {
  seq: number;
  entity: string;
  id: string;
  op: string;
  payload: unknown;
  command_id?: string | null;
  actor?: string | null;
  occurred_at: string;
  recorded_at?: string;
  warehouse_id?: string | null;
};

export type OfficeLists = {
  stock: Map<string, StockRow>;
  deviations: Map<string, DeviationRow>;
  locations: Map<string, LocationRow>;
};

export type SyncGetJson = (url: string) => Promise<unknown>;

export class SyncLoadError extends Error {
  readonly status: number;

  constructor(status: number) {
    super(`http_${status}`);
    this.name = "SyncLoadError";
    this.status = status;
  }
}

export function emptyLists(): OfficeLists {
  return { stock: new Map(), deviations: new Map(), locations: new Map() };
}

export function snapshotUrl(baseUrl: string, warehouse: string, entity: "StockBalance" | "Location"): string {
  const query = new URLSearchParams({ warehouse, entity });
  return `${baseUrl}/sync/snapshot?${query.toString()}`;
}

export function changesUrl(baseUrl: string, warehouse: string, since: number): string {
  const query = new URLSearchParams({ warehouse, since: String(since) });
  return `${baseUrl}/sync/changes?${query.toString()}`;
}

/** Drop a change with no warehouse_id until the spec patch lands. Do not flip yet. */
export function changeMatchesSelectedWarehouse(entry: ChangeRow, warehouseId: string): boolean {
  if (!entry.warehouse_id) {
    return false;
  }
  return entry.warehouse_id === warehouseId;
}

export function asDecimalString(value: unknown): string | null {
  if (typeof value !== "string" || value === "") {
    return null;
  }
  return value;
}

export function formatDecimalSvSe(value: string): string {
  const negative = value.startsWith("-");
  const raw = negative ? value.slice(1) : value;
  const [whole = "0", fraction] = raw.split(".");
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, "\u00A0");
  const body = fraction === undefined ? grouped : `${grouped},${fraction}`;
  return negative ? `\u2212${body}` : body;
}

export function isUnlocatedSystemLocation(location: Pick<LocationRow, "is_system" | "code"> | undefined): boolean {
  return location?.is_system === true && location.code === UNLOCATED_CODE;
}

export function compareStockLocationOrder(
  aLocationId: string,
  bLocationId: string,
  locations: Map<string, LocationRow>,
): number {
  const aUnlocated = isUnlocatedSystemLocation(locations.get(aLocationId));
  const bUnlocated = isUnlocatedSystemLocation(locations.get(bLocationId));
  if (aUnlocated !== bUnlocated) {
    return aUnlocated ? 1 : -1;
  }
  return aLocationId.localeCompare(bLocationId);
}

export function compareDecimalStrings(a: string, b: string): number {
  const aNeg = a.startsWith("-");
  const bNeg = b.startsWith("-");
  if (aNeg !== bNeg) {
    return aNeg ? -1 : 1;
  }
  const cmp = compareUnsignedDecimal(aNeg ? a.slice(1) : a, bNeg ? b.slice(1) : b);
  return aNeg ? -cmp : cmp;
}

export function mapStockSnapshot(body: unknown): StockRow[] {
  const items = snapshotItems(body);
  const rows: StockRow[] = [];
  for (const item of items) {
    const row = asStock(item, "");
    if (row) {
      rows.push(row);
    }
  }
  return rows;
}

export function mapLocationSnapshot(body: unknown): LocationRow[] {
  const items = snapshotItems(body);
  const rows: LocationRow[] = [];
  for (const item of items) {
    const row = asLocation(item, "");
    if (row) {
      rows.push(row);
    }
  }
  return rows;
}

export function listsFromSnapshots(stockBody: unknown, locationBody: unknown): OfficeLists {
  return {
    stock: toStockMap(mapStockSnapshot(stockBody)),
    deviations: new Map(),
    locations: toLocationMap(mapLocationSnapshot(locationBody)),
  };
}

export function listsFromStockSnapshot(stockBody: unknown): OfficeLists {
  return listsFromSnapshots(stockBody, { items: [] });
}

export function mapChangeRows(body: unknown): ChangeRow[] {
  if (typeof body !== "object" || body === null) {
    return [];
  }
  const raw = body as Record<string, unknown>;
  const list = raw.entries;
  if (!Array.isArray(list)) {
    return [];
  }
  const rows: ChangeRow[] = [];
  for (const item of list) {
    const row = asChangeRow(item);
    if (row) {
      rows.push(row);
    }
  }
  return rows;
}

export function applyChanges(lists: OfficeLists, entries: ChangeRow[], warehouseId: string): OfficeLists {
  const stock = new Map(lists.stock);
  const deviations = new Map(lists.deviations);
  const locations = new Map(lists.locations);
  const ordered = [...entries].sort((a, b) => a.seq - b.seq);
  for (const entry of ordered) {
    if (!changeMatchesSelectedWarehouse(entry, warehouseId)) {
      continue;
    }
    const entity = entry.entity.toLowerCase();
    if (entity === "stock_balance") {
      applyStockRow(stock, entry);
    } else if (entity === "deviation") {
      applyDeviationRow(deviations, entry);
    } else if (entity === "location") {
      applyLocationRow(locations, entry);
    }
  }
  return { stock, deviations, locations };
}

export async function loadOfficeLists(
  getJson: SyncGetJson,
  baseUrl: string,
  warehouseId: string,
): Promise<OfficeLists> {
  const stockBody = await getJson(snapshotUrl(baseUrl, warehouseId, "StockBalance"));
  const locationBody = await getJson(snapshotUrl(baseUrl, warehouseId, "Location"));
  let lists = listsFromSnapshots(stockBody, locationBody);
  let since = 0;
  for (;;) {
    const page = mapChangeRows(await getJson(changesUrl(baseUrl, warehouseId, since)));
    if (page.length === 0) {
      break;
    }
    lists = applyChanges(lists, page, warehouseId);
    const lastSeq = page.reduce((max, row) => (row.seq > max ? row.seq : max), page[0]!.seq);
    if (lastSeq <= since) {
      break;
    }
    since = lastSeq;
  }
  return lists;
}

function applyStockRow(stock: Map<string, StockRow>, entry: ChangeRow): void {
  if (entry.op === "delete") {
    stock.delete(entry.id);
    return;
  }
  const row = asStock(entry.payload, entry.id);
  if (row) {
    stock.set(row.id, row);
  }
}

export function deviationLocationId(row: DeviationRow): string {
  if (row.kind === "wrong_location") {
    return row.detail.actual_location_id ?? row.detail.location_id ?? "";
  }
  return row.detail.location_id ?? "";
}

export function deviationDetailParts(row: DeviationRow): string[] {
  return [
    row.detail.location_id,
    row.detail.suggested_location_id,
    row.detail.actual_location_id,
    row.detail.existing_handling_unit_id,
    row.detail.incoming_handling_unit_id,
    row.detail.lpn,
  ].filter((part): part is string => Boolean(part));
}

function applyDeviationRow(deviations: Map<string, DeviationRow>, entry: ChangeRow): void {
  if (entry.op === "delete") {
    deviations.delete(entry.id);
    return;
  }
  const row = asDeviation(entry.payload, entry.id);
  if (row) {
    deviations.set(row.id, row);
  }
}

function snapshotItems(body: unknown): unknown[] {
  if (typeof body !== "object" || body === null) {
    return [];
  }
  const items = (body as { items?: unknown }).items;
  return Array.isArray(items) ? items : [];
}

function asChangeRow(value: unknown): ChangeRow | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const raw = value as Record<string, unknown>;
  if (typeof raw.seq !== "number" || typeof raw.entity !== "string" || typeof raw.id !== "string") {
    return null;
  }
  return {
    seq: raw.seq,
    entity: raw.entity,
    id: raw.id,
    op: String(raw.op ?? ""),
    payload: raw.payload,
    command_id: raw.command_id == null ? null : String(raw.command_id),
    actor: raw.actor == null ? null : String(raw.actor),
    occurred_at: String(raw.occurred_at ?? ""),
    recorded_at: raw.recorded_at == null ? undefined : String(raw.recorded_at),
    warehouse_id: raw.warehouse_id == null || raw.warehouse_id === "" ? null : String(raw.warehouse_id),
  };
}

function asStock(value: unknown, fallbackId: string): StockRow | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const raw = value as Record<string, unknown>;
  const id = typeof raw.id === "string" && raw.id !== "" ? raw.id : fallbackId;
  const handlingUnitId = typeof raw.handling_unit_id === "string" ? raw.handling_unit_id : "";
  const qty = asDecimalString(raw.qty_base);
  const reserved = asDecimalString(raw.reserved_qty_base);
  if (!id || !handlingUnitId || qty === null || reserved === null) {
    return null;
  }
  return {
    id,
    location_id: typeof raw.location_id === "string" ? raw.location_id : String(raw.location_id ?? ""),
    article_id: String(raw.article_id ?? ""),
    handling_unit_id: handlingUnitId,
    qty_base: qty,
    reserved_qty_base: reserved,
  };
}

function asDeviation(value: unknown, fallbackId: string): DeviationRow | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const raw = value as Record<string, unknown>;
  const id = typeof raw.id === "string" ? raw.id : fallbackId;
  if (!id) {
    return null;
  }
  const detail =
    typeof raw.detail === "object" && raw.detail !== null ? (raw.detail as Record<string, unknown>) : {};
  return {
    warehouse_id: String(raw.warehouse_id ?? ""),
    id,
    kind: String(raw.kind ?? ""),
    command_id: String(raw.command_id ?? ""),
    detail: {
      location_id: optionalString(detail.location_id),
      actual_location_id: optionalString(detail.actual_location_id),
      suggested_location_id: optionalString(detail.suggested_location_id),
      existing_handling_unit_id: optionalString(detail.existing_handling_unit_id),
      incoming_handling_unit_id: optionalString(detail.incoming_handling_unit_id),
      movement_id: optionalString(detail.movement_id) ?? optionalString(raw.movement_id),
      displaced_movement_id: optionalString(detail.displaced_movement_id) ?? optionalString(raw.displaced_movement_id),
      lpn: optionalString(detail.lpn),
    },
    created_at: String(raw.created_at ?? ""),
  };
}

function optionalString(value: unknown): string | undefined {
  return value == null ? undefined : String(value);
}

function applyLocationRow(locations: Map<string, LocationRow>, entry: ChangeRow): void {
  if (entry.op === "delete") {
    locations.delete(entry.id);
    return;
  }
  const row = asLocation(entry.payload, entry.id);
  if (row) {
    locations.set(row.id, row);
  }
}

function asLocation(value: unknown, fallbackId: string): LocationRow | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const raw = value as Record<string, unknown>;
  const id = typeof raw.id === "string" && raw.id !== "" ? raw.id : fallbackId;
  if (!id) {
    return null;
  }
  return {
    id,
    warehouse_id: String(raw.warehouse_id ?? ""),
    code: String(raw.code ?? ""),
    is_system: Boolean(raw.is_system),
  };
}

function toStockMap(rows: StockRow[]): Map<string, StockRow> {
  return new Map(rows.map((row) => [row.id, row]));
}

function toLocationMap(rows: LocationRow[]): Map<string, LocationRow> {
  return new Map(rows.map((row) => [row.id, row]));
}

function compareUnsignedDecimal(a: string, b: string): number {
  const [aWholeRaw = "0", aFrac = ""] = a.split(".");
  const [bWholeRaw = "0", bFrac = ""] = b.split(".");
  const aWhole = aWholeRaw.replace(/^0+(?=\d)/, "") || "0";
  const bWhole = bWholeRaw.replace(/^0+(?=\d)/, "") || "0";
  if (aWhole.length !== bWhole.length) {
    return aWhole.length - bWhole.length;
  }
  if (aWhole !== bWhole) {
    return aWhole < bWhole ? -1 : 1;
  }
  const pad = Math.max(aFrac.length, bFrac.length);
  const aF = aFrac.padEnd(pad, "0");
  const bF = bFrac.padEnd(pad, "0");
  if (aF === bF) {
    return 0;
  }
  return aF < bF ? -1 : 1;
}
