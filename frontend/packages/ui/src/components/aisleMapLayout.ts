export type MapLocation = {
  id: string;
  code: string;
  type: string;
  parent_id?: string | null;
};

export type AisleLevelRow = {
  level: MapLocation;
  bins: MapLocation[];
};

export type AisleRackColumn = {
  rack: MapLocation;
  rows: AisleLevelRow[];
};

export type AisleBlock = {
  aisle: MapLocation;
  columns: AisleRackColumn[];
  /** Highest hyllplan first so the floor sits at the bottom of the schematic. */
  rowCount: number;
};

function byCode(a: MapLocation, b: MapLocation): number {
  return a.code.localeCompare(b.code);
}

export function lastSegment(code: string): string {
  const i = code.lastIndexOf("-");
  return i === -1 ? code : code.slice(i + 1);
}

export function formatMapLabel(prefix: string | undefined, code: string): string {
  return prefix ? `${prefix} ${code}` : code;
}

export function groupAisleMap(locations: MapLocation[]): AisleBlock[] {
  const aisleRows = locations.filter((l) => l.type === "aisle").sort(byCode);
  const racks = locations.filter((l) => l.type === "rack");
  const levels = locations.filter((l) => l.type === "level");
  const bins = locations.filter((l) => l.type === "bin");
  return aisleRows.map((aisle) => {
    const aisleRacks = racks.filter((r) => r.parent_id === aisle.id).sort(byCode);
    const columns = aisleRacks.map((rack) => {
      const rackLevels = levels
        .filter((lv) => lv.parent_id === rack.id)
        .sort(byCode)
        .reverse();
      return {
        rack,
        rows: rackLevels.map((level) => ({
          level,
          bins: bins.filter((b) => b.parent_id === level.id).sort(byCode),
        })),
      };
    });
    const rowCount = Math.max(0, ...columns.map((c) => c.rows.length));
    return { aisle, columns, rowCount };
  });
}
