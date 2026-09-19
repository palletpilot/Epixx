import { describe, expect, it } from "vitest";
import { previewLocationCode } from "./previewLocationCode";

describe("previewLocationCode", () => {
  it("default pattern example is A-01-03-02", () => {
    expect(previewLocationCode("{aisle}-{rack:2}-{level:2}-{bin:2}")).toBe("A-01-03-02");
  });
});
