export type StockRow = {
  id: string;
  warehouse_id: string;
  location_id: string;
  handling_unit_id: string;
  article_id: string | null;
  qty_base: number;
  created_at: string;
};

export type DeviationDetail = {
  location_id?: string;
  existing_handling_unit_id?: string;
  incoming_handling_unit_id?: string;
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

export function emptyLists(): OfficeLists {
  return { stock: new Map(), deviations: new Map() };
}

export function snapshotUrl(baseUrl: string, warehouse: string, entity: "stock" | "deviation"): string {
  const query = new URLSearchParams({ warehouse, entity });
  return `${baseUrl}/sync/snapshot?${query.toString()}`;
}

export function changesUrl(baseUrl: string, warehouse: string, since: number): string {
  const query = new URLSearchParams({ warehouse, since: String(since) });
  return `${baseUrl}/sync/changes?${query.toString()}`;
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

export function mapDeviationSnapshot(body: unknown): DeviationRow[] {
  const items = snapshotItems(body);
  const rows: DeviationRow[] = [];
  for (const item of items) {
    const row = asDeviation(item, "");
    if (row) {
      rows.push(row);
    }
  }
  return rows;
}

export function listsFromSnapshots(stockBody: unknown, deviationBody: unknown): OfficeLists {
  return {
    stock: toMap(mapStockSnapshot(stockBody)),
    deviations: toMap(mapDeviationSnapshot(deviationBody)),
  };
}

export function mapChangeRows(body: unknown): ChangeRow[] {
  if (typeof body !== "object" || body === null) {
    return [];
  }
  const raw = body as Record<string, unknown>;
  const list = raw.entries ?? raw.items;
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
    if (!entry.warehouse_id) {
      continue;
    }
    if (entry.warehouse_id !== warehouseId) {
      continue;
    }
    const entity = entry.entity.toLowerCase();
    if (entity === "stock") {
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
  const [stockBody, deviationBody, changeBody] = await Promise.all([
    getJson(snapshotUrl(baseUrl, warehouseId, "stock")),
    getJson(snapshotUrl(baseUrl, warehouseId, "deviation")),
    getJson(changesUrl(baseUrl, warehouseId, 0)),
  ]);
  return applyChanges(listsFromSnapshots(stockBody, deviationBody), mapChangeRows(changeBody), warehouseId);
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
  const id = typeof raw.id === "string" ? raw.id : fallbackId;
  if (!id) {
    return null;
  }
  return {
    id,
    warehouse_id: String(raw.warehouse_id ?? ""),
    location_id: String(raw.location_id ?? ""),
    handling_unit_id: String(raw.handling_unit_id ?? ""),
    article_id: raw.article_id == null ? null : String(raw.article_id),
    qty_base: typeof raw.qty_base === "number" ? raw.qty_base : 0,
    created_at: String(raw.created_at ?? ""),
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
      existing_handling_unit_id: optionalString(detail.existing_handling_unit_id),
      incoming_handling_unit_id: optionalString(detail.incoming_handling_unit_id),
      lpn: optionalString(detail.lpn),
    },
    created_at: String(raw.created_at ?? ""),
  };
}

function optionalString(value: unknown): string | undefined {
  return value == null ? undefined : String(value);
}

function toMap<T extends { id: string }>(rows: T[]): Map<string, T> {
  return new Map(rows.map((row) => [row.id, row]));
}
