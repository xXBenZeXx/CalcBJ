// JSON shapes of the engine's requests and replies (web/engine-wasm). Enum values are the C# names.

export type HoleCardRule = "USPeek" | "ENHC" | "ENHC_OBO";
export type DoubleRule = "AnyTwo" | "NineToEleven" | "TenToEleven";
export type SurrenderRule = "None" | "Late" | "Early";
export type DeckEstimation = "Exact" | "QuarterDeck" | "HalfDeck" | "FullDeck";
export type TcRounding = "Floor" | "Truncate" | "Round";
export type CountMetric = "TrueCount" | "RunningCount";
export type IndexMode = "BasicOnly" | "Illustrious18" | "I18Fab4" | "FullIndices";
export type HandKind = "Hard" | "Soft" | "Pair" | "Insurance";
export type PlayKind =
  | "Stand"
  | "Hit"
  | "Double"
  | "NoDouble"
  | "Split"
  | "NoSplit"
  | "Surrender"
  | "NoSurrender"
  | "Insure"
  | "NoInsure";

export interface Rules {
  decks: number;
  h17: boolean;
  holeCard: HoleCardRule;
  blackjackPays: number;
  doubleOn: DoubleRule;
  das: boolean;
  maxSplitHands: number;
  resplitAces: boolean;
  hitSplitAces: boolean;
  surrender: SurrenderRule;
  insurance: boolean;
  /** Fraction of the shoe dealt before the shuffle. */
  penetration: number;
  burnCard: boolean;
}

export interface CountSystem {
  name: string;
  /** Tags for A, 2..9, T. */
  tags: number[];
  /** Tags for red cards when they differ (e.g. Red Seven). */
  redTags: number[] | null;
  ircBase: number;
  ircPerDeck: number;
  metric: CountMetric;
  deckEst: DeckEstimation;
  rounding: TcRounding;
}

export interface IndexOverride {
  enabled: boolean;
  kind: HandKind;
  /** Hard/soft total, or pair rank 1..10. */
  total: number;
  /** 1..10, 1 = ace. */
  upcard: number;
  play: PlayKind;
  atOrAbove: boolean;
  index: number;
}

export interface StrategyConfig {
  mode: IndexMode;
  indexMin: number;
  indexMax: number;
  overrides: IndexOverride[];
}

/** Row 0 covers every count at or below `min`, the last row every count at or above `max`. */
export interface BetRamp {
  min: number;
  max: number;
  units: number[];
  hands: number[];
}

export interface OptimizerSettings {
  minUnits: number;
  maxUnits: number;
  increment: number;
  maxHands: number;
  allowWongOut: boolean;
  kelly: boolean;
  kellyFraction: number;
  bankrollUnits: number;
}

/** Per-count tallies of a simulation, in twentieths of a unit (sums and squares of the last two seats). */
export interface SimData {
  binMin: number;
  numBins: number;
  spots: number;
  n: number[];
  sa: number[];
  qa: number[];
  sb: number[];
  qb: number[];
  ab: number[];
  rounds: number;
  shoes: number;
  emergency: number;
  seconds: number;
  threads: number;
  key: string;
  rulesText: string;
  countText: string;
  strategyText: string;
  metric: CountMetric;
  basicEv: number;
  /** Milliseconds since 1970. */
  created: number;
}

export interface BinStat {
  value: number;
  n: number;
  freq: number;
  mean: number;
  m2: number;
  cov: number;
  measuredCov: boolean;
  meanVar: number;
}

/** Non-finite values (N0 of a break-even ramp) arrive as null. */
export interface AnalysisResult {
  valid: boolean;
  ev: number;
  var: number;
  sd: number;
  m2: number;
  avgBet: number;
  playedFrac: number;
  avgBetPlayed: number;
  edgePct: number;
  di: number;
  score: number;
  n0: number | null;
  seEv: number;
  winPerHour: number;
  sdPerHour: number;
  winPer100: number;
  sdPer100: number;
  seWinPerHour: number;
  roundsPerHour: number;
  unit: number;
  bankroll: number;
  ror: number;
  cePerHour: number;
  kellyUnit: number;
  hoursN0: number | null;
  spread: number;
  usedAssumedCov: boolean;
  rowFreq: number[];
  rowEv: number[];
  rowContribution: number[];
  rowM2: number[];
  rowN: number[];
}

export interface Evaluation {
  result: AnalysisResult;
  /** Bankroll for 5%, 13.5% and 1% risk of ruin (null when the ramp loses). */
  bankrollFor: (number | null)[];
  /** Per ramp row: frequency, mean, second moment and covariance. */
  rows: BinStat[];
}

export interface RulesInfo {
  describe: string;
  key: string;
  totalCards: number;
  cutCard: number;
  midDepthDecks: number;
}

export interface CountInfo {
  scale: number;
  imbalance: number;
  balanced: boolean;
  level: number;
  irc: number;
  tagText: string;
  key: string;
  /** This count's true-count units per Hi-Lo true count. */
  factor: number;
  bettingCorrelation: number;
  insuranceCorrelation: number;
}

export interface IndexInfo {
  kind: HandKind;
  total: number;
  upcard: number;
  hand: string;
  play: string;
  condition: string;
  threshold: number;
  atOrAbove: boolean;
  irregular: boolean;
  text: string;
}

export interface StrategyInfo {
  key: string;
  basicEv: number;
  indices: IndexInfo[];
}

export interface ChartData {
  /** Chart code (H, S, D, Ds, P, R, Rs, Rp; "" for pairs not split) of every strategy cell. */
  codes: string[];
  /** EV of stand, hit, double, surrender and split per cell (5 per cell, null where not possible). */
  cellEv: (number | null)[];
}

export interface RampResult {
  ramp: BetRamp;
  preset: number;
}
