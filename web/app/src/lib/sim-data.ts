import type { BetRamp, SimData } from "./engine/types";

function add(a: number[], b: number[]): number[] {
  return b.map((v, i) => v + (a[i] ?? 0));
}

/** Combines two runs of the same settings (their tallies add up); labels and metadata come from `b`. */
export function mergeSim(a: SimData | null, b: SimData): SimData {
  if (!a) return b;
  return {
    ...b,
    n: add(a.n, b.n),
    sa: add(a.sa, b.sa),
    qa: add(a.qa, b.qa),
    sb: add(a.sb, b.sb),
    qb: add(a.qb, b.qb),
    ab: add(a.ab, b.ab),
    rounds: a.rounds + b.rounds,
    shoes: a.shoes + b.shoes,
    emergency: a.emergency + b.emergency,
    seconds: a.seconds + b.seconds,
  };
}

export function rampRow(r: BetRamp, v: number): number {
  return Math.max(r.min, Math.min(r.max, v)) - r.min;
}

/** The ramp over new rows; each row takes the bet the old ramp had for that count. */
export function resizeRamp(r: BetRamp, min: number, max: number): BetRamp {
  const units: number[] = [];
  const hands: number[] = [];
  for (let v = min; v <= max; v++) {
    units.push(r.units[rampRow(r, v)]);
    hands.push(r.hands[rampRow(r, v)]);
  }
  return { min, max, units, hands };
}

export function flatRamp(): BetRamp {
  return { min: 0, max: 0, units: [1], hands: [1] };
}
