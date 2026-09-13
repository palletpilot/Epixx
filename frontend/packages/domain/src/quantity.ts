import Decimal from "decimal.js";

export class Quantity {
  readonly #value: Decimal;

  private constructor(value: Decimal) {
    this.#value = value;
  }

  static parse(raw: string): Quantity {
    return new Quantity(new Decimal(raw));
  }

  toString(): string {
    return this.#value.toFixed();
  }

  format(locale: string): string {
    const raw = this.toString();
    const decimal = new Intl.NumberFormat(locale).format(1.1).includes(",") ? "," : ".";
    const [whole, frac] = raw.split(".");
    return frac !== undefined ? `${whole}${decimal}${frac}` : raw;
  }
}
