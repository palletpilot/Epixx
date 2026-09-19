import { describe, expect, it } from "vitest";
import { warehousePickOptions } from "./warehousePick";

const fake = "01900000-0000-7000-8000-000000000001";
const nord = { id: "aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa", name: "Nord" };

describe("warehousePickOptions", () => {
  it("empty device warehouse_ids loads snapshot names, not the fake uuid", () => {
    const opts = warehousePickOptions([], [nord]);
    expect(opts).toEqual([nord]);
    expect(opts.some((o) => o.id === fake)).toBe(false);
  });

  it("bound device ids keep order and use snapshot names when present", () => {
    const opts = warehousePickOptions([nord.id], [nord]);
    expect(opts).toEqual([nord]);
  });
});
