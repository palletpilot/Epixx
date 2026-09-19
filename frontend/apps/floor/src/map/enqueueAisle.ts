import type { FloorDb, LocationRow } from "../db";
import { uuidv7 } from "../uuid";
import { enqueueCommand } from "../sync/outbox";
import { DEFAULT_DIMS, type Dims, type GeneratedAisle, type MapNode } from "./generateAisle";

function asLocation(
  warehouseId: string,
  type: string,
  node: MapNode,
  parentPath: string,
): LocationRow {
  const label = node.code.replaceAll("-", "_");
  const path = parentPath ? `${parentPath}.${label}` : label;
  return {
    id: node.id,
    warehouse_id: warehouseId,
    parent_id: node.parent_id,
    type,
    code: node.code,
    path,
    barcode: node.code,
    status: "active",
  };
}

export async function enqueueAisleMap(
  db: FloorDb,
  args: {
    warehouseId: string;
    deviceId: string;
    userId: string;
    built: GeneratedAisle;
    dimsByLevel: Dims[];
  },
): Promise<void> {
  const { warehouseId, deviceId, userId, built } = args;
  const now = Date.now();
  const layers: Array<{ type: string; nodes: MapNode[]; parentType?: string }> = [
    { type: "aisle", nodes: built.aisle },
    { type: "rack", nodes: built.racks },
    { type: "level", nodes: built.levels },
    { type: "bin", nodes: built.bins },
  ];
  const pathById = new Map<string, string>();

  await db.transaction("rw", db.outbox, db.locations, async () => {
    let t = 0;
    for (const layer of layers) {
      const parentId = layer.nodes[0]?.parent_id ?? null;
      await enqueueCommand(db, {
        id: uuidv7(),
        type: "CreateLocationBatch",
        v: 1,
        payload: {
          warehouse_id: warehouseId,
          parent_id: parentId,
          type: layer.type,
          locations: layer.nodes.map((n) => ({
            id: n.id,
            code: n.code,
            parent_id: n.parent_id,
          })),
        },
        device_id: deviceId,
        user_id: userId,
        created_at: new Date(now + t).toISOString(),
        occurred_at: new Date(now + t).toISOString(),
      });
      t += 1;
      for (const node of layer.nodes) {
        const parentPath = node.parent_id ? (pathById.get(node.parent_id) ?? "") : "";
        const row = asLocation(warehouseId, layer.type, node, parentPath);
        pathById.set(node.id, row.path ?? node.code.replaceAll("-", "_"));
        await db.locations.put(row);
      }
    }

    const perLevel = args.dimsByLevel.length === built.levels.length;
    if (perLevel) {
      for (let i = 0; i < built.levels.length; i++) {
        const level = built.levels[i];
        const dims = args.dimsByLevel[i] ?? DEFAULT_DIMS;
        const ids = built.bins.filter((b) => b.parent_id === level?.id).map((b) => b.id);
        if (ids.length === 0) {
          continue;
        }
        await enqueueCommand(db, {
          id: uuidv7(),
          type: "SetLocationDimensions",
          v: 1,
          payload: { ids, ...dims },
          device_id: deviceId,
          user_id: userId,
          created_at: new Date(now + t).toISOString(),
          occurred_at: new Date(now + t).toISOString(),
        });
        t += 1;
      }
    } else {
      const dims = args.dimsByLevel[0] ?? DEFAULT_DIMS;
      await enqueueCommand(db, {
        id: uuidv7(),
        type: "SetLocationDimensions",
        v: 1,
        payload: { ids: built.bins.map((b) => b.id), ...dims },
        device_id: deviceId,
        user_id: userId,
        created_at: new Date(now + t).toISOString(),
        occurred_at: new Date(now + t).toISOString(),
      });
    }
  });
}
