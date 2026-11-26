<script lang="ts">
  import Banner from "../lib/components/Banner.svelte";
  import Card from "../lib/components/Card.svelte";
  import Dialog from "../lib/components/Dialog.svelte";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import Tile from "../lib/components/Tile.svelte";
  import Tiles from "../lib/components/Tiles.svelte";
  import { engine } from "../lib/engine/engine";
  import type { OptimizerSettings } from "../lib/engine/types";
  import { countLabel, fixed, grouped, money, money0, pct, ror, signedPct, trim } from "../lib/format";
  import { touchScreen } from "../lib/platform";
  import { simulation } from "../lib/simulation.svelte";
  import { CUSTOM_PRESET, RAMP_PRESETS, app } from "../lib/state.svelte";
  import { keyHint } from "../lib/text";

  let evaluation = $derived(app.evaluation);
  let a = $derived(evaluation?.result.valid ? evaluation.result : null);
  let ramp = $derived(app.ramp);
  let counting = $derived(app.count.metric === "RunningCount" ? "running count" : "true count");

  let banner = $derived.by((): { text: string; tone: "info" | "warn" } | null => {
    if (!app.sim) return { text: `No simulation yet — press Run simulation${keyHint("F5")}.`, tone: "info" };
    if (app.simOfAnotherCount)
      return {
        text:
          "⚠  The last simulation was run with another count, so this bet ramp can't be worked out on it. " +
          (touchScreen ? "Press Run simulation for these settings." : "Press F5 to simulate these settings."),
        tone: "warn",
      };
    if (app.simOutdated)
      return {
        text:
          "⚠  These results were simulated with an older version of CalcBJ. " +
          (touchScreen ? "Press Run simulation for this version's results." : "Press F5 for this version's results."),
        tone: "warn",
      };
    if (app.simStale)
      return {
        text:
          "⚠  Settings changed since the last simulation; these results are for the previous settings. " +
          (touchScreen ? "Press Run simulation to re-run." : "Press F5 to re-run."),
        tone: "warn",
      };
    if (a?.usedAssumedCov)
      return {
        text: "Multi-hand rows use an assumed correlation of 0.37 because the simulation was heads-up. Use 2+ spots for measured covariance.",
        tone: "info",
      };
    if (simulation.running) return { text: "Simulation running — results refine as more rounds come in.", tone: "info" };
    return null;
  });

  /** Win and variance contribution of every ramp row that occurs. */
  let contributions = $derived.by(() => {
    if (!evaluation || !a) return [];
    const result = a;
    const rows = evaluation.rows;
    const variance = ramp.units.map((u, i) => {
      const h = Math.max(1, ramp.hands[i]);
      const x = rows[i];
      return u > 0 && x ? x.freq * u * u * (h * x.m2 + h * (h - 1) * x.cov) : 0;
    });
    const totalWin = result.rowContribution.reduce((s, v) => s + v, 0);
    const totalVar = variance.reduce((s, v) => s + v, 0);
    return ramp.units.flatMap((u, i) =>
      result.rowFreq[i] > 0
        ? [
            {
              label: countLabel(ramp.min + i, ramp.min, ramp.max),
              freq: result.rowFreq[i],
              ev: result.rowEv[i],
              wager: u <= 0 ? "sit out" : money(u * app.unit * Math.max(1, ramp.hands[i])),
              win: result.rowContribution[i] * 100 * app.unit,
              winShare: totalWin !== 0 ? pct(result.rowContribution[i] / totalWin, 1) : "—",
              varShare: totalVar > 0 ? pct(variance[i] / totalVar, 1) : "—",
            },
          ]
        : [],
    );
  });

  function setUnits(i: number, v: number) {
    app.ramp.units[i] = v;
    app.rampPreset = CUSTOM_PRESET;
  }

  function setHands(i: number, v: number) {
    app.ramp.hands[i] = v;
    app.rampPreset = CUSTOM_PRESET;
  }

  function choosePreset(p: number) {
    if (p !== CUSTOM_PRESET) app.applyRampPreset(p);
  }

  // ---------------- optimiser and linear ramp ----------------

  let optimiseOpen = $state(false);
  let optimiseNeedsSim = $state(false);
  let opt = $state({ kelly: false, minUnits: 1, maxUnits: 12, increment: 1, maxHands: 1, kellyFraction: 1, wong: false });

  function openOptimise() {
    if (!app.sim) {
      optimiseNeedsSim = true;
      return;
    }
    optimiseNeedsSim = false;
    opt.maxUnits = Math.max(1, ...app.ramp.units);
    optimiseOpen = true;
  }

  function optimise() {
    if (!app.sim) return;
    const settings: OptimizerSettings = {
      kelly: opt.kelly,
      minUnits: opt.minUnits,
      maxUnits: Math.max(opt.minUnits, opt.maxUnits),
      increment: opt.increment,
      maxHands: opt.maxHands,
      kellyFraction: opt.kellyFraction,
      allowWongOut: opt.wong,
      bankrollUnits: app.bankroll / app.unit,
    };
    app.ramp = engine.optimize(app.sim, $state.snapshot(app.ramp), settings);
    app.rampPreset = CUSTOM_PRESET;
  }

  let linearOpen = $state(false);
  let lin = $state({ start: 1, below: 1, step: 2, max: 12, wong: false, wongAt: -2 });

  function linear() {
    const r = $state.snapshot(app.ramp);
    for (let v = r.min; v <= r.max; v++) {
      let u = v < lin.start ? lin.below : Math.min(lin.max, lin.below + lin.step * (v - lin.start + 1));
      if (lin.wong && v <= lin.wongAt) u = 0;
      r.units[v - r.min] = u;
      r.hands[v - r.min] = 1;
    }
    app.ramp = r;
    app.rampPreset = CUSTOM_PRESET;
  }
</script>

<div class="cols" style="--left: 40fr; --right: 60fr">
  <div class="col">
    <Card title="Betting">
      <div class="fields">
        <Field label="Betting unit ($)">
          <NumberInput
            value={app.unit}
            min={1}
            max={1e6}
            decimals={2}
            width="7em"
            format={(v) => v.toLocaleString("en-US", { maximumFractionDigits: 2 })}
            onchange={(v) => (app.unit = v)}
          />
        </Field>
        <Field label="Bankroll ($)">
          <NumberInput
            value={app.bankroll}
            min={1}
            max={1e9}
            width="7em"
            format={(v) => v.toLocaleString("en-US")}
            onchange={(v) => (app.bankroll = v)}
          />
        </Field>
        <Field label="Rounds per hour">
          <NumberInput value={app.rph} min={10} max={400} width="7em" onchange={(v) => app.setManualRph(v)} />
        </Field>
        <Field label="Ramp rows">
          <NumberInput
            value={ramp.min}
            min={-100}
            max={99}
            width="4.2em"
            label="Lowest ramp row"
            onchange={(v) => app.setRampRange(v, ramp.max)}
          />
          <span>to</span>
          <NumberInput
            value={ramp.max}
            min={-99}
            max={100}
            width="4.2em"
            label="Highest ramp row"
            onchange={(v) => app.setRampRange(ramp.min, v)}
          />
        </Field>
      </div>
    </Card>

    <Card title="Bet ramp">
      <div class="buttons">
        <select value={app.rampPreset} aria-label="Ramp preset" onchange={(e) => choosePreset(Number(e.currentTarget.value))}>
          {#each RAMP_PRESETS as name, i (name)}<option value={i}>{name}</option>{/each}
        </select>
        <button class="btn btn-primary" onclick={openOptimise}>Optimise…</button>
        <button class="btn" onclick={() => (linearOpen = true)}>Linear…</button>
      </div>
      {#if optimiseNeedsSim && !app.sim}<p class="bad">Run a simulation first.</p>{/if}
      <p class="note">
        Edit Units and Hands directly. 0 units = sit out (wong out); 2–3 hands use the covariance measured between
        neighbouring seats.
      </p>
      <div class="scroll-x">
        <table class="table ramp">
          <thead>
            <tr>
              <th class="num">Count</th>
              <th class="num">Freq.</th>
              <th class="num">EV/hand</th>
              <th class="num">Units</th>
              <th class="num">Hands</th>
              <th class="num">Bet</th>
            </tr>
          </thead>
          <tbody>
            {#each ramp.units as units, i (ramp.min + i)}
              {@const hands = ramp.hands[i]}
              {@const n = evaluation?.result.rowN[i] ?? 0}
              {@const rowEv = evaluation?.result.rowEv[i] ?? 0}
              <tr>
                <td class="num">{countLabel(ramp.min + i, ramp.min, ramp.max)}</td>
                <td class="num">{evaluation ? pct(evaluation.result.rowFreq[i], 2) : "—"}</td>
                <td class="num" class:good={!!evaluation && n > 0 && rowEv >= 0} class:bad={!!evaluation && n > 0 && rowEv < 0}>
                  {evaluation && n > 0 ? signedPct(rowEv, 2) : "—"}
                </td>
                <td class="num edit">
                  <NumberInput
                    value={units}
                    min={0}
                    max={100000}
                    decimals={2}
                    width="4.6em"
                    label={`Units at ${ramp.min + i}`}
                    onchange={(v) => setUnits(i, v)}
                  />
                </td>
                <td class="num edit">
                  <select value={hands} aria-label={`Hands at ${ramp.min + i}`} onchange={(e) => setHands(i, Number(e.currentTarget.value))}>
                    <option value={1}>1</option><option value={2}>2</option><option value={3}>3</option>
                  </select>
                </td>
                <td class="num" class:muted={units <= 0}>
                  {units <= 0 ? "sit out" : money(units * app.unit) + (hands > 1 ? ` × ${hands}` : "")}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>
  </div>

  <div class="col">
    {#if banner}<Banner text={banner.text} tone={banner.tone} />{/if}
    <Card title="Results">
      <Tiles>
        {#if a && evaluation && app.sim}
          <Tile
            title="Win rate per hour"
            value={money(a.winPerHour)}
            sub={`${money(a.winPer100)} per 100 rounds · ± ${money(a.seWinPerHour)} sim error`}
            tone={a.winPerHour >= 0 ? "good" : "bad"}
            tip="Expected profit per hour at your unit size and rounds per hour (rounds you sit out still take time)."
          />
          <Tile
            title="Std. deviation per hour"
            value={money0(a.sdPerHour)}
            sub={`${money0(a.sdPer100)} per 100 rounds`}
            tip="Standard deviation of one hour's result. After h hours the SD is this × √h."
          />
          <Tile
            title="Player edge"
            value={signedPct(a.edgePct / 100, 3)}
            sub={`avg bet ${money(a.avgBetPlayed * app.unit)} when playing`}
            tone={a.edgePct >= 0 ? "good" : "bad"}
            tip="Win rate divided by the average initial amount wagered per round played."
          />
          <Tile
            title="SCORE"
            value={fixed(a.score, 2)}
            sub={`win/100 rds on a $10k Kelly bankroll  ·  DI ${fixed(a.di, 2)}`}
            tip="Win rate per 100 rounds for a $10,000 bankroll bet at full Kelly with this ramp shape (= DI²). Compares games independent of bet size."
          />
          <Tile
            title="N0 (long run)"
            value={a.ev > 0 ? `${grouped(a.n0)} rds` : "never"}
            sub={a.ev > 0 ? `≈ ${grouped(a.hoursN0)} hours` : "negative expectation"}
            tip="Rounds needed until the expected win equals one standard deviation: (SD/EV)². After N0 rounds you are ahead about 84% of the time."
          />
          <Tile
            title="Risk of ruin"
            value={ror(a.ror)}
            sub={`lifetime, ${money0(app.bankroll)} bankroll`}
            tone={a.ror > 0.2 ? "bad" : a.ror < 0.05 ? "good" : undefined}
            tip="Chance of eventually losing the whole bankroll if you never change your unit: exp(−2·EV·B/variance)."
          />
          <Tile
            title="Bankroll for 5% ROR"
            value={money0(evaluation.bankrollFor[0])}
            sub={`13.5% ROR: ${money0(evaluation.bankrollFor[1])}  ·  1%: ${money0(evaluation.bankrollFor[2])}`}
            tip="Bankroll that gives a 5% (13.5%, 1%) lifetime risk of ruin with this ramp and unit."
          />
          <Tile
            title="Kelly-optimal unit"
            value={money(a.kellyUnit)}
            sub={a.kellyUnit > 0 ? `your unit is ${fixed(app.unit / a.kellyUnit, 2)}× Kelly` : ""}
            tip="Unit size that maximises long-run bankroll growth for this ramp shape and your bankroll."
          />
          <Tile
            title="Certainty equivalent"
            value={money(a.cePerHour) + "/h"}
            sub="risk-adjusted win for your bankroll"
            tone={a.cePerHour >= 0 ? "good" : "bad"}
            tip="Risk-adjusted value of the game for your bankroll (log utility): win rate − variance/(2·bankroll), per hour."
          />
          <Tile title="Spread & coverage" value={"1–" + trim(a.spread, 1)} sub={`you play ${pct(a.playedFrac, 1)} of rounds`} />
          <Tile
            title="Hours to double bankroll"
            value={a.winPerHour > 0 ? `${grouped(app.bankroll / a.winPerHour)} h` : "—"}
            sub="expected, at this unit"
          />
          <Tile
            title="Basic strategy edge"
            value={signedPct(app.sim.basicEv, 3)}
            sub="player EV off the top (CA)"
            tone={app.sim.basicEv >= 0 ? "good" : "bad"}
          />
        {:else}
          {#each ["Win rate per hour", "Std. deviation per hour", "Player edge", "SCORE", "N0 (long run)", "Risk of ruin", "Bankroll for 5% ROR", "Kelly-optimal unit", "Certainty equivalent", "Spread & coverage", "Hours to double bankroll", "Basic strategy edge"] as t (t)}
            <Tile title={t} value="—" />
          {/each}
        {/if}
      </Tiles>
    </Card>

    <Card title="Where the money comes from">
      {#if contributions.length}
        <div class="scroll-x">
          <table class="table">
            <thead>
              <tr>
                <th class="num">Count</th>
                <th class="num">Freq.</th>
                <th class="num">EV/hand</th>
                <th class="num">Wager</th>
                <th class="num">Win / 100 rds</th>
                <th class="num">Share of win</th>
                <th class="num">Share of variance</th>
              </tr>
            </thead>
            <tbody>
              {#each contributions as c (c.label)}
                <tr>
                  <td class="num">{c.label}</td>
                  <td class="num">{pct(c.freq, 2)}</td>
                  <td class="num">{signedPct(c.ev, 2)}</td>
                  <td class="num">{c.wager}</td>
                  <td class="num" class:good={c.win >= 0} class:bad={c.win < 0}>{money(c.win)}</td>
                  <td class="num">{c.winShare}</td>
                  <td class="num">{c.varShare}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {:else}
        <p class="note">Run a simulation to see what each count contributes.</p>
      {/if}
    </Card>
  </div>
</div>

<Dialog bind:open={optimiseOpen} title="Optimise bet ramp" confirmText="Optimise" onconfirm={optimise}>
  <p class="note">
    Finds the bet for every count from the simulated EV and variance at that count. Bets are Kelly-proportional (EV ÷
    variance), clipped to your limits and rounded to the increment.
  </p>
  <label class="check">
    <input type="radio" name="goal" checked={!opt.kelly} onchange={() => (opt.kelly = false)} />
    Maximise SCORE (best risk-adjusted win rate within the spread)
  </label>
  <label class="check">
    <input type="radio" name="goal" checked={opt.kelly} onchange={() => (opt.kelly = true)} />
    Kelly bets for my bankroll ({grouped(app.bankroll / app.unit)} units)
  </label>
  <div class="fields">
    <Field label="Minimum bet (units)">
      <NumberInput value={opt.minUnits} min={0.25} max={1000} decimals={2} onchange={(v) => (opt.minUnits = v)} />
    </Field>
    <Field label="Maximum bet (units)">
      <NumberInput value={opt.maxUnits} min={0.25} max={10000} decimals={2} onchange={(v) => (opt.maxUnits = v)} />
    </Field>
    <Field label="Bet increment (units)">
      <NumberInput value={opt.increment} min={0.05} max={100} decimals={2} onchange={(v) => (opt.increment = v)} />
    </Field>
    <Field label="Max hands per round">
      <select bind:value={opt.maxHands}><option value={1}>1</option><option value={2}>2</option><option value={3}>3</option></select>
    </Field>
    <Field label="Kelly fraction">
      <NumberInput
        value={opt.kellyFraction}
        min={0.05}
        max={2}
        decimals={2}
        disabled={!opt.kelly}
        onchange={(v) => (opt.kellyFraction = v)}
      />
    </Field>
  </div>
  <label class="check"><input type="checkbox" bind:checked={opt.wong} /> Sit out (wong out) when a hand isn't worth its risk</label>
</Dialog>

<Dialog bind:open={linearOpen} title="Generate linear ramp" confirmText="Generate" onconfirm={linear}>
  <p class="note">Bets ramp up linearly from the starting {counting}.</p>
  <div class="fields">
    <Field label={`Start raising at ${counting}`}>
      <NumberInput value={lin.start} min={-100} max={100} onchange={(v) => (lin.start = v)} />
    </Field>
    <Field label="Units below the start">
      <NumberInput value={lin.below} min={0} max={1000} decimals={2} onchange={(v) => (lin.below = v)} />
    </Field>
    <Field label="Extra units per +1">
      <NumberInput value={lin.step} min={0} max={1000} decimals={2} onchange={(v) => (lin.step = v)} />
    </Field>
    <Field label="Maximum units">
      <NumberInput value={lin.max} min={0.5} max={10000} decimals={2} onchange={(v) => (lin.max = v)} />
    </Field>
  </div>
  <label class="check"><input type="checkbox" bind:checked={lin.wong} /> Sit out at low counts</label>
  <div class="fields">
    <Field label="Sit out at count ≤">
      <NumberInput value={lin.wongAt} min={-100} max={100} disabled={!lin.wong} onchange={(v) => (lin.wongAt = v)} />
    </Field>
  </div>
</Dialog>

<style>
  .ramp td {
    padding-top: 3px;
    padding-bottom: 3px;
  }

  /* Keep all six columns on a phone screen. */
  @media (max-width: 520px) {
    .ramp th,
    .ramp td {
      padding-left: 3px;
      padding-right: 3px;
      font-size: 13px;
    }

    .ramp :global(.input) {
      width: 3.4em !important;
    }
  }
</style>
