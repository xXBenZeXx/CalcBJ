<script lang="ts">
  import Card from "../lib/components/Card.svelte";
  import { count, fixed, grouped, money, money0, pct, signedPct, trim } from "../lib/format";
  import type { CompareEntry } from "../lib/scenario";
  import { app } from "../lib/state.svelte";

  interface Metric {
    label: string;
    get: (e: CompareEntry) => string;
    /** Higher is better; rows without one are not highlighted. */
    value?: (e: CompareEntry) => number;
  }

  const METRICS: Metric[] = [
    { label: "Rules", get: (e) => e.rules },
    { label: "Count", get: (e) => e.count },
    { label: "Playing strategy", get: (e) => e.strategy },
    { label: "Bet ramp (units)", get: (e) => e.ramp },
    { label: "Spots / rounds per hour", get: (e) => `${e.spots} / ${e.roundsPerHour.toFixed(0)}` },
    { label: "Unit / bankroll", get: (e) => `${money(e.unit)} / ${money0(e.bankroll)}` },
    { label: "Basic strategy house edge", get: (e) => pct(e.houseEdge, 3), value: (e) => -e.houseEdge },
    { label: "Win rate per hour", get: (e) => money(e.winPerHour), value: (e) => e.winPerHour },
    { label: "SD per hour", get: (e) => money0(e.sdPerHour) },
    { label: "Win per 100 rounds", get: (e) => money(e.winPer100), value: (e) => e.winPer100 },
    { label: "Player edge", get: (e) => signedPct(e.edgePct / 100, 3), value: (e) => e.edgePct },
    { label: "Average bet", get: (e) => money(e.avgBet) },
    {
      label: "Spread / rounds played",
      get: (e) => (Number.isNaN(e.spread) ? "—" : `1–${trim(e.spread, 1)} / ${trim(e.playedPct, 1)}%`),
    },
    { label: "SCORE", get: (e) => fixed(e.score, 2), value: (e) => e.score },
    { label: "DI", get: (e) => fixed(e.di, 2), value: (e) => e.di },
    {
      label: "N0 (rounds / hours)",
      get: (e) => (e.n0 === Infinity ? "never (losing game)" : `${grouped(e.n0)} / ${grouped(e.hoursN0)}`),
      value: (e) => -e.n0,
    },
    { label: "Risk of ruin", get: (e) => pct(e.ror, 2), value: (e) => -e.ror },
    { label: "Bankroll for 5% ROR", get: (e) => money0(e.bankroll5), value: (e) => -e.bankroll5 },
    { label: "Kelly-optimal unit", get: (e) => money(e.kellyUnit) },
    { label: "Certainty equivalent / hour", get: (e) => money(e.cePerHour), value: (e) => e.cePerHour },
    { label: "Rounds simulated", get: (e) => count(e.rounds) + (e.stale ? " (stale)" : "") },
  ];

  let name = $state("");
  let message = $state("");

  /** Index of the best scenario in each row that has a better direction. */
  let best = $derived(
    METRICS.map((m) => {
      if (!m.value || app.compare.length < 2) return -1;
      let index = -1;
      let top = -Infinity;
      app.compare.forEach((e, i) => {
        const v = m.value!(e);
        if (!Number.isNaN(v) && v > top) {
          top = v;
          index = i;
        }
      });
      return index;
    }),
  );

  function add() {
    if (!app.sim) {
      message = "Run a simulation first.";
      return;
    }
    app.compare.push(app.snapshot(name.trim() || `Game ${app.compare.length + 1}`));
    name = "";
    message = "";
  }

  async function copy() {
    const lines = [["Metric", ...app.compare.map((e) => e.name)].join("\t")];
    for (const m of METRICS) lines.push([m.label, ...app.compare.map(m.get)].join("\t"));
    try {
      await navigator.clipboard.writeText(lines.join("\n"));
      message = "Copied — paste it into a spreadsheet.";
    } catch {
      message = "The browser did not allow copying.";
    }
  }
</script>

<Card title="Scenarios">
  <form
    class="buttons"
    onsubmit={(e) => {
      e.preventDefault();
      add();
    }}
  >
    <input class="input name" placeholder="Name (optional)" aria-label="Scenario name" bind:value={name} />
    <button class="btn btn-primary" type="submit">Add current scenario</button>
    <button class="btn" type="button" disabled={!app.compare.length} onclick={() => (app.compare = [])}>Clear all</button>
    <button class="btn" type="button" disabled={!app.compare.length} onclick={copy}>Copy table</button>
  </form>
  {#if message}<p class="note">{message}</p>{/if}
  <p class="note">Best value in each row is highlighted. Scenarios are saved inside the .bjev file.</p>
  <div class="scroll-x">
    <table class="table compare">
      <thead>
        <tr>
          <th class="metric"></th>
          {#each app.compare as e, i (i)}
            <th>
              <span class="head">
                <span>{e.name}</span>
                <button class="remove" aria-label={`Remove ${e.name}`} onclick={() => app.compare.splice(i, 1)}>✕</button>
              </span>
            </th>
          {:else}
            <th>No scenarios yet</th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each METRICS as m, r (m.label)}
          <tr>
            <td class="metric">{m.label}</td>
            {#each app.compare as e, i (i)}
              <td class:best={best[r] === i}>{m.get(e)}</td>
            {:else}
              <td class="muted">{r === 0 ? "Press “Add current scenario”." : ""}</td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</Card>

<style>
  .name {
    text-align: left;
    width: 16em;
  }

  .compare td,
  .compare th {
    white-space: normal;
    min-width: 13em;
    vertical-align: top;
  }

  .compare .metric {
    position: sticky;
    left: 0;
    z-index: 1;
    min-width: 11em;
    background: var(--card);
    color: var(--muted);
    font-weight: 600;
  }

  .compare tbody tr:nth-child(even) td.metric {
    background: var(--grid-alt);
  }

  .head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 8px;
    color: var(--text);
  }

  .remove {
    border: 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
    padding: 0 2px;
  }

  .remove:hover {
    color: var(--bad);
  }

  .compare td.best {
    background: var(--accent-soft);
    color: var(--accent-dark);
    font-weight: 600;
  }

  @media (prefers-color-scheme: dark) {
    .compare td.best {
      color: var(--good);
    }
  }
</style>
