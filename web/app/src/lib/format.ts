// Number formatting as in the Windows app (en-US style: 1,234.56).

const whole = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });

function finite(v: number | null | undefined): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

/**
 * Rounds half away from zero on the decimal digits, as the Windows app formats numbers (toFixed works on the binary
 * value and makes 1.005 into 1.00). Never returns -0.
 */
export function round(v: number, decimals: number): number {
  if (!Number.isFinite(v)) return v;
  const a = Math.abs(v);
  const s = String(a);
  const r = s.includes("e") ? Number(a.toFixed(decimals)) : Number(Math.round(Number(s + "e" + decimals)) + "e-" + decimals);
  return v < 0 && r !== 0 ? -r : r;
}

export function money(v: number | null): string {
  if (!finite(v)) return "—";
  // Cents first, so $999.996 shows as $1,000 and not $1000.00.
  const a = round(Math.abs(v), 2);
  return (v < 0 && a > 0 ? "-$" : "$") + (a >= 1000 ? whole.format(a) : a.toFixed(2));
}

export function money0(v: number | null): string {
  if (!finite(v)) return "—";
  const a = round(Math.abs(v), 0);
  return (v < 0 && a > 0 ? "-$" : "$") + whole.format(a);
}

export function pct(fraction: number | null, decimals: number): string {
  return finite(fraction) ? round(fraction * 100, decimals).toFixed(decimals) + "%" : "—";
}

export function signedPct(fraction: number | null, decimals: number): string {
  if (!finite(fraction)) return "—";
  const x = round(fraction * 100, decimals);
  return (x > 0 ? "+" : "") + x.toFixed(decimals) + "%";
}

/** Fixed decimals; "∞" for infinity and "—" for missing values. */
export function fixed(v: number | null, decimals: number): string {
  if (v === Number.POSITIVE_INFINITY) return "∞";
  return finite(v) ? round(v, decimals).toFixed(decimals) : "—";
}

/** Whole number with thousands separators. */
export function grouped(v: number | null): string {
  if (v === Number.POSITIVE_INFINITY) return "∞";
  return finite(v) ? whole.format(v) : "—";
}

/** Up to `decimals` decimals without trailing zeros (.NET "0.##"). */
export function trim(v: number, decimals: number): string {
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
  return v > 0 ? "+" + s : s;
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
  const v = Number(s);
  return Number.isFinite(v) ? v : null;
}
