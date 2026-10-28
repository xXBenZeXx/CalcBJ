// Series of the Chart component.

export interface ChartSeries {
  name: string;
  /** "band" fills between `values` (low) and `high`; "point" draws markers only. */
  kind: "bar" | "line" | "area" | "band" | "point";
  /** One value per x position; null leaves a gap. */
  values: (number | null)[];
  high?: (number | null)[];
  /** CSS colour, e.g. "var(--series-1)". */
  color: string;
  /** Per-bar colours (e.g. negative bars in the other pole). */
  colors?: (string | null)[];
  /** Fill opacity of areas and bands (default 0.1). */
  opacity?: number;
  /** Markers on a line. */
  markers?: boolean;
  /** Formats this series' values in the tooltip and table (default: the chart's y format). */
  format?: (v: number) => string;
}

/** A 1/2/5 × 10^n step giving roughly the requested number of intervals. */
export function niceStep(range: number, intervals: number): number {
  if (!(range > 0) || !Number.isFinite(range)) return 1;
  const raw = range / intervals;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const n = raw / mag;
  return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
}

export function ticks(lo: number, hi: number, step: number): number[] {
  const out: number[] = [];
  for (let v = Math.ceil(lo / step - 1e-9) * step; v <= hi + step * 1e-9; v += step) out.push(Math.abs(v) < step * 1e-9 ? 0 : v);
  return out;
}

/** Compact axis numbers: 950, 1.2K, 25K, 1.5M (999,600 is "1M", not "1000K"). */
export function compact(v: number): string {
  const a = Math.abs(v);
  if (a >= 999_500) return String(Number((v / 1e6).toFixed(a >= 1e7 ? 0 : 1))) + "M";
  if (a >= 1e3) return String(Number((v / 1e3).toFixed(a >= 1e4 ? 0 : 1))) + "K";
  return String(Number(v.toFixed(2)));
}
