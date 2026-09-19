import { describe, expect, it } from "vitest";
import { generateAisle } from "./generateAisle";

const Pattern = "{aisle}-{rack:2}-{level:2}-{bin:2}";

describe("generateAisle", () => {
  it("A, 2 racks, 3 levels, 2 bins → 1+2+6+12 locations, first bin A-01-01-01", () => {
    let n = 0;
    const ids = () => `id-${n++}`;
    const built = generateAisle({
      pattern: Pattern,
      aisle: "A",
      racks: 2,
      levels: 3,
      bins: 2,
      newId: ids,
    });
    expect(built.aisle).toHaveLength(1);
    expect(built.racks).toHaveLength(2);
    expect(built.levels).toHaveLength(6);
    expect(built.bins).toHaveLength(12);
    expect(built.count).toBe(21);
    expect(built.firstBin).toBe("A-01-01-01");
    expect(built.lastBin).toBe("A-02-03-02");
    expect(built.aisle[0]?.code).toBe("A");
    expect(built.racks.map((r) => r.code)).toEqual(["A-01", "A-02"]);
    expect(built.bins[0]?.code).toBe("A-01-01-01");
    expect(built.bins[0]?.parent_id).toBe(built.levels[0]?.id);
    expect(built.racks[0]?.parent_id).toBe(built.aisle[0]?.id);
  });
});
