export type StockRow = {
  location_id: string | null;
  article_id: string;
  handling_unit_id: string;
  qty_base: string;
  reserved_qty_base: string;
};

export type DeviationDetail = {
  location_id?: string;
  actual_location_id?: string;
  suggested_location_id?: string;
  existing_handling_unit_id?: string;
  incoming_handling_unit_id?: string;
  existing_movement_id?: string;
  incoming_movement_id?: string;
  lpn?: string;
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
  return { stock: new Map(), deviations: new Map() };
}

export function snapshotUrl(baseUrl: string, warehouse: string, entity: "StockBalance"): string {
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

export function compareStockLocations(a: string | null, b: string | null): number {
  if (!a && !b) {
    return 0;
  }
  if (!a) {
    return 1;
  }
  if (!b) {
    return -1;
  }
  return a.localeCompare(b);
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

export function listsFromStockSnapshot(stockBody: unknown): OfficeLists {
  return {
    stock: toStockMap(mapStockSnapshot(stockBody)),
    deviations: new Map(),
  };
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
    }
  }
  return { stock, deviations };
}

export async function loadOfficeLists(
  getJson: SyncGetJson,
  baseUrl: string,
  warehouseId: string,
): Promise<OfficeLists> {
  const stockBody = await getJson(snapshotUrl(baseUrl, warehouseId, "StockBalance"));
  let lists = listsFromStockSnapshot(stockBody);
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
    stock.set(row.handling_unit_id, row);
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

function asStock(value: unknown, fallbackHuId: string): StockRow | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const raw = value as Record<string, unknown>;
  const handlingUnitId =
    typeof raw.handling_unit_id === "string" && raw.handling_unit_id !== ""
      ? raw.handling_unit_id
      : fallbackHuId;
  const qty = asDecimalString(raw.qty_base);
  const reserved = asDecimalString(raw.reserved_qty_base);
  if (!handlingUnitId || qty === null || reserved === null) {
    return null;
  }
  return {
    location_id: asStockLocationId(raw.location_id),
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
      existing_movement_id: optionalString(detail.existing_movement_id),
      incoming_movement_id: optionalString(detail.incoming_movement_id),
      lpn: optionalString(detail.lpn),
    },
    created_at: String(raw.created_at ?? ""),
  };
}

function optionalString(value: unknown): string | undefined {
  return value == null ? undefined : String(value);
}

function asStockLocationId(value: unknown): string | null {
  return typeof value === "string" && value !== "" ? value : null;
}

function toStockMap(rows: StockRow[]): Map<string, StockRow> {
  return new Map(rows.map((row) => [row.handling_unit_id, row]));
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
