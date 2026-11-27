// .bjev scenario files (src/lib/scenario.ts): files saved by the former Windows app still open, and what the app writes
// reads back the same.
import { readFileSync } from "node:fs";
import { beforeAll, describe, expect, test } from "vitest";
import { readScenario, writeScenario } from "../src/lib/scenario";
import { appFile, freshApp, storedSim } from "./engine";

type State = Awaited<ReturnType<typeof freshApp>>;
let state: State;
let defaults: Parameters<typeof readScenario>[1];

beforeAll(async () => {
  state = await freshApp();
  defaults = { rules: state.defaultRules(), count: structuredClone(state.presets[0]), strategy: state.defaultStrategy() };
});

/** Saved by the Windows app (CRLF line ends, .NET's number format). */
const sample = readFileSync(appFile("tests/fixtures/windows-sample.bjev"), "utf8");

describe("a file from the Windows app", () => {
  test("opens with its settings, results and Compare entries", () => {
    const s = readScenario(sample, defaults);
    expect(s.rules.decks).toBe(2);
    expect(s.rules.h17).toBe(false);
    expect(s.count.name).toBe("Red Seven");
    expect(s.spots).toBe(2);
    expect(s.compare).toHaveLength(1);
    expect(s.sim?.rounds).toBe(500_000);
    // Stored before results had a version: the app shows them as an older version's.
    expect(s.sim?.key).not.toContain("#results=");
    expect(state.versionOf(s.sim?.key ?? "")).toBe(0);
  });

  test("writes back the same, apart from the seed, the timestamp's precision and how numbers are spelled", () => {
    const back = writeScenario(readScenario(sample, defaults)).split("\r\n");
    const lines = sample.split("\r\n");
    expect(back).toHaveLength(lines.length);
    const differences: string[] = [];
    lines.forEach((line, i) => {
      if (line === back[i]) return;
      const a = /^\s*<(\w+)>(.*)<\/\1>$/.exec(line);
      const b = /^\s*<(\w+)>(.*)<\/\1>$/.exec(back[i]);
      const sameNumber = a && b && a[1] === b[1] && a[2] !== "" && Number(a[2]) === Number(b[2]);
      const allowed = a && b && a[1] === b[1] && (a[1] === "Seed" || a[1] === "Created");
      if (!sameNumber && !allowed) differences.push(`line ${i + 1}: ${line.trim()} → ${back[i].trim()}`);
    });
    expect(differences).toEqual([]);
  });

  test("keeps its CRLF line ends", () => {
    expect(writeScenario(readScenario(sample, defaults))).toContain("\r\n");
  });
});

describe("files the app writes", () => {
  test("read back the same", () => {
    const s = state.app.toScenario();
    s.sim = storedSim(state.app.simKey, 1_000_000);
    s.sim.n[100] = 7;
    s.sim.sa[100] = -140;
    const back = readScenario(writeScenario(s), defaults);
    expect(back.rules).toEqual(s.rules);
    expect(back.count).toEqual(s.count);
    expect(back.strategy).toEqual(s.strategy);
    expect(back.spots).toBe(s.spots);
    expect(back.ramp).toEqual(s.ramp);
    expect(back.sim?.key).toBe(s.sim.key);
    expect(back.sim?.rounds).toBe(1_000_000);
    expect(back.sim?.n[100]).toBe(7);
    expect(back.sim?.sa[100]).toBe(-140);
  });

  test("anything else is refused with a readable message", () => {
    expect(() => readScenario("<nope", defaults)).toThrow("This is not a CalcBJ scenario file.");
    expect(() => readScenario("<Other/>", defaults)).toThrow("This is not a CalcBJ scenario file.");
  });
});
