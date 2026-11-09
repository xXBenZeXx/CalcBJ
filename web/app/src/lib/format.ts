// Number formatting as in the Windows app (en-US style: 1,234.56).

const whole = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });

function finite(v: number | null | undefined): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

function infinite(v: number | null | undefined): boolean {
  return v === Number.POSITIVE_INFINITY || v === Number.NEGATIVE_INFINITY;
}

/**
 * Rounds half away from zero on the decimal digits, as the Windows app formats numbers: .NET takes a double's first 15
 * significant digits (0.085 for 0.08499999999999999) and rounds those, where toFixed works on the binary value and
 * makes 1.005 into 1.00. Never returns -0.
 */
export function round(v: number, decimals: number): number {
  if (!Number.isFinite(v)) return v;
  const a = Number(Math.abs(v).toPrecision(15));
  const s = String(a);
  let r = a;
  if (s.includes("e")) r = Number(a.toFixed(Math.min(100, decimals)));
  else {
    // A number this large has no decimals left to round (and its string would get an exponent of its own).
    const scaled = String(Math.round(Number(s + "e" + decimals)));
    if (!scaled.includes("e")) r = Number(scaled + "e-" + decimals);
  }
  return v < 0 && r !== 0 ? -r : r;
}

/**
 * A positive amount rounded to `decimals` the way the Windows app rounds money (.NET Math.Round, away from zero): on
 * the binary value times the power of ten, so 1.005 gives 1.00 there and here.
 */
function roundAmount(a: number, decimals: number): number {
  const p = 10 ** decimals;
  const x = a * p;
  if (!Number.isFinite(x) || Math.abs(x) >= 1e15) return a;
  const below = Math.trunc(x);
  return (x - below >= 0.5 ? below + 1 : below) / p;
}

export function money(v: number | null): string {
  if (!finite(v)) return "—";
  // Cents first, so $999.996 shows as $1,000 and not $1000.00.
  const a = roundAmount(Math.abs(v), 2);
  return (v < 0 && a > 0 ? "-$" : "$") + (a >= 1000 ? whole.format(round(a, 0)) : a.toFixed(2));
}

export function money0(v: number | null): string {
  if (!finite(v)) return "—";
  const a = roundAmount(Math.abs(v), 0);
  return (v < 0 && a > 0 ? "-$" : "$") + whole.format(a);
}

/**
 * A risk of ruin, with enough decimals to tell small ones apart, the same on every page (Windows app: Theme.Ror):
 * 23.4%, 5.18%, 0.520%, 0.0400%, and "< 0.0001%" below that.
 */
export function ror(v: number | null | undefined): string {
  if (!finite(v)) return "—";
  const p = v * 100;
  if (p <= 0) return "0%";
  if (p < 0.0001) return "< 0.0001%";
  const d = p >= 10 ? 1 : p >= 1 ? 2 : p >= 0.1 ? 3 : 4;
  return round(p, d).toFixed(d) + "%";
}

export function pct(fraction: number | null, decimals: number): string {
  return finite(fraction) ? round(fraction * 100, decimals).toFixed(decimals) + "%" : "—";
}

export function signedPct(fraction: number | null, decimals: number): string {
  if (!finite(fraction)) return "—";
  // The sign is the value's own, as in the Windows app: a gain too small to show still reads "+0.000%".
  return (fraction > 0 ? "+" : "") + round(fraction * 100, decimals).toFixed(decimals) + "%";
}

/** Fixed decimals; "∞" for infinity and "—" for missing values (as the Windows app's Theme.F, here and below). */
export function fixed(v: number | null, decimals: number): string {
  if (infinite(v)) return "∞";
  return finite(v) ? round(v, decimals).toFixed(decimals) : "—";
}

/** Whole number with thousands separators. */
export function grouped(v: number | null): string {
  if (infinite(v)) return "∞";
  return finite(v) ? whole.format(round(v, 0)) : "—";
}

/** Up to `decimals` decimals without trailing zeros (.NET "0.##"). */
export function trim(v: number, decimals: number): string {
  if (infinite(v)) return "∞";
  return finite(v) ? String(round(v, decimals)) : "—";
}

/** Rounds to the nearest integer, ties to even (.NET Math.Round). */
function roundEven(x: number): number {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

/** Rounds an entered count tag to the grid the simulator counts exactly: thirds, else 0.05 (as the Windows app). */
export function snapTag(v: number): number {
  const third = roundEven(v * 3) / 3;
  return Math.abs(v - third) < 0.005 ? third : roundEven(v * 20) / 20;
}

/** "+2", "0", "-1.5": count tags and index values. */
export function signedTrim(v: number, decimals = 2): string {
  const s = trim(v, decimals);
  // No sign on a value that shows as 0, as in the Windows app ("+0.##;-0.##;0").
  return Number.isFinite(v) && round(v, decimals) > 0 ? "+" + s : s;
}

export function signed(v: number): string {
  return v > 0 ? "+" + v : String(v);
}

/** 1.5K, 200M, 2B. Values that would round up to 1000 of a unit move to the next one ("1M", not "1000K"). */
export function count(n: number): string {
  if (n >= 999_950_000) return trim(n / 1e9, n % 1e9 === 0 ? 0 : 2) + "B";
  if (n >= 999_950) return trim(n / 1e6, n % 1e6 === 0 ? 0 : 1) + "M";
  if (n >= 1e3) return trim(n / 1e3, 1) + "K";
  return String(n);
}

/** Count label in a ramp: the first and last rows cover everything beyond them. */
export function countLabel(v: number, min: number, max: number): string {
  const s = signed(v);
  if (v === min && min !== max) return "≤ " + s;
  if (v === max && min !== max) return "≥ " + s;
  return s;
}

/**
 * Lenient number input: ignores $, %, + and spaces, and reads both the app's own format (25,000 and 12,345.67) and
 * a decimal comma (25,5 or 1.234,5). With both marks the later one is the decimal mark; commas alone are thousands
 * separators when they group the digits in threes (1,000 or 1,000,000), otherwise a decimal comma.
 */
export function parseNumber(text: string): number | null {
  let s = text.trim().replace(/[$%+≥≤\s]/g, "").replace(/[−–]/g, "-");
  if (s === "" || s === "-") return null;
  const comma = s.lastIndexOf(","), dot = s.lastIndexOf(".");
  if (comma >= 0 && dot >= 0) s = dot > comma ? s.replace(/,/g, "") : s.replace(/\./g, "").replace(",", ".");
  else if (comma >= 0) s = /^-?[1-9]\d{0,2}(,\d{3})+$/.test(s) ? s.replace(/,/g, "") : s.replace(",", ".");
  else if (/^-?[1-9]\d{0,2}(\.\d{3}){2,}$/.test(s)) s = s.replace(/\./g, "");
  // Decimal numbers only (Number() would also read "0x10").
  if (!/^-?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(s)) return null;
  const v = Number(s);
  return Number.isFinite(v) ? v : null;
}
