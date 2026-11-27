// Stored simulation results (src/lib/state.svelte.ts): their key ends with the engine's results version ("#results=2");
// results of the same settings under an older version are shown as outdated, never added to, and give way to the newer
// version's. Each test starts the app's state afresh with the stored results it needs.
import { beforeAll, describe, expect, test } from "vitest";
import type { SimData } from "../src/lib/engine/types";
import { freshApp, storedSim } from "./engine";

const SIMS = "calcbj.sims.v1";

/** The key of the app's default settings, and the same settings as CalcBJ up to 0.1.6 stored them. */
let current: string;
let old: string;

beforeAll(async () => {
  const { app, settingsOf } = await freshApp();
  current = app.simKey;
  old = settingsOf(current);
});

function stored(): { key: string; rounds: number }[] {
  return (JSON.parse(localStorage.getItem(SIMS) ?? "[]") as SimData[]).map((d) => ({ key: d.key, rounds: d.rounds }));
}

describe("results keys", () => {
  test("end with the engine's results version", async () => {
    const { settingsOf, versionOf } = await freshApp();
    expect(current).toMatch(/#spots=3#results=2$/);
    expect(versionOf(current)).toBe(2);
    expect(settingsOf(current)).toBe(current.slice(0, -"#results=2".length));
    expect(versionOf(old)).toBe(0);
    expect(settingsOf(old)).toBe(old);
    expect(versionOf("x#spots=1#results=junk")).toBe(0);
  });
});

describe("after a restart", () => {
  test("results of an older version show as outdated", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(old, 1_000_000)]) });
    expect(app.sim?.key).toBe(old);
    expect(app.simOutdated).toBe(true);
    expect(app.simStale).toBe(true);
  });

  test("this version's results win over an older version's, whatever the rounds", async () => {
    const sims = [storedSim(old, 9_000_000), storedSim(current, 1_000_000)];
    const { app } = await freshApp({ [SIMS]: JSON.stringify(sims) });
    expect(app.sim?.key).toBe(current);
    expect(app.simOutdated).toBe(false);
    expect(app.simStale).toBe(false);
  });

  test("results of other settings are neither current nor outdated", async () => {
    const other = current.replace("#spots=3#", "#spots=2#");
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(other, 1_000_000)]) });
    expect(app.sim).toBeNull();
    expect(app.simOutdated).toBe(false);
  });
});

describe("storing results", () => {
  test("new results replace an older version's, even with fewer rounds", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(old, 5_000_000)]) });
    app.remember(storedSim(current, 1_000_000));
    expect(stored()).toEqual([{ key: current, rounds: 1_000_000 }]);
  });

  test("an older version's results never replace this version's", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(current, 1_000_000)]) });
    app.remember(storedSim(old, 9_000_000));
    expect(stored()).toEqual([{ key: current, rounds: 1_000_000 }]);
  });

  test("an older version's results are kept while nothing newer is held", async () => {
    const { app } = await freshApp();
    app.remember(storedSim(old, 2_000_000));
    expect(stored()).toEqual([{ key: old, rounds: 2_000_000 }]);
  });

  test("the larger run of the same version stays", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(current, 4_000_000)]) });
    app.remember(storedSim(current, 1_000_000));
    expect(stored()).toEqual([{ key: current, rounds: 4_000_000 }]);
  });
});

describe("changing settings", () => {
  test("coming back to settings shows their outdated results again", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(old, 1_000_000)]) });
    app.spots = 2;
    app.showCached(app.simKey);
    // Nothing is held for two spots: the three-spot results stay on screen, for other settings now.
    expect(app.sim?.key).toBe(old);
    expect(app.simOutdated).toBe(false);
    expect(app.simStale).toBe(true);
    app.spots = 3;
    app.showCached(app.simKey);
    expect(app.sim?.key).toBe(old);
    expect(app.simOutdated).toBe(true);
  });

  test("opening a file with an older version's results keeps this version's stored", async () => {
    const { app } = await freshApp({ [SIMS]: JSON.stringify([storedSim(current, 1_000_000)]) });
    const s = app.toScenario();
    s.sim = storedSim(old, 3_000_000);
    app.loadScenario(s, "old.bjev");
    expect(app.sim?.key).toBe(old);
    expect(app.simOutdated).toBe(true);
    expect(stored()).toEqual([{ key: current, rounds: 1_000_000 }]);
  });
});
