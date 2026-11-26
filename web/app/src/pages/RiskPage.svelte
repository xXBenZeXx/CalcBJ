<script lang="ts">
  import Banner from "../lib/components/Banner.svelte";
  import Card from "../lib/components/Card.svelte";
  import Chart from "../lib/components/Chart.svelte";
  import { compact } from "../lib/components/chart";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import Tile from "../lib/components/Tile.svelte";
  import Tiles from "../lib/components/Tiles.svelte";
  import { engine } from "../lib/engine/engine";
  import { grouped, money, money0, pct, ror as rorText } from "../lib/format";
  import { app } from "../lib/state.svelte";
  import { keyHint } from "../lib/text";

  const TARGETS = [0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.135, 0.2, 0.3];
  const PERCENTILES = [0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99];
  /** The 90th percentile of the normal distribution, from the same function as the table's 90% row. */
  const Z90 = engine.phiInv([0.9])[0] ?? 1.2815515655446004;

  let a = $derived(app.evaluation?.result.valid ? app.evaluation.result : null);

  /** Per observed round, in dollars, and the trip totals. */
  let trip = $derived.by(() => {
    if (!a) return null;
    const mu = a.ev * app.unit;
    const variance = a.var * app.unit * app.unit;
    const n = app.tripHours * a.roundsPerHour;
    const expected = mu * n;
    const sd = Math.sqrt(variance * n);
    const z = engine.phiInv(PERCENTILES);
    return {
      mu,
      variance,
      expected,
      sd,
      lose: engine.probLoss(mu, variance, n),
      ruin: engine.tripRuin(mu, variance, app.tripBankroll, n),
      percentiles: PERCENTILES.map((p, i) => ({ p, v: expected + (z[i] ?? 0) * sd })),
    };
  });

  function bankrollFor(ror: number): number {
    if (!a || a.ev <= 0) return Infinity;
    return ((-Math.log(ror) * a.var) / (2 * a.ev)) * app.unit;
  }

  // All hands of the top bet.
  let maxBet = $derived(Math.max(0, ...app.ramp.units.map((u, i) => u * Math.max(1, app.ramp.hands[i]))) * app.unit);

  /** Lifetime ROR for bankrolls up to the one that gives 0.1%. */
  let rorCurve = $derived.by(() => {
    if (!a || a.ev <= 0) return null;
    const result = a;
    const top = bankrollFor(0.001);
    const x = Array.from({ length: 120 }, (_, k) => (top * (k + 1)) / 120);
    const ror = x.map((b) => 100 * Math.exp((-2 * result.ev * (b / app.unit)) / result.var));
    // Your bankroll as a marker at its own position on the curve.
    const at = app.bankroll <= top ? x.findIndex((b) => b >= app.bankroll) : -1;
    if (at >= 0) {
      x.splice(at, 0, app.bankroll);
      ror.splice(at, 0, 100 * result.ror);
    }
    return { x, ror, mine: x.map((b, i) => (i === at ? ror[i] : null)) };
  });

  /** Expected result with ±1 and ±2 SD bands, out to about 1.2 × N0. */
  let path = $derived.by(() => {
    if (!a || !trip) return null;
    const h0 = a.hoursN0;
    const horizon = Math.max(app.tripHours, Math.min(5000, h0 !== null && h0 > 0 && Number.isFinite(h0) ? h0 * 1.2 : 200));
    const x = Array.from({ length: 101 }, (_, k) => (horizon * k) / 100);
    const rph = a.roundsPerHour;
    const m = x.map((h) => trip.mu * h * rph);
    const s = x.map((h) => Math.sqrt(trip.variance * h * rph));
    return {
      x,
      expected: m,
      lo1: m.map((v, i) => v - s[i]),
      hi1: m.map((v, i) => v + s[i]),
      lo2: m.map((v, i) => v - 2 * s[i]),
      hi2: m.map((v, i) => v + 2 * s[i]),
    };
  });

  let banner = $derived.by((): { text: string; tone: "info" | "warn" } | null => {
    if (!app.sim) return { text: `No simulation yet — press Run simulation${keyHint("F5")}.`, tone: "info" };
    if (app.simOfAnotherCount)
      return { text: "The last simulation was run with another count. Run the simulation for these settings.", tone: "info" };
    if (!a) return { text: "The current ramp never bets.", tone: "info" };
    if (app.simOutdated)
      return { text: "⚠  Simulated with an older version of CalcBJ; run the simulation for this version's figures.", tone: "warn" };
    if (app.simStale)
      return { text: "⚠  Settings changed since the last simulation; figures are for the previous settings.", tone: "warn" };
    return null;
  });

  const dollars = (v: number) => (v < 0 ? "-$" : "$") + compact(Math.abs(v));
</script>

<div class="cols" style="--left: 42fr; --right: 58fr">
  <div class="col">
    <Card title="Inputs">
      <div class="fields">
        <Field label="Total bankroll ($)">
          <NumberInput
            value={app.bankroll}
            min={1}
            max={1e9}
            width="7.5em"
            format={(v) => v.toLocaleString("en-US")}
            onchange={(v) => (app.bankroll = v)}
          />
        </Field>
        <Field label="Trip length (hours)">
          <NumberInput value={app.tripHours} min={1} max={100000} width="7.5em" onchange={(v) => (app.tripHours = v)} />
        </Field>
        <Field label="Trip bankroll ($)">
          <NumberInput
            value={app.tripBankroll}
            min={1}
            max={1e9}
            width="7.5em"
            format={(v) => v.toLocaleString("en-US")}
            onchange={(v) => (app.tripBankroll = v)}
          />
        </Field>
      </div>
      <p class="note">
        Lifetime risk of ruin assumes you keep betting the same unit forever (no resizing). Trip figures use a normal
        approximation for the result after the trip and the reflection principle for the chance of busting the trip
        bankroll at any point during it.
      </p>
    </Card>

    <Card title="Bankroll needed">
      <div class="scroll-x">
        <table class="table">
          <thead>
            <tr><th class="num">Risk of ruin</th><th class="num">Bankroll</th><th class="num">Units</th><th class="num">Max bets</th></tr>
          </thead>
          <tbody>
            {#each TARGETS as t (t)}
              {@const b = bankrollFor(t)}
              <tr>
                <td class="num">{pct(t, t < 0.01 || t === 0.135 ? 1 : 0)}</td>
                <td class="num">{a ? money0(b) : "—"}</td>
                <!-- A losing game: no bankroll is enough, in every column. -->
                <td class="num">{a && Number.isFinite(b) ? grouped(b / app.unit) : "—"}</td>
                <td class="num">{a && maxBet > 0 && Number.isFinite(b) ? grouped(b / maxBet) : "—"}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>

    <Card title="Trip result percentiles">
      <div class="scroll-x">
        <table class="table">
          <thead><tr><th class="num">Percentile</th><th class="num">Trip result</th><th class="num">Per hour</th></tr></thead>
          <tbody>
            {#each PERCENTILES as p, i (p)}
              {@const v = trip?.percentiles[i].v ?? null}
              <tr>
                <td class="num">{pct(p, 0)}</td>
                <td class="num" class:good={v !== null && v >= 0} class:bad={v !== null && v < 0}>{money0(v)}</td>
                <td class="num">{v === null ? "—" : money(v / app.tripHours)}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>
  </div>

  <div class="col">
    {#if banner}<Banner text={banner.text} tone={banner.tone} />{/if}
    <Card title="Risk summary">
      <Tiles>
        {#if a && trip}
          <Tile
            title="Lifetime risk of ruin"
            value={rorText(a.ror)}
            sub={`with ${money0(app.bankroll)}`}
            tone={a.ror > 0.2 ? "bad" : a.ror < 0.05 ? "good" : undefined}
          />
          <Tile
            title="Expected trip result"
            value={money0(trip.expected)}
            sub={`± ${money0(trip.sd)} (1 SD) over ${grouped(app.tripHours)} h`}
            tone={trip.expected >= 0 ? "good" : "bad"}
          />
          <Tile title="Chance of a losing trip" value={pct(trip.lose, 1)} sub="behind at the end of the trip" />
          <Tile
            title="Chance to bust trip bankroll"
            value={rorText(trip.ruin)}
            sub={`trip bankroll ${money0(app.tripBankroll)}`}
            tone={trip.ruin > 0.2 ? "bad" : undefined}
          />
          <Tile title="Good trip (90th pct.)" value={money0(trip.expected + Z90 * trip.sd)} sub="1 trip in 10 is better" tone="good" />
          <Tile
            title="Bad trip (10th pct.)"
            value={money0(trip.expected - Z90 * trip.sd)}
            sub="1 trip in 10 is worse"
            tone={trip.expected - Z90 * trip.sd >= 0 ? "good" : "bad"}
          />
        {:else}
          {#each ["Lifetime risk of ruin", "Expected trip result", "Chance of a losing trip", "Chance to bust trip bankroll", "Good trip (90th pct.)", "Bad trip (10th pct.)"] as t (t)}
            <Tile title={t} value="—" />
          {/each}
        {/if}
      </Tiles>
    </Card>

    <Card title="Risk of ruin vs. bankroll">
      {#if rorCurve}
        <Chart
          label="Lifetime risk of ruin for each bankroll"
          x={rorCurve.x}
          xFormat={dollars}
          xTitle="Bankroll ($)"
          tableHeader="Bankroll"
          yMin={0}
          yMax={100}
          yFormat={(v) => `${Number(v.toFixed(1))}%`}
          height={250}
          series={[
            { name: "Lifetime risk of ruin", kind: "line", values: rorCurve.ror, color: "var(--series-1)" },
            { name: "Your bankroll", kind: "point", values: rorCurve.mine, color: "var(--series-2)" },
          ]}
        />
      {:else}
        <p class="note">{a ? "Negative expectation: ruin is certain in the long run." : "Run a simulation to see the curve."}</p>
      {/if}
    </Card>

    <Card title="Expected result over time (±1 and ±2 SD)">
      {#if path}
        <Chart
          label="Expected result over time with one and two standard deviation bands"
          x={path.x}
          xFormat={(v) => grouped(v)}
          xTitle="Hours played"
          tableHeader="Hours"
          yFormat={dollars}
          height={280}
          series={[
            { name: "±2 SD", kind: "band", values: path.lo2, high: path.hi2, color: "var(--series-1)", opacity: 0.1, format: money0 },
            { name: "±1 SD", kind: "band", values: path.lo1, high: path.hi1, color: "var(--series-1)", opacity: 0.2, format: money0 },
            { name: "Expected", kind: "line", values: path.expected, color: "var(--series-1)", format: money0 },
          ]}
        />
      {:else}
        <p class="note">Run a simulation to see how results spread out over time.</p>
      {/if}
    </Card>
  </div>
</div>
