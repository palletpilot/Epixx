/**
 * Office-list contract fixture copied from WmsCore M1a (PR 8, 945431d)
 * snapshot and change JSON. Snake_case names match Program.cs
 * JsonNamingPolicy.SnakeCaseLower. The pinned stock_balance id is a literal;
 * this file must not compute UUIDv5.
 */

export const PR8_STOCK_BALANCE_ID = "f4faf703-c1f7-5805-9e1f-aed95cc6831f";
export const PR8_HANDLING_UNIT_ID = "01900000-0000-7000-8000-000000000001";
export const PR8_ARTICLE_ID = "01900000-0000-7000-8000-000000000002";
export const PR8_WAREHOUSE_ID = "01900000-0000-7000-8000-0000000000aa";
export const PR8_LOCATION_ID = "01900000-0000-7000-8000-000000000010";
export const PR8_UNLOCATED_ID = "01900000-0000-7000-8000-000000000011";
export const PR8_DEVIATION_ID = "01900000-0000-7000-8000-000000000020";
export const PR8_COMMAND_ID = "01900000-0000-7000-8000-000000000030";
export const PR8_ACTOR_ID = "01900000-0000-7000-8000-000000000040";
export const PR8_FEED_EPOCH = "01900000-0000-7000-8000-000000000050";
export const PR8_SNAPSHOT_SEQ = 17;
export const PR8_SNAPSHOT_SCHEMA = "M1aWarehouseScopedFeed";

const occurredAt = "2026-09-19T08:00:01.000Z";
const recordedAt = "2026-09-19T08:00:01.100Z";

function snapshotEnvelope(entity: string, items: unknown[]) {
  return {
    feed_epoch: PR8_FEED_EPOCH,
    warehouse: PR8_WAREHOUSE_ID,
    entity,
    page: 1,
    seq: PR8_SNAPSHOT_SEQ,
    snapshot_schema: PR8_SNAPSHOT_SCHEMA,
    items,
  };
}

/** GetSnapshot entity=StockBalance — StockBalanceDto with id. */
export const pr8StockBalanceSnapshot = snapshotEnvelope("StockBalance", [
  {
    id: PR8_STOCK_BALANCE_ID,
    location_id: PR8_LOCATION_ID,
    article_id: PR8_ARTICLE_ID,
    handling_unit_id: PR8_HANDLING_UNIT_ID,
    qty_base: "4",
    reserved_qty_base: "0",
  },
]);

/** GetSnapshot entity=Location — LocationDto including is_system and code. */
export const pr8LocationSnapshot = snapshotEnvelope("Location", [
  {
    id: PR8_LOCATION_ID,
    warehouse_id: PR8_WAREHOUSE_ID,
    parent_id: null,
    type: "bin",
    code: "A-01-01-01",
    path: "A-01-01-01",
    height_mm: null,
    width_mm: null,
    depth_mm: null,
    max_weight_g: null,
    barcode: "A-01-01-01",
    status: "active",
    is_system: false,
  },
  {
    id: PR8_UNLOCATED_ID,
    warehouse_id: PR8_WAREHOUSE_ID,
    parent_id: null,
    type: "system",
    code: "UNLOCATED",
    path: "UNLOCATED",
    height_mm: null,
    width_mm: null,
    depth_mm: null,
    max_weight_g: null,
    barcode: "UNLOCATED",
    status: "active",
    is_system: true,
  },
]);

/**
 * GetSnapshot entity=Deviation — anonymous item from InternalApi.
 * No status field (this schema has no deviation.status column).
 */
export const pr8DeviationSnapshot = snapshotEnvelope("Deviation", [
  {
    id: PR8_DEVIATION_ID,
    warehouse_id: PR8_WAREHOUSE_ID,
    kind: "occupied_bin",
    command_id: PR8_COMMAND_ID,
    detail: {
      location_id: PR8_LOCATION_ID,
      existing_handling_unit_id: "01900000-0000-7000-8000-000000000003",
      incoming_handling_unit_id: PR8_HANDLING_UNIT_ID,
      lpn: "LPN-in",
      command_id: PR8_COMMAND_ID,
    },
    created_at: occurredAt,
  },
]);

function changeEnvelope(entries: unknown[]) {
  return {
    feed_epoch: PR8_FEED_EPOCH,
    warehouse: PR8_WAREHOUSE_ID,
    entries,
  };
}

const stockBalanceDto = {
  id: PR8_STOCK_BALANCE_ID,
  location_id: PR8_LOCATION_ID,
  article_id: PR8_ARTICLE_ID,
  handling_unit_id: PR8_HANDLING_UNIT_ID,
  qty_base: "9",
  reserved_qty_base: "0",
};

/** GET /internal/changes entry envelope (seq, entity, id, op, payload, …). */
export const pr8StockBalanceUpsertChanges = changeEnvelope([
  {
    seq: PR8_SNAPSHOT_SEQ + 1,
    entity: "stock_balance",
    id: PR8_STOCK_BALANCE_ID,
    op: "upsert",
    payload: stockBalanceDto,
    command_id: PR8_COMMAND_ID,
    actor: PR8_ACTOR_ID,
    warehouse_id: PR8_WAREHOUSE_ID,
    occurred_at: occurredAt,
    recorded_at: recordedAt,
  },
]);

export const pr8StockBalanceDeleteChanges = changeEnvelope([
  {
    seq: PR8_SNAPSHOT_SEQ + 2,
    entity: "stock_balance",
    id: PR8_STOCK_BALANCE_ID,
    op: "delete",
    payload: stockBalanceDto,
    command_id: PR8_COMMAND_ID,
    actor: PR8_ACTOR_ID,
    warehouse_id: PR8_WAREHOUSE_ID,
    occurred_at: occurredAt,
    recorded_at: recordedAt,
  },
]);

export const pr8EmptyChanges = changeEnvelope([]);
