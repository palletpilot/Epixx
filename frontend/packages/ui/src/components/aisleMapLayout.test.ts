import { describe, expect, it } from "vitest";
import { formatMapLabel, groupAisleMap, lastSegment, type MapLocation } from "./aisleMapLayout";

function loc(partial: MapLocation): MapLocation {
  return partial;
}

describe("aisleMapLayout", () => {
  it("groups racks as columns, hyllplan as rows with floor at the bottom, bins as cells", () => {
    const aisle = loc({ id: "a", code: "A", type: "aisle" });
    const rack = loc({ id: "r", code: "A-01", type: "rack", parent_id: "a" });
    const low = loc({ id: "l1", code: "A-01-01", type: "level", parent_id: "r" });
    const high = loc({ id: "l2", code: "A-01-02", type: "level", parent_id: "r" });
    const b1 = loc({ id: "b1", code: "A-01-01-01", type: "bin", parent_id: "l1" });
    const b2 = loc({ id: "b2", code: "A-01-02-01", type: "bin", parent_id: "l2" });
    const [block] = groupAisleMap([aisle, rack, low, high, b1, b2]);
    expect(block?.columns).toHaveLength(1);
    expect(block?.columns[0]?.rows.map((row) => row.level.code)).toEqual(["A-01-02", "A-01-01"]);
    expect(block?.columns[0]?.rows[0]?.bins.map((b) => b.code)).toEqual(["A-01-02-01"]);
  });

  it("uses the last code segment for cell labels", () => {
    expect(lastSegment("A-01-03-02")).toBe("02");
    expect(formatMapLabel("Gång", "A")).toBe("Gång A");
  });
});
