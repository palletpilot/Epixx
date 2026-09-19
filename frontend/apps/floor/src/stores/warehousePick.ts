export type WarehouseOption = { id: string; name: string };

export function warehousePickOptions(
  deviceWarehouseIds: string[] | undefined,
  snapshot: WarehouseOption[],
): WarehouseOption[] {
  const bound = deviceWarehouseIds ?? [];
  if (bound.length === 0) {
    return snapshot;
  }

  return bound.map((id) => snapshot.find((w) => w.id === id) ?? { id, name: id });
}
