// .bjev scenario files in the format the Windows app writes (XmlSerializer of its ScenarioFile class), so a file
// saved in either version opens in the other.
import type { BetRamp, CountSystem, IndexOverride, Rules, SimData, StrategyConfig } from "./engine/types";

/** A snapshot on the Compare page (C#: CompareEntry). Infinite values (N0 of a losing game) stay Infinity. */
export interface CompareEntry {
  name: string;
  rules: string;
  count: string;
  strategy: string;
  ramp: string;
  spots: number;
  unit: number;
  roundsPerHour: number;
  bankroll: number;
  winPerHour: number;
  sdPerHour: number;
  winPer100: number;
  sdPer100: number;
  edgePct: number;
  score: number;
  di: number;
  n0: number;
  hoursN0: number;
  ror: number;
  avgBet: number;
  spread: number;
  playedPct: number;
  cePerHour: number;
  houseEdge: number;
  bankroll5: number;
  kellyUnit: number;
  rounds: number;
  stale: boolean;
}

export interface Scenario {
  rules: Rules;
  count: CountSystem;
  strategy: StrategyConfig;
  spots: number;
  roundsPerHour: number;
  autoRoundsPerHour: boolean;
  unit: number;
  bankroll: number;
  ramp: BetRamp | null;
  rampPreset: number;
  simRounds: number;
  threads: number;
  tripHours: number;
  tripBankroll: number;
  sim: SimData | null;
  compare: CompareEntry[];
}

// ---------------- writing ----------------

/** xsd:double as .NET writes it: INF, -INF and NaN for the special values. */
function num(v: number): string {
  if (Number.isNaN(v)) return "NaN";
  if (v === Infinity) return "INF";
  if (v === -Infinity) return "-INF";
  return String(v);
}

function escape(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/** Local time with its UTC offset, e.g. 2026-10-27T17:07:42.406+01:00 (xsd:dateTime, as XmlSerializer reads it). */
function dateTime(ms: number): string {
  const d = new Date(ms > 0 ? ms : Date.now());
  const p = (n: number, w = 2) => String(Math.trunc(Math.abs(n))).padStart(w, "0");
  const off = -d.getTimezoneOffset();
  return (
    `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:` +
    `${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}${off >= 0 ? "+" : "-"}${p(off / 60)}:${p(off % 60)}`
  );
}

class XmlWriter {
  private readonly lines: string[] = ['<?xml version="1.0" encoding="utf-8"?>'];
  private depth = 0;

  open(tag: string, attributes = ""): void {
    this.lines.push(`${"  ".repeat(this.depth)}<${tag}${attributes}>`);
    this.depth++;
  }

  close(tag: string): void {
    this.depth--;
    this.lines.push(`${"  ".repeat(this.depth)}</${tag}>`);
  }

  value(tag: string, v: string | number | boolean): void {
    const text = typeof v === "number" ? num(v) : typeof v === "boolean" ? String(v) : escape(v);
    this.lines.push(`${"  ".repeat(this.depth)}<${tag}>${text}</${tag}>`);
  }

  /** A list as XmlSerializer writes arrays: one element per item, named after the item type. */
  list(tag: string, item: string, values: number[]): void {
    this.open(tag);
    for (const v of values) this.value(item, item === "double" ? v : Math.round(v));
    this.close(tag);
  }

  toString(): string {
    return this.lines.join("\r\n");
  }
}

export function writeScenario(s: Scenario): string {
  const w = new XmlWriter();
  w.open("ScenarioFile", ' xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"');
  w.value("Version", "1");

  const r = s.rules;
  w.open("Rules");
  w.value("Decks", r.decks);
  w.value("H17", r.h17);
  w.value("HoleCard", r.holeCard);
  w.value("BlackjackPays", r.blackjackPays);
  w.value("DoubleOn", r.doubleOn);
  w.value("DAS", r.das);
  w.value("MaxSplitHands", r.maxSplitHands);
  w.value("ResplitAces", r.resplitAces);
  w.value("HitSplitAces", r.hitSplitAces);
  w.value("Surrender", r.surrender);
  w.value("Insurance", r.insurance);
  w.value("Penetration", r.penetration);
  w.value("BurnCard", r.burnCard);
  w.close("Rules");

  const c = s.count;
  w.open("Count");
  w.value("Name", c.name);
  w.list("Tags", "double", c.tags);
  if (c.redTags) w.list("RedTags", "double", c.redTags);
  w.value("IrcBase", c.ircBase);
  w.value("IrcPerDeck", c.ircPerDeck);
  w.value("Metric", c.metric);
  w.value("DeckEst", c.deckEst);
  w.value("Rounding", c.rounding);
  w.close("Count");

  w.open("Strategy");
  w.value("Mode", s.strategy.mode);
  w.value("IndexMin", s.strategy.indexMin);
  w.value("IndexMax", s.strategy.indexMax);
  w.open("Overrides");
  for (const o of s.strategy.overrides) {
    w.open("IndexOverride");
    w.value("Enabled", o.enabled);
    w.value("Kind", o.kind);
    w.value("Total", o.total);
    w.value("Upcard", o.upcard);
    w.value("Play", o.play);
    w.value("AtOrAbove", o.atOrAbove);
    w.value("Index", o.index);
    w.close("IndexOverride");
  }
  w.close("Overrides");
  w.close("Strategy");

  w.value("Spots", s.spots);
  w.value("RoundsPerHour", s.roundsPerHour);
  w.value("AutoRoundsPerHour", s.autoRoundsPerHour);
  w.value("Unit", s.unit);
  w.value("Bankroll", s.bankroll);
  if (s.ramp) {
    w.open("Ramp");
    w.value("Min", s.ramp.min);
    w.value("Max", s.ramp.max);
    w.list("Units", "double", s.ramp.units);
    w.list("Hands", "int", s.ramp.hands);
    w.close("Ramp");
  }
  w.value("RampPreset", s.rampPreset);
  w.value("SimRounds", s.simRounds);
  w.value("Threads", s.threads);
  w.value("AutoRun", false);
  w.value("TripHours", s.tripHours);
  w.value("TripBankroll", s.tripBankroll);

  const d = s.sim;
  if (d) {
    w.open("Sim");
    w.value("BinMin", d.binMin);
    w.value("NumBins", d.numBins);
    w.value("Spots", d.spots);
    w.list("N", "long", d.n);
    w.list("SA", "long", d.sa);
    w.list("QA", "long", d.qa);
    w.list("SB", "long", d.sb);
    w.list("QB", "long", d.qb);
    w.list("AB", "long", d.ab);
    w.value("Rounds", Math.round(d.rounds));
    w.value("Shoes", Math.round(d.shoes));
    w.value("Emergency", Math.round(d.emergency));
    w.value("Seconds", d.seconds);
    w.value("Threads", d.threads);
    w.value("Seed", 0);
    w.value("Key", d.key);
    w.value("RulesText", d.rulesText);
    w.value("CountText", d.countText);
    w.value("StrategyText", d.strategyText);
    w.value("Metric", d.metric);
    w.value("BasicEV", d.basicEv);
    w.value("Created", dateTime(d.created));
    w.close("Sim");
  }

  w.open("Compare");
  for (const e of s.compare) {
    w.open("CompareEntry");
    w.value("Name", e.name);
    w.value("Rules", e.rules);
    w.value("Count", e.count);
    w.value("Strategy", e.strategy);
    w.value("Ramp", e.ramp);
    w.value("Spots", e.spots);
    for (const [tag, v] of COMPARE_NUMBERS) w.value(tag, e[v]);
    w.value("Rounds", Math.round(e.rounds));
    w.value("Stale", e.stale);
    w.close("CompareEntry");
  }
  w.close("Compare");
  w.close("ScenarioFile");
  return w.toString();
}

/** The double fields of a compare entry, in the order the Windows app writes them. */
const COMPARE_NUMBERS: [string, keyof CompareEntry & string][] = [
  ["Unit", "unit"],
  ["RoundsPerHour", "roundsPerHour"],
  ["Bankroll", "bankroll"],
  ["WinPerHour", "winPerHour"],
  ["SdPerHour", "sdPerHour"],
  ["WinPer100", "winPer100"],
  ["SdPer100", "sdPer100"],
  ["EdgePct", "edgePct"],
  ["Score", "score"],
  ["DI", "di"],
  ["N0", "n0"],
  ["HoursN0", "hoursN0"],
  ["ROR", "ror"],
  ["AvgBet", "avgBet"],
  ["Spread", "spread"],
  ["PlayedPct", "playedPct"],
  ["CEPerHour", "cePerHour"],
  ["HouseEdge", "houseEdge"],
  ["Bankroll5", "bankroll5"],
  ["KellyUnit", "kellyUnit"],
];

// ---------------- reading ----------------

function child(el: Element | null, name: string): Element | null {
  if (!el) return null;
  for (const c of Array.from(el.children)) if (c.localName === name) return c;
  return null;
}

function text(el: Element | null, name: string): string | null {
  const c = child(el, name);
  return c ? (c.textContent ?? "").trim() : null;
}

function readNumber(s: string | null): number | null {
  if (s === null || s === "") return null;
  if (s === "INF") return Infinity;
  if (s === "-INF") return -Infinity;
  if (s === "NaN") return NaN;
  const v = Number(s);
  return Number.isNaN(v) ? null : v;
}

function numberOr(el: Element | null, name: string, fallback: number): number {
  return readNumber(text(el, name)) ?? fallback;
}

function boolOr(el: Element | null, name: string, fallback: boolean): boolean {
  const t = text(el, name);
  return t === "true" || t === "1" ? true : t === "false" || t === "0" ? false : fallback;
}

function stringOr<T extends string>(el: Element | null, name: string, fallback: T): T {
  const t = text(el, name);
  return t === null ? fallback : (t as T);
}

function numbers(el: Element | null, name: string): number[] | null {
  const c = child(el, name);
  if (!c) return null;
  return Array.from(c.children).map((e) => readNumber((e.textContent ?? "").trim()) ?? 0);
}

/** Parses a .bjev file. Missing fields come back as null or from `defaults`; values are checked when applied. */
export function readScenario(xml: string, defaults: { rules: Rules; count: CountSystem; strategy: StrategyConfig }): Scenario {
  const doc = new DOMParser().parseFromString(xml, "application/xml");
  const root = doc.documentElement;
  if (doc.getElementsByTagName("parsererror").length > 0 || root.localName !== "ScenarioFile")
    throw new Error("This is not a CalcBJ scenario file.");

  const r = child(root, "Rules");
  const d = defaults.rules;
  const rules: Rules = {
    decks: numberOr(r, "Decks", d.decks),
    h17: boolOr(r, "H17", d.h17),
    holeCard: stringOr(r, "HoleCard", d.holeCard),
    blackjackPays: numberOr(r, "BlackjackPays", d.blackjackPays),
    doubleOn: stringOr(r, "DoubleOn", d.doubleOn),
    das: boolOr(r, "DAS", d.das),
    maxSplitHands: numberOr(r, "MaxSplitHands", d.maxSplitHands),
    resplitAces: boolOr(r, "ResplitAces", d.resplitAces),
    hitSplitAces: boolOr(r, "HitSplitAces", d.hitSplitAces),
    surrender: stringOr(r, "Surrender", d.surrender),
    insurance: boolOr(r, "Insurance", d.insurance),
    penetration: numberOr(r, "Penetration", d.penetration),
    burnCard: boolOr(r, "BurnCard", d.burnCard),
  };

  const c = child(root, "Count");
  const tags = numbers(c, "Tags");
  const redTags = numbers(c, "RedTags");
  const count: CountSystem =
    c && tags?.length === 10
      ? {
          name: text(c, "Name") ?? "Custom",
          tags,
          redTags: redTags?.length === 10 ? redTags : null,
          ircBase: numberOr(c, "IrcBase", 0),
          ircPerDeck: numberOr(c, "IrcPerDeck", 0),
          metric: stringOr(c, "Metric", "TrueCount"),
          deckEst: stringOr(c, "DeckEst", "HalfDeck"),
          rounding: stringOr(c, "Rounding", "Floor"),
        }
      : defaults.count;

  const st = child(root, "Strategy");
  const overrides: IndexOverride[] = Array.from(child(st, "Overrides")?.children ?? []).map((o) => ({
    enabled: boolOr(o, "Enabled", true),
    kind: stringOr(o, "Kind", "Hard"),
    total: numberOr(o, "Total", 16),
    upcard: numberOr(o, "Upcard", 10),
    play: stringOr(o, "Play", "Stand"),
    atOrAbove: boolOr(o, "AtOrAbove", true),
    index: numberOr(o, "Index", 0),
  }));
  const strategy: StrategyConfig = st
    ? {
        mode: stringOr(st, "Mode", defaults.strategy.mode),
        indexMin: numberOr(st, "IndexMin", defaults.strategy.indexMin),
        indexMax: numberOr(st, "IndexMax", defaults.strategy.indexMax),
        overrides,
      }
    : defaults.strategy;

  const rp = child(root, "Ramp");
  const units = numbers(rp, "Units");
  const hands = numbers(rp, "Hands");
  const ramp: BetRamp | null =
    rp && units && hands ? { min: numberOr(rp, "Min", 0), max: numberOr(rp, "Max", 0), units, hands } : null;

  const sd = child(root, "Sim");
  let sim: SimData | null = null;
  if (sd) {
    const arrays = ["N", "SA", "QA", "SB", "QB", "AB"].map((n) => numbers(sd, n) ?? []);
    const created = Date.parse((text(sd, "Created") ?? "").replace(/(\.\d{3})\d+/, "$1"));
    sim = {
      binMin: numberOr(sd, "BinMin", -100),
      numBins: numberOr(sd, "NumBins", 201),
      spots: numberOr(sd, "Spots", 1),
      n: arrays[0],
      sa: arrays[1],
      qa: arrays[2],
      sb: arrays[3],
      qb: arrays[4],
      ab: arrays[5],
      rounds: numberOr(sd, "Rounds", 0),
      shoes: numberOr(sd, "Shoes", 0),
      emergency: numberOr(sd, "Emergency", 0),
      seconds: numberOr(sd, "Seconds", 0),
      threads: numberOr(sd, "Threads", 0),
      key: text(sd, "Key") ?? "",
      rulesText: text(sd, "RulesText") ?? "",
      countText: text(sd, "CountText") ?? "",
      strategyText: text(sd, "StrategyText") ?? "",
      metric: stringOr(sd, "Metric", "TrueCount"),
      basicEv: numberOr(sd, "BasicEV", 0),
      created: Number.isNaN(created) ? Date.now() : created,
    };
  }

  const compare: CompareEntry[] = Array.from(child(root, "Compare")?.children ?? []).map((e) => {
    const entry = {
      name: text(e, "Name") ?? "",
      rules: text(e, "Rules") ?? "",
      count: text(e, "Count") ?? "",
      strategy: text(e, "Strategy") ?? "",
      ramp: text(e, "Ramp") ?? "",
      spots: numberOr(e, "Spots", 1),
      rounds: numberOr(e, "Rounds", 0),
      stale: boolOr(e, "Stale", false),
    } as CompareEntry;
    for (const [tag, key] of COMPARE_NUMBERS) (entry as unknown as Record<string, number>)[key] = numberOr(e, tag, 0);
    return entry;
  });

  return {
    rules,
    count,
    strategy,
    spots: numberOr(root, "Spots", 3),
    roundsPerHour: numberOr(root, "RoundsPerHour", 0),
    autoRoundsPerHour: boolOr(root, "AutoRoundsPerHour", true),
    unit: numberOr(root, "Unit", 0),
    bankroll: numberOr(root, "Bankroll", 0),
    ramp,
    rampPreset: numberOr(root, "RampPreset", 3),
    simRounds: numberOr(root, "SimRounds", 0),
    threads: numberOr(root, "Threads", 0),
    tripHours: numberOr(root, "TripHours", 0),
    tripBankroll: numberOr(root, "TripBankroll", 0),
    sim,
    compare,
  };
}
