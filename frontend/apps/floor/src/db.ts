import Dexie, { type Table } from "dexie";

export type OutboxState = "pending" | "sent" | "acked" | "confirmed" | "rejected";

export type OutboxRow = {
  id: string;
  type: string;
  v: number;
  payload: unknown;
  created_at: string;
  occurred_at: string;
  device_id: string;
  user_id: string;
  state: OutboxState;
  sent_at?: string;
};

export type ConfirmedRow = {
  id: string;
  confirmed_at: string;
};

export type TaskRow = {
  id: string;
  warehouse_id: string;
  type: string;
  status: string;
  assignee_user_id?: string | null;
  assigned_until?: string | null;
  suggested_location_id?: string | null;
  created_at?: string;
  requested_qty_base?: string;
};

export type CursorRow = {
  warehouse_id: string;
  seq: number;
  feed_epoch: string;
  snapshot_schema: string;
};

export type SessionRow = {
  user_id: string;
  display_name: string;
  encrypted_refresh_token: string;
  pin_hash: string;
  failed_attempts: number;
  locked_until?: string | null;
  full_login_required?: boolean;
  token_expires_at?: string | null;
};

export type DeviceRow = {
  id: string;
  secret: string;
  warehouse_ids: string[];
  tenant_id?: string;
  crypto_key_handle?: CryptoKey;
  revoked?: boolean;
  access_token?: string;
};

export type LocationRow = {
  id: string;
  warehouse_id: string;
  parent_id?: string | null;
  type: string;
  code: string;
  path?: string;
  height_mm?: number | null;
  width_mm?: number | null;
  depth_mm?: number | null;
  max_weight_g?: number | null;
  barcode?: string;
  status?: string;
  is_system?: boolean;
};

export type WarehouseRow = {
  id: string;
  name: string;
  code_pattern?: string | null;
  activated_at?: string | null;
};

export type ArticleRow = {
  id: string;
  sku: string;
  name: string;
  status: string;
  base_uom_id: string;
  quantity_precision: number;
  quantity_step: string;
  packaging_levels: { id: string; rank: number; name: string; qty_in_base: string }[];
};

export type UnitRow = {
  id: string;
  code: string;
  dimension: string;
  factor_to_dimension_base: string;
  display_name_sv: string;
  display_name_en: string;
};

export class FloorDb extends Dexie {
  outbox!: Table<OutboxRow, string>;
  confirmed!: Table<ConfirmedRow, string>;
  tasks!: Table<TaskRow, string>;
  locations!: Table<LocationRow, string>;
  warehouses!: Table<WarehouseRow, string>;
  articles!: Table<ArticleRow, string>;
  units!: Table<UnitRow, string>;
  cursor!: Table<CursorRow, string>;
  sessions!: Table<SessionRow, string>;
  device!: Table<DeviceRow, string>;

  constructor(name = "lagerkraft-floor") {
    super(name);
    this.version(1).stores({
      outbox: "id, type, v, created_at, device_id, user_id, state, sent_at",
      confirmed: "id, confirmed_at",
      tasks: "id, warehouse_id, status",
      cursor: "warehouse_id",
      sessions: "user_id",
      device: "id",
    });
    this.version(2).stores({
      locations: "id, warehouse_id, parent_id, type, code",
      warehouses: "id",
    });
    this.version(3).stores({
      articles: "id, sku, status",
      units: "id, code",
    });
  }
}

let shared: FloorDb | undefined;

export function getDb(): FloorDb {
  shared ??= new FloorDb();
  return shared;
}

export function resetSharedDb(): void {
  shared = undefined;
}
