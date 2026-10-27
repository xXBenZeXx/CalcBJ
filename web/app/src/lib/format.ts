// Number formatting as in the Windows app (en-US style: 1,234.56).

const whole = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });

function finite(v: number | null | undefined): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

export function money(v: number | null): string {
  if (!finite(v)) return "—";
  const a = Math.abs(v);
  return (v < 0 ? "-$" : "$") + (a >= 1000 ? whole.format(a) : a.toFixed(2));
}

export function money0(v: number | null): string {
  if (!finite(v)) return "—";
  return (v < 0 ? "-$" : "$") + whole.format(Math.abs(v));
}

export function pct(fraction: number | null, decimals: number): string {
  return finite(fraction) ? (fraction * 100).toFixed(decimals) + "%" : "—";
}

export function signedPct(fraction: number | null, decimals: number): string {
  if (!finite(fraction)) return "—";
  const s = (fraction * 100).toFixed(decimals) + "%";
  return fraction > 0 ? "+" + s : s;
}

/** Fixed decimals; "∞" for infinity and "—" for missing values. */
export function fixed(v: number | null, decimals: number): string {
  if (v === Number.POSITIVE_INFINITY) return "∞";
  return finite(v) ? v.toFixed(decimals) : "—";
}

/** Whole number with thousands separators. */
export function grouped(v: number | null): string {
  if (v === Number.POSITIVE_INFINITY) return "∞";
  return finite(v) ? whole.format(v) : "—";
}

/** Up to `decimals` decimals without trailing zeros (.NET "0.##"). */
export function trim(v: number, decimals: number): string {
  return finite(v) ? String(Number(v.toFixed(decimals))) : "—";
}

/** "+2", "0", "-1.5": count tags and index values. */
export function signedTrim(v: number, decimals = 2): string {
  const s = trim(v, decimals);
  return v > 0 ? "+" + s : s;
}

export function signed(v: number): string {
  return v > 0 ? "+" + v : String(v);
}

/** 1.5K, 200M, 2B. */
export function count(n: number): string {
  if (n >= 1e9) return trim(n / 1e9, n % 1e9 === 0 ? 0 : 2) + "B";
  if (n >= 1e6) return trim(n / 1e6, n % 1e6 === 0 ? 0 : 1) + "M";
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

/** Lenient number input: ignores $, %, + and thousands separators; accepts a decimal comma. */
export function parseNumber(text: string): number | null {
  let s = text.trim().replace(/[$%+≥≤\s]/g, "").replace(/[−–]/g, "-");
  s = s.includes(".") ? s.replace(/,/g, "") : s.replace(",", ".");
  if (s === "" || s === "-") return null;
  const v = Number(s);
  return Number.isFinite(v) ? v : null;
}
