<script lang="ts">
  import Banner from "../lib/components/Banner.svelte";
  import Card from "../lib/components/Card.svelte";
  import Chart from "../lib/components/Chart.svelte";
  import { compact } from "../lib/components/chart";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import { engine } from "../lib/engine/engine";
  import { countLabel, grouped, money, money0, pct, signedPct } from "../lib/format";
  import { app } from "../lib/state.svelte";

  let hours = $state(100);

  let a = $derived(app.evaluation?.result.valid ? app.evaluation.result : null);
  let ramp = $derived(app.ramp);
  let labels = $derived(ramp.units.map((_, i) => countLabel(ramp.min + i, ramp.min, ramp.max)));

  // Small multiples on one count axis: frequency, advantage and bet each get their own scale (never two y-axes).
  let frequency = $derived(a ? a.rowFreq.map((f) => f * 100) : []);
  let advantage = $derived(a ? a.rowEv.map((v, i) => (a.rowN[i] > 0 ? v * 100 : null)) : []);
  let bets = $derived(ramp.units.map((u, i) => u * Math.max(1, ramp.hands[i])));
  let contribution = $derived(a ? a.rowContribution.map((c) => c * 100 * app.unit) : []);

  /** Normal approximation of the result after `hours`, with the losing side shaded. */
  let distribution = $derived.by(() => {
    if (!a) return null;
    const n = hours * a.roundsPerHour;
    const mu = a.ev * app.unit * n;
    const sd = Math.sqrt(a.var * n) * app.unit;
    if (!(sd > 0)) return null;
    const lo = mu - 3.5 * sd;
    const hi = mu + 3.5 * sd;
    const x = Array.from({ length: 201 }, (_, k) => lo + ((hi - lo) * k) / 200);
    const density = x.map((v) => Math.exp(-0.5 * ((v - mu) / sd) ** 2) / (sd * Math.sqrt(2 * Math.PI)));
    return {
      x,
      density,
      losing: density.map((y, i) => (x[i] < 0 ? y : null)),
      mu,
      sd,
      lose: engine.phi([-mu / sd])[0],
    };
  });

  let banner = $derived.by((): { text: string; tone: "info" | "warn" } | null => {
    if (!app.sim) return { text: "No simulation yet — press Run simulation (F5).", tone: "info" };
    if (!a) return { text: "The current ramp never bets.", tone: "info" };
    if (app.simStale)
      return { text: "⚠  Settings changed since the last simulation; charts show the previous settings.", tone: "warn" };
    return null;
  });

  const percent = (v: number) => `${Number(v.toFixed(2))}%`;
  const dollars = (v: number) => (v < 0 ? "-$" : "$") + compact(Math.abs(v));
</script>

{#if banner}<Banner text={banner.text} tone={banner.tone} />{/if}

<div class="cols">
  <div class="col">
    <Card title="Count frequency and player advantage">
      {#if a}
        <p class="panel">Share of rounds at each count</p>
        <Chart
          label="Share of rounds at each count"
          {labels}
          tableHeader="Count"
          yFormat={percent}
          height={170}
          series={[{ name: "Share of rounds", kind: "bar", values: frequency, color: "var(--series-1)", format: (v) => pct(v / 100, 2) }]}
        />
        <p class="panel">Player advantage per hand</p>
        <Chart
          label="Player advantage per hand at each count"
          {labels}
          tableHeader="Count"
          yFormat={percent}
          height={170}
          series={[
            {
              name: "Advantage per hand",
              kind: "line",
              markers: true,
              values: advantage,
              color: "var(--series-1)",
              format: (v) => signedPct(v / 100, 2),
            },
          ]}
        />
      {:else}
        <p class="note">Run a simulation to see how often each count comes up and what it is worth.</p>
      {/if}
    </Card>

    <Card title="Contribution to win rate by count">
      {#if a}
        <Chart
          label="Win per 100 rounds contributed by each count"
          {labels}
          tableHeader="Count"
          yFormat={dollars}
          height={230}
          series={[
            {
              name: "Win per 100 rounds",
              kind: "bar",
              values: contribution,
              colors: contribution.map((w) => (w >= 0 ? null : "var(--series-negative)")),
              color: "var(--series-1)",
              format: money,
            },
          ]}
        />
      {:else}
        <p class="note">Run a simulation to see where the win rate comes from.</p>
      {/if}
    </Card>
  </div>

  <div class="col">
    <Card title="Your bet ramp against the advantage">
      {#if a}
        <p class="panel">Bet at each count (units × hands)</p>
        <Chart
          label="Bet at each count in units times hands"
          {labels}
          tableHeader="Count"
          yFormat={(v) => String(Number(v.toFixed(2)))}
          height={170}
          series={[{ name: "Bet (units)", kind: "bar", values: bets, color: "var(--series-1)" }]}
        />
        <p class="panel">Player advantage per hand</p>
        <Chart
          label="Player advantage per hand at each count"
          {labels}
          tableHeader="Count"
          yFormat={percent}
          height={170}
          series={[
            {
              name: "Advantage per hand",
              kind: "line",
              markers: true,
              values: advantage,
              color: "var(--series-1)",
              format: (v) => signedPct(v / 100, 2),
            },
          ]}
        />
      {:else}
        <p class="note">Run a simulation to compare your bets with the advantage.</p>
      {/if}
    </Card>

    <Card title="Distribution of results">
      <div class="fields">
        <Field label="After this many hours">
          <NumberInput value={hours} min={1} max={100000} width="6.5em" onchange={(v) => (hours = v)} />
        </Field>
      </div>
      {#if distribution}
        <p class="note">
          Expected {money0(distribution.mu)}, SD {money0(distribution.sd)}, chance of losing {pct(distribution.lose, 1)}
        </p>
        <Chart
          label={`Probability density of the result after ${grouped(hours)} hours`}
          x={distribution.x}
          xFormat={dollars}
          xTitle="Result ($)"
          tableHeader="Result"
          yFormat={() => ""}
          height={240}
          series={[
            {
              name: `Result after ${grouped(hours)} h`,
              kind: "area",
              values: distribution.density,
              color: "var(--series-1)",
              format: (v) => v.toExponential(2),
            },
            {
              name: "Losing outcomes",
              kind: "area",
              values: distribution.losing,
              color: "var(--series-negative)",
              opacity: 0.18,
              format: (v) => v.toExponential(2),
            },
          ]}
        />
      {:else}
        <p class="note">Run a simulation to see the spread of results.</p>
      {/if}
    </Card>
  </div>
</div>

<style>
  .panel {
    font-size: 12.5px;
    font-weight: 600;
    color: var(--muted);
    margin-top: 4px;
  }
</style>
