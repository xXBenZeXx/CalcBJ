// Number formatting and reading (src/lib/format.ts): the app shows numbers as the Windows app did and reads what
// people type in either style.
import { describe, expect, test } from "vitest";
import {
  count,
  countLabel,
  fixed,
  grouped,
  money,
  parseNumber,
  pct,
  ror,
  round,
  signedPct,
  signedTrim,
  snapTag,
  trim,
} from "../src/lib/format";

describe("parseNumber", () => {
  test.each([
    ["25,000", 25000],
    ["12,345.67", 12345.67],
    ["1,000,000", 1000000],
    ["25,5", 25.5],
    ["1.234,5", 1234.5],
    ["1.000.000", 1000000],
    ["1,0000", 1],
    ["$1,000", 1000],
    ["75%", 75],
    ["+3", 3],
    ["−2", -2],
    ["–1.5", -1.5],
    [" ≥ 4 ", 4],
    [".5", 0.5],
    ["1e3", 1000],
  ])("%s reads as %d", (text, value) => {
    expect(parseNumber(text)).toBe(value);
  });

  test.each(["", "-", "abc", "0x10", "1..2", "Infinity", "12a"])("%j is not a number", (text) => {
    expect(parseNumber(text)).toBeNull();
  });
});

describe("rounding as the Windows app did", () => {
  test("takes the first 15 significant digits, then rounds half away from zero", () => {
    expect(round(1.005, 2)).toBe(1.01);
    expect(round(0.08499999999999999, 2)).toBe(0.09);
    expect(round(-2.5, 0)).toBe(-3);
    expect(round(12.345, 2)).toBe(12.35);
  });

  test("never returns -0", () => {
    expect(Object.is(round(-0.001, 2), 0)).toBe(true);
    expect(signedTrim(-0.001)).toBe("0");
  });
});

describe("formats", () => {
  test("money rounds to cents first", () => {
    expect(money(999.996)).toBe("$1,000");
    expect(money(1234.5)).toBe("$1,235");
    expect(money(-12.5)).toBe("-$12.50");
    expect(money(1.005)).toBe("$1.00");
    expect(money(null)).toBe("—");
  });

  test("risk of ruin keeps small values apart", () => {
    expect(ror(0.234)).toBe("23.4%");
    expect(ror(0.0518)).toBe("5.18%");
    expect(ror(0.0052)).toBe("0.520%");
    expect(ror(0.0004)).toBe("0.0400%");
    expect(ror(0.0000005)).toBe("< 0.0001%");
    expect(ror(0)).toBe("0%");
    expect(ror(Number.NaN)).toBe("—");
  });

  test("percentages, fixed and grouped numbers", () => {
    expect(pct(0.12345, 2)).toBe("12.35%");
    expect(signedPct(0.0000001, 3)).toBe("+0.000%");
    expect(signedPct(-0.00528, 3)).toBe("-0.528%");
    expect(fixed(Number.POSITIVE_INFINITY, 2)).toBe("∞");
    expect(fixed(null, 2)).toBe("—");
    expect(grouped(1234567.4)).toBe("1,234,567");
    expect(trim(1.5, 2)).toBe("1.5");
    expect(trim(2, 2)).toBe("2");
  });

  test("round counts", () => {
    expect(count(999)).toBe("999");
    expect(count(1500)).toBe("1.5K");
    expect(count(999_950)).toBe("1M");
    expect(count(200e6)).toBe("200M");
    expect(count(2e9)).toBe("2B");
  });

  test("count labels and signs", () => {
    expect(countLabel(-3, -3, 8)).toBe("≤ -3");
    expect(countLabel(8, -3, 8)).toBe("≥ +8");
    expect(countLabel(0, -3, 8)).toBe("0");
    expect(signedTrim(2)).toBe("+2");
    expect(signedTrim(-1.5)).toBe("-1.5");
  });

  test("entered tags snap to thirds, else to 0.05", () => {
    expect(snapTag(0.333)).toBeCloseTo(1 / 3, 12);
    expect(snapTag(0.52)).toBe(0.5);
    expect(snapTag(1.5)).toBe(1.5);
  });
});
