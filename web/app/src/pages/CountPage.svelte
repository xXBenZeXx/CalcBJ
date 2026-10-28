<script lang="ts">
  import Card from "../lib/components/Card.svelte";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import StrategyChart from "../lib/components/StrategyChart.svelte";
  import { engine } from "../lib/engine/engine";
  import type { CountSystem, HandKind, IndexOverride, PlayKind, StrategyInfo } from "../lib/engine/types";
  import { parseNumber, signed, signedTrim, snapTag, trim } from "../lib/format";
  import { app, presets } from "../lib/state.svelte";
  import { INDEX_MODES, RANK_NAMES, isBalanced, metricPrefix } from "../lib/text";

  const RANKS = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "T"];
  const UPCARDS = ["2", "3", "4", "5", "6", "7", "8", "9", "T", "A"];
  const PLAYS: [PlayKind, string][] = [
    ["Stand", "Stand"],
    ["Hit", "Hit"],
    ["Double", "Double"],
    ["NoDouble", "No double"],
    ["Split", "Split"],
    ["NoSplit", "No split"],
    ["Surrender", "Surrender"],
    ["NoSurrender", "No surrender"],
    ["Insure", "Insure"],
  ];
  const presetKeys = presets.map((p) => engine.countKey(p));

  let presetIndex = $derived(presets.findIndex((p) => p.name === app.count.name));
  let info = $derived(engine.countInfo(app.rules, app.count));

  // ---------------- counting system ----------------

  function chooseSystem(i: number) {
    if (i < 0) return;
    const p = structuredClone(presets[i]);
    p.deckEst = app.count.deckEst;
    p.rounding = app.count.rounding;
    app.count = p;
    app.resetRanges();
  }

  /** New tags: the system takes the name of a matching preset, otherwise it becomes "Custom". */
  function replaceCount(cs: CountSystem) {
    const key = engine.countKey(cs);
    const same = key === engine.countKey(app.count);
    const match = presetKeys.indexOf(key);
    cs.name = match >= 0 ? presets[match].name : same ? app.count.name : "Custom";
    app.count = cs;
    if (!same) app.resetRanges();
  }

  function setTag(i: number, red: boolean, v: number) {
    const cs = $state.snapshot(app.count);
    if (red) {
      cs.redTags = cs.redTags ?? [...cs.tags];
      cs.redTags[i] = snapTag(v);
    } else {
      cs.tags[i] = snapTag(v);
    }
    replaceCount(cs);
  }

  function setRedTags(on: boolean) {
    const cs = $state.snapshot(app.count);
    cs.redTags = on ? (cs.redTags ?? [...cs.tags]) : null;
    replaceCount(cs);
  }

  /** True-count settings; a different metric (or an unbalanced count) moves the ramp rows and index range. */
  function setConversion(change: Partial<CountSystem>) {
    const oldMetric = app.count.metric;
    Object.assign(app.count, change);
    if (app.count.metric !== oldMetric || app.count.metric === "RunningCount" || !isBalanced(app.count)) {
      app.resetRanges();
    }
  }

  let metricNote = $derived.by(() => {
    if (app.count.metric === "RunningCount")
      return "Bets and index plays key off the running count itself; true-count settings are ignored. Typical for unbalanced counts such as KO and Red Seven.";
    if (!info.balanced)
      return "This count is unbalanced: the true count is the running count (including the IRC) divided by decks remaining. Set the IRC to suit.";
    const decks = {
      Exact: "exact",
      QuarterDeck: "to the nearest ¼ deck",
      HalfDeck: "to the nearest ½ deck",
      FullDeck: "to the nearest deck",
    }[app.count.deckEst];
    return `True count = running count ÷ estimated decks remaining (unseen cards, ${decks}), then ${app.count.rounding.toLowerCase()}ed to a whole number.`;
  });

  // ---------------- custom index plays ----------------

  interface Row {
    on: boolean;
    kind: HandKind;
    total: string;
    up: string;
    play: PlayKind;
    when: "≥" | "≤";
    index: string;
  }

  function toRow(o: IndexOverride): Row {
    return {
      on: o.enabled,
      kind: o.kind,
      total: o.kind === "Pair" ? (RANK_NAMES[o.total] ?? "") : o.kind === "Insurance" ? "" : String(o.total),
      up: o.upcard === 1 ? "A" : o.upcard === 10 ? "T" : String(o.upcard),
      play: o.play === "NoInsure" ? "Insure" : o.play,
      when: o.atOrAbove ? "≥" : "≤",
      index: signedTrim(o.index),
    };
  }

  function parseRank(text: string): number {
    const s = text.trim().toUpperCase();
    if (["A", "1", "11"].includes(s)) return 1;
    if (["T", "10", "J", "Q", "K"].includes(s)) return 10;
    const v = Number(s);
    return Number.isInteger(v) && v >= 2 && v <= 9 ? v : -1;
  }

  /** The override a row describes, or null while it is incomplete. */
  function toOverride(r: Row): IndexOverride | null {
    const o: IndexOverride = {
      enabled: r.on,
      kind: r.kind,
      total: 0,
      upcard: parseRank(r.up),
      play: r.play,
      atOrAbove: r.when !== "≤",
      index: parseNumber(r.index) ?? 0,
    };
    if (r.kind === "Pair") o.total = parseRank(r.total.split(",")[0]);
    else if (r.kind === "Soft") {
      const v = parseInt(r.total.trim().toUpperCase().replace(/A/g, "").replace(/,/g, ""), 10);
      if (!Number.isNaN(v)) o.total = v <= 10 ? v + 11 : v;
    } else if (r.kind === "Hard") {
      const v = parseInt(r.total.trim(), 10);
      if (!Number.isNaN(v)) o.total = v;
    }
    if (r.kind === "Insurance") {
      o.play = "Insure";
      o.upcard = 1;
    }
    const valid =
      r.kind === "Insurance" ||
      (o.upcard >= 1 &&
        ((r.kind === "Pair" && o.total >= 1) ||
          (r.kind === "Soft" && o.total >= 12 && o.total <= 21) ||
          (r.kind === "Hard" && o.total >= 4 && o.total <= 21)));
    return valid ? o : null;
  }

  let rows = $state<Row[]>(app.strategy.overrides.map(toRow));

  function saveRows() {
    app.strategy.overrides = rows.map(toOverride).filter((o): o is IndexOverride => o !== null);
  }

  function addRow(o: Partial<IndexOverride> = {}) {
    const base: IndexOverride = {
      enabled: true,
      kind: "Hard",
      total: 16,
      upcard: 10,
      play: "Stand",
      atOrAbove: true,
      index: 0,
    };
    rows.push(toRow({ ...base, ...o }));
  }

  /** Published Hi-Lo multi-deck values (Illustrious 18 and Fab 4), S17 unless changed below for H17. */
  function addPublished() {
    const published: [HandKind, number, number, PlayKind, number][] = [
      ["Insurance", 0, 1, "Insure", 3],
      ["Hard", 16, 10, "Stand", 0],
      ["Hard", 15, 10, "Stand", 4],
      ["Pair", 10, 5, "Split", 5],
      ["Pair", 10, 6, "Split", 4],
      ["Hard", 10, 10, "Double", 4],
      ["Hard", 12, 3, "Stand", 2],
      ["Hard", 12, 2, "Stand", 3],
      ["Hard", 11, 1, "Double", 1],
      ["Hard", 9, 2, "Double", 1],
      ["Hard", 10, 1, "Double", 4],
      ["Hard", 9, 7, "Double", 3],
      ["Hard", 16, 9, "Stand", 5],
      ["Hard", 13, 2, "Stand", -1],
      ["Hard", 12, 4, "Stand", 0],
      ["Hard", 12, 5, "Stand", -2],
      ["Hard", 12, 6, "Stand", -1],
      ["Hard", 13, 3, "Stand", -2],
      ["Hard", 14, 10, "Surrender", 3],
      ["Hard", 15, 10, "Surrender", 0],
      ["Hard", 15, 9, "Surrender", 2],
      ["Hard", 15, 1, "Surrender", 1],
    ];
    for (const [kind, total, upcard, play, index] of published) {
      let i = index;
      if (app.rules.h17 && kind === "Hard" && upcard === 1) {
        // H17: 11 v A doubles from -1, 10 v A from +3, and 15 v A surrenders from -1.
        if (total === 11 && play === "Double") i = -1;
        else if (total === 10 && play === "Double") i = 3;
        else if (total === 15 && play === "Surrender") i = -1;
      }
      addRow({ kind, total, upcard, play, index: i });
    }
    saveRows();
  }

  // ---------------- chart and index list ----------------

  let viewAt = $state<number | null>(null);
  let showIndices = $state(true);
  let selected = $state("");

  let range = $derived.by(() => {
    const values: number[] = [];
    for (let v = app.strategy.indexMin; v <= app.strategy.indexMax; v++) values.push(v);
    return values;
  });
  let at = $derived(viewAt !== null && range.includes(viewAt) ? viewAt : null);
  let atLabel = $derived(at === null ? null : metricPrefix(app.count) + signed(at));

  let strategy = $derived.by((): StrategyInfo | string => {
    try {
      return engine.strategy(app.rules, app.count, app.strategy);
    } catch (e) {
      return e instanceof Error ? e.message : String(e);
    }
  });
  let chart = $derived(typeof strategy === "string" ? null : engine.chart(app.rules, app.count, app.strategy, at));
  let indices = $derived(typeof strategy === "string" ? [] : strategy.indices);
  let listed = $derived(indices.filter((i) => i.condition !== "always"));
</script>

<div class="cols">
  <div class="col">
    <Card title="Counting system">
      <div class="fields">
        <Field label="System">
          <select value={presetIndex} onchange={(e) => chooseSystem(Number(e.currentTarget.value))}>
            {#each presets as p, i (p.name)}<option value={i}>{p.name}</option>{/each}
            <option value={-1}>Custom</option>
          </select>
        </Field>
      </div>
      <div class="scroll-x">
        <table class="table tags">
          <thead>
            <tr>
              <th>Card</th>
              {#each RANKS as r (r)}<th>{r}</th>{/each}
            </tr>
          </thead>
          <tbody>
            <tr>
              <th>{app.count.redTags ? "Black" : "Tag"}</th>
              {#each RANKS as r, i (r)}
                <td>
                  <NumberInput
                    value={app.count.tags[i]}
                    min={-20}
                    max={20}
                    decimals={2}
                    width="3.4em"
                    label={`Tag of ${r}`}
                    format={(v) => signedTrim(v)}
                    onchange={(v) => setTag(i, false, v)}
                  />
                </td>
              {/each}
            </tr>
            {#if app.count.redTags}
              <tr>
                <th>Red cards</th>
                {#each RANKS as r, i (r)}
                  <td>
                    <NumberInput
                      value={app.count.redTags[i]}
                      min={-20}
                      max={20}
                      decimals={2}
                      width="3.4em"
                      label={`Tag of a red ${r}`}
                      format={(v) => signedTrim(v)}
                      onchange={(v) => setTag(i, true, v)}
                    />
                  </td>
                {/each}
              </tr>
            {/if}
          </tbody>
        </table>
      </div>
      <label class="check">
        <input type="checkbox" checked={app.count.redTags !== null} onchange={(e) => setRedTags(e.currentTarget.checked)} />
        Separate tags for red cards (e.g. Red Seven)
      </label>
      <p class="note">
        {info.balanced ? "Balanced" : `Unbalanced (${signedTrim(info.imbalance)} per deck)`} · level {trim(info.level, 2)} ·
        betting correlation {info.bettingCorrelation.toFixed(3)} · insurance correlation {info.insuranceCorrelation.toFixed(3)} ·
        1 Hi-Lo TC ≈ {trim(info.factor, 2)} of this count
      </p>
      <div class="fields">
        <Field label="Initial running count">
          <NumberInput
            value={app.count.ircBase}
            min={-200}
            max={200}
            decimals={1}
            width="4.5em"
            label="Initial running count"
            onchange={(v) => setConversion({ ircBase: v })}
          />
          <span>+</span>
          <NumberInput
            value={app.count.ircPerDeck}
            min={-20}
            max={20}
            decimals={1}
            width="4.5em"
            label="Initial running count per deck"
            onchange={(v) => setConversion({ ircPerDeck: v })}
          />
          <span>× decks</span>
        </Field>
      </div>
      <p class="note">Running count starts at {signedTrim(info.irc)} for {app.rules.decks} deck(s).</p>
    </Card>

    <Card title="True count conversion">
      <div class="fields">
        <Field label="Bet and play by">
          <select
            value={app.count.metric}
            onchange={(e) => setConversion({ metric: e.currentTarget.value as CountSystem["metric"] })}
          >
            <option value="TrueCount">True count</option>
            <option value="RunningCount">Running count</option>
          </select>
        </Field>
        <Field label="Deck estimation">
          <select
            value={app.count.deckEst}
            disabled={app.count.metric !== "TrueCount"}
            onchange={(e) => setConversion({ deckEst: e.currentTarget.value as CountSystem["deckEst"] })}
          >
            <option value="Exact">Exact (cards remaining)</option>
            <option value="QuarterDeck">Nearest quarter deck</option>
            <option value="HalfDeck">Nearest half deck</option>
            <option value="FullDeck">Nearest full deck</option>
          </select>
        </Field>
        <Field label="True count rounding">
          <select
            value={app.count.rounding}
            disabled={app.count.metric !== "TrueCount"}
            onchange={(e) => setConversion({ rounding: e.currentTarget.value as CountSystem["rounding"] })}
          >
            <option value="Floor">Floor (round down)</option>
            <option value="Truncate">Truncate (toward zero)</option>
            <option value="Round">Round to nearest</option>
          </select>
        </Field>
      </div>
      <p class="note">{metricNote}</p>
    </Card>

    <Card title="Playing strategy">
      <div class="fields">
        <Field label="Index plays">
          <select bind:value={app.strategy.mode}>
            {#each INDEX_MODES as m (m.value)}<option value={m.value}>{m.label}</option>{/each}
          </select>
        </Field>
        <Field label="Index range">
          <NumberInput
            value={app.strategy.indexMin}
            min={-100}
            max={100}
            width="4.2em"
            label="Lowest index"
            onchange={(v) => (app.strategy.indexMin = v)}
          />
          <span>to</span>
          <NumberInput
            value={app.strategy.indexMax}
            min={-100}
            max={100}
            width="4.2em"
            label="Highest index"
            onchange={(v) => (app.strategy.indexMax = v)}
          />
        </Field>
      </div>
      <p class="note">
        Indices are generated for the selected count and rules by combinatorial analysis of the average shoe composition at
        each count, so any tag set gets its own index numbers.
      </p>
    </Card>

    <Card title="Custom index plays">
      <p class="note">
        Rows here replace the computed decision for that play everywhere: e.g. Hard 16 v T, Stand, ≥, 0 means stand at 0 or
        more and hit below.
      </p>
      {#if rows.length}
        <div class="scroll-x">
          <table class="table overrides">
            <thead>
              <tr><th>On</th><th>Hand</th><th>Total</th><th>Dealer</th><th>Play</th><th>When</th><th>Index</th><th></th></tr>
            </thead>
            <tbody>
              {#each rows as row, i (i)}
                <tr class:invalid={toOverride(row) === null}>
                  <td><input type="checkbox" aria-label="Use this play" bind:checked={row.on} onchange={saveRows} /></td>
                  <td>
                    <select aria-label="Hand" bind:value={row.kind} onchange={saveRows}>
                      <option>Hard</option><option>Soft</option><option>Pair</option><option>Insurance</option>
                    </select>
                  </td>
                  <td>
                    <input
                      class="input"
                      aria-label="Total"
                      style:width="3.6em"
                      bind:value={row.total}
                      disabled={row.kind === "Insurance"}
                      onchange={saveRows}
                    />
                  </td>
                  <td>
                    <select aria-label="Dealer upcard" bind:value={row.up} disabled={row.kind === "Insurance"} onchange={saveRows}>
                      {#each UPCARDS as u (u)}<option>{u}</option>{/each}
                    </select>
                  </td>
                  <td>
                    <select aria-label="Play" bind:value={row.play} disabled={row.kind === "Insurance"} onchange={saveRows}>
                      {#each PLAYS as [value, label] (value)}<option {value}>{label}</option>{/each}
                    </select>
                  </td>
                  <td>
                    <select aria-label="When" bind:value={row.when} onchange={saveRows}>
                      <option>≥</option><option>≤</option>
                    </select>
                  </td>
                  <td>
                    <input class="input" aria-label="Index" style:width="4em" bind:value={row.index} onchange={saveRows} />
                  </td>
                  <td>
                    <button
                      class="btn remove"
                      aria-label="Remove row"
                      onclick={() => {
                        rows.splice(i, 1);
                        saveRows();
                      }}>✕</button
                    >
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
      <div class="buttons">
        <button
          class="btn"
          onclick={() => {
            addRow();
            saveRows();
          }}>Add row</button
        >
        <button class="btn" onclick={addPublished}>Add published Hi-Lo I18 + Fab 4</button>
        <button
          class="btn"
          disabled={!rows.length}
          onclick={() => {
            rows = [];
            saveRows();
          }}>Clear</button
        >
      </div>
    </Card>
  </div>

  <div class="col">
    <Card title="Strategy chart">
      <div class="fields">
        <Field label="Show strategy at">
          <select
            value={at === null ? "basic" : String(at)}
            onchange={(e) => (viewAt = e.currentTarget.value === "basic" ? null : Number(e.currentTarget.value))}
          >
            <option value="basic">Basic strategy (off the top)</option>
            {#each range as v (v)}<option value={String(v)}>{metricPrefix(app.count)}{signed(v)}</option>{/each}
          </select>
        </Field>
        <label class="check"><input type="checkbox" bind:checked={showIndices} /> Mark index plays in the chart</label>
      </div>
      <div class="legend">
        <span class="key" data-action="H">H</span>Hit
        <span class="key" data-action="S">S</span>Stand
        <span class="key" data-action="D">D</span>Double
        <span class="key" data-action="P">P</span>Split
        <span class="key" data-action="R">R</span>Surrender
        <span class="index-note">◥ index play (S≥+1: stand at +1 or more) · tap a cell for its EVs</span>
      </div>
      {#if chart}
        <div class="charts">
          <StrategyChart kind="Hard" codes={chart.codes} cellEv={chart.cellEv} {indices} {showIndices} {atLabel} onselect={(t) => (selected = t)} />
          <StrategyChart kind="Soft" codes={chart.codes} cellEv={chart.cellEv} {indices} {showIndices} {atLabel} onselect={(t) => (selected = t)} />
          <StrategyChart kind="Pair" codes={chart.codes} cellEv={chart.cellEv} {indices} {showIndices} {atLabel} onselect={(t) => (selected = t)} />
        </div>
        {#if selected}<p class="selected pre">{selected}</p>{/if}
      {:else}
        <p class="bad">Strategy error: {strategy}</p>
      {/if}
    </Card>

    <Card title="Index plays in use">
      <p class="note">Every decision that differs from basic strategy somewhere in the index range, for the selected count.</p>
      <div class="scroll-x">
        <table class="table">
          <thead><tr><th>Hand</th><th>Play</th><th>When</th></tr></thead>
          <tbody>
            {#each listed as i (i.hand + i.play)}
              <tr><td>{i.hand}</td><td>{i.play}</td><td>{i.condition}</td></tr>
            {:else}
              <tr><td>—</td><td>Basic strategy only</td><td></td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>
  </div>
</div>

<style>
  .tags th:first-child,
  .tags tbody th {
    text-align: left;
    color: var(--text);
  }

  .tags th {
    text-align: center;
  }

  .tags td {
    padding: 3px 2px;
    text-align: center;
  }

  .overrides td {
    padding: 3px 4px;
  }

  .overrides tr.invalid td {
    color: var(--bad);
  }

  .overrides tr.invalid .input {
    border-color: var(--bad);
  }

  .remove {
    min-height: 28px;
    padding: 2px 8px;
  }

  .legend {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 6px;
    color: var(--muted);
    font-size: 12.5px;
  }

  .key {
    display: inline-grid;
    place-items: center;
    width: 22px;
    height: 20px;
    font-weight: 600;
    color: var(--cell-text);
    background: var(--cell-hit);
    margin-left: 6px;
  }

  .key:first-child {
    margin-left: 0;
  }

  .key[data-action="S"] {
    background: var(--cell-stand);
  }

  .key[data-action="D"] {
    background: var(--cell-double);
  }

  .key[data-action="P"] {
    background: var(--cell-split);
  }

  .key[data-action="R"] {
    background: var(--cell-surrender);
  }

  .index-note {
    color: var(--index-mark);
    margin-left: 6px;
  }

  .charts {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .selected {
    background: var(--grid-header);
    border-radius: 6px;
    padding: 8px 10px;
    font-size: 12.5px;
  }
</style>
