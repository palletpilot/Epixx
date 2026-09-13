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

export class FloorDb extends Dexie {
  outbox!: Table<OutboxRow, string>;
  confirmed!: Table<ConfirmedRow, string>;
  tasks!: Table<TaskRow, string>;
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
