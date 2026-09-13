import { ROLE_PERMISSIONS } from "./generated/permissions";

export type RoleAssignment = {
  role: string;
  /** Empty means every warehouse. */
  warehouseIds: readonly string[];
};

export function can(
  assignments: readonly RoleAssignment[],
  permission: string,
  warehouseId?: string,
): boolean {
  for (const assignment of assignments) {
    const granted = ROLE_PERMISSIONS[assignment.role];
    if (!granted?.includes(permission)) {
      continue;
    }
    if (
      warehouseId === undefined
      || assignment.warehouseIds.length === 0
      || assignment.warehouseIds.includes(warehouseId)
    ) {
      return true;
    }
  }
  return false;
}

/** JWT `ra` claim: `[{ "r": "floor_worker", "w": "*" | string[] }]`. */
export function parseRoleAssignments(ra: unknown): RoleAssignment[] {
  if (!Array.isArray(ra)) {
    return [];
  }
  const out: RoleAssignment[] = [];
  for (const item of ra) {
    if (typeof item !== "object" || item === null || !("r" in item)) {
      continue;
    }
    const role = (item as { r?: unknown }).r;
    if (typeof role !== "string" || role === "") {
      continue;
    }
    out.push({ role, warehouseIds: parseWarehouses((item as { w?: unknown }).w) });
  }
  return out;
}

function parseWarehouses(w: unknown): readonly string[] {
  if (w === "*" || w === undefined) {
    return [];
  }
  if (!Array.isArray(w)) {
    return [];
  }
  return w.filter((id): id is string => typeof id === "string");
}
