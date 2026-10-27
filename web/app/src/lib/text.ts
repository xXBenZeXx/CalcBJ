// Descriptions and small helpers shared by the pages (texts as in the Windows app).
import type { CountSystem, IndexMode, StrategyConfig } from "./engine/types";

export const RANK_NAMES = ["", "A", "2", "3", "4", "5", "6", "7", "8", "9", "T"];
/** Dealer upcards in chart order: 2..9, T, A. */
export const CHART_UPCARDS = [2, 3, 4, 5, 6, 7, 8, 9, 10, 1];

export const INDEX_MODES: { value: IndexMode; label: string }[] = [
  { value: "BasicOnly", label: "Basic strategy only" },
  { value: "Illustrious18", label: "Illustrious 18" },
  { value: "I18Fab4", label: "Illustrious 18 + Fab 4" },
  { value: "FullIndices", label: "Full index set" },
];

export function hardCell(total: number, up: number): number {
  return total * 10 + (up - 1);
}

export function softCell(total: number, up: number): number {
  return 220 + total * 10 + (up - 1);
}

export function pairCell(rank: number, up: number): number {
  return 440 + (rank - 1) * 10 + (up - 1);
}

export function rankTag(cs: CountSystem, rank: number): number {
  const i = rank - 1;
  return cs.redTags ? 0.5 * (cs.tags[i] + cs.redTags[i]) : cs.tags[i];
}

/** Sum of tags over one 52-card deck. */
export function imbalancePerDeck(cs: CountSystem): number {
  let sum = 0;
  for (let r = 1; r <= 10; r++) sum += rankTag(cs, r) * (r === 10 ? 16 : 4);
  return sum;
}

export function isBalanced(cs: CountSystem): boolean {
  return Math.abs(imbalancePerDeck(cs)) < 1e-9;
}

export function countText(cs: CountSystem): string {
  let s = cs.name + " (" + (cs.metric === "TrueCount" ? "TC" : "RC");
  if (cs.metric === "TrueCount") {
    s += { Exact: ", exact decks", QuarterDeck: ", ¼-deck", HalfDeck: ", ½-deck", FullDeck: ", full-deck" }[cs.deckEst];
    s += ", " + cs.rounding.toLowerCase();
  }
  return s + ")";
}

export function strategyText(sc: StrategyConfig): string {
  const names: Record<IndexMode, string> = {
    BasicOnly: "Basic strategy",
    Illustrious18: "Illustrious 18",
    I18Fab4: "I18 + Fab 4",
    FullIndices: `Full indices ${sc.indexMin}..${sc.indexMax}`,
  };
  const custom = sc.overrides.filter((o) => o.enabled).length;
  return names[sc.mode] + (custom > 0 ? ` + ${custom} custom` : "");
}

export function metricPrefix(cs: CountSystem): string {
  return cs.metric === "TrueCount" ? "TC " : "RC ";
}
