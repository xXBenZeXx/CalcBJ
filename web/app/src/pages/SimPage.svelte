<script lang="ts">
  import Banner from "../lib/components/Banner.svelte";
  import Card from "../lib/components/Card.svelte";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import Tile from "../lib/components/Tile.svelte";
  import Tiles from "../lib/components/Tiles.svelte";
  import { engine } from "../lib/engine/engine";
  import { count, fixed, grouped, pct, signed, signedPct } from "../lib/format";
  import { touchScreen } from "../lib/platform";
  import { flatRamp } from "../lib/sim-data";
  import { simulation } from "../lib/simulation.svelte";
  import { ROUND_OPTIONS, app, maxThreads } from "../lib/state.svelte";
  import { countText, keyHint, strategyText } from "../lib/text";

  let info = $derived(engine.rulesInfo(app.rules, app.spots));
  let d = $derived(app.sim);
  let flat = $derived(d ? engine.evaluate(d, flatRamp(), 1, 100, 1000).result : null);

  /** Counts that occur at least 0.005% of the time (and everything between them). */
  let bins = $derived.by(() => {
    if (!d) return [];
    const stats = engine.bins(d);
    const common = stats.filter((b) => b.freq >= 0.00005).map((b) => b.value);
    if (!common.length) return [];
    const lo = Math.min(...common);
    const hi = Math.max(...common);
    return stats.filter((b) => b.value >= lo && b.value <= hi && b.n > 0);
  });

  let progressText = $derived.by(() => {
    let s: string;
    if (simulation.running) s = `Running: ${((100 * simulation.done) / simulation.target).toFixed(1)}% of ${count(simulation.target)} rounds`;
    else if (d) s = `Finished ${new Date(d.created).toLocaleTimeString("en-GB")}.`;
    else s = "No results yet.";
    return (
      s +
      `\nResults are kept for ${app.cacheCount} configuration(s): returning to one of them is instant, ` +
      "and Run adds rounds to the results already held."
    );
  });
</script>

<div class="cols" style="--left: 42fr; --right: 58fr">
  <div class="col">
    <Card title="Simulation settings">
      <div class="fields">
        <Field label="Rounds to simulate">
          <select bind:value={app.simRounds}>
            {#each ROUND_OPTIONS as r (r)}<option value={r}>{count(r)} rounds</option>{/each}
          </select>
        </Field>
        <Field label="CPU threads">
          <NumberInput value={app.threads} min={1} max={maxThreads} width="4.5em" onchange={(v) => (app.threads = v)} />
        </Field>
      </div>
      <p class="note">
        Simulations only run when you press Run{keyHint("F5")}. Settings you already simulated show their results straight
        away.
      </p>
      <div class="buttons">
        <button class="btn btn-primary" onclick={() => app.run()}>▶ Run simulation{keyHint("F5")}</button>
        <button class="btn" disabled={!simulation.running} onclick={() => app.stop()}>Stop{keyHint("Esc")}</button>
        <button class="btn" onclick={() => app.discardAndRun()}>Discard results & re-run</button>
      </div>
      <p class="note pre">
        Rules: {info.describe}
        Count: {countText(app.count)}
        Play: {strategyText(app.strategy)}
        Table: {app.spots} spot(s), cut card at {info.cutCard} of {info.totalCards} cards
      </p>
      <p class="note pre">{progressText}</p>
    </Card>

    <Card title="How the simulator works">
      <p class="note">
        Every round is dealt from a shuffled shoe to all spots; the dealer peeks (or not) as the rules say, and every player
        decision uses your strategy at the running/true count at that moment, counting every card that has been exposed.
        Results for your seat are accumulated separately for each count value, together with the covariance to the
        neighbouring seat. That is all the bet-ramp, risk and optimiser pages need, so they never have to re-simulate.
      </p>
      {#if touchScreen}
        <p class="note">
          Each CPU thread runs its own copy of the engine (WebAssembly in a Web Worker) on its own shoe, and player results
          are tallied exactly (in 1/20 units). The Speed tile shows how many rounds per second this device simulates; more
          spots per round take proportionally longer.
        </p>
      {:else}
        <p class="note">
          Each CPU thread runs its own copy of the engine (WebAssembly in a Web Worker) on its own shoe, and player results
          are tallied exactly (in 1/20 units). A modern 8-core computer runs on the order of 100 million heads-up rounds
          per second; more spots per round take proportionally longer, and phones are several times slower.
        </p>
      {/if}
    </Card>
  </div>

  <div class="col">
    {#if d && app.simStale}
      <Banner
        tone="warn"
        text={`⚠  Settings have changed since this simulation. Results below are for: ${d.rulesText} · ${d.countText} · ${d.strategyText}`}
      />
    {/if}
    <Card title="Results">
      <Tiles>
        {#if d && d.rounds > 0}
          <Tile title="Rounds simulated" value={count(d.rounds)} sub={`${grouped(d.shoes)} shoes`} />
          <Tile
            title="Speed"
            value={`${fixed(d.seconds > 0 ? d.rounds / d.seconds / 1e6 : 0, 1)}M/s`}
            sub={`${fixed(d.seconds, 1)} s on ${d.threads} threads`}
          />
          <Tile
            title="Rounds per shoe"
            value={fixed(d.shoes > 0 ? d.rounds / d.shoes : 0, 1)}
            sub={d.emergency > 0 ? `${d.emergency} reshuffle fallbacks` : "per shuffle"}
          />
          <Tile
            title="Flat-bet EV"
            value={signedPct(flat?.ev ?? null, 3)}
            sub={`± ${pct(flat?.seEv ?? null, 3)} (1 SE), with your indices`}
            tone={flat && flat.ev >= 0 ? "good" : "bad"}
          />
          <Tile title="SD per hand" value={fixed(flat?.sd ?? null, 3)} sub="units per round, flat bet" />
          <Tile title="Theoretical EV (CA)" value={signedPct(d.basicEv, 3)} sub="basic strategy off the top" />
        {:else}
          {#each ["Rounds simulated", "Speed", "Rounds per shoe", "Flat-bet EV", "SD per hand", "Theoretical EV (CA)"] as t (t)}
            <Tile title={t} value="—" />
          {/each}
        {/if}
      </Tiles>
    </Card>

    <Card title="Results by count (one hand, flat bet)">
      {#if bins.length}
        <div class="scroll-x">
          <table class="table">
            <thead>
              <tr>
                <th class="num">Count</th>
                <th class="num">Rounds</th>
                <th class="num">Frequency</th>
                <th class="num">EV / hand</th>
                <th class="num">± 1 SE</th>
                <th class="num">SD</th>
                <th class="num">2-hand corr.</th>
              </tr>
            </thead>
            <tbody>
              {#each bins as b (b.value)}
                {@const v = Math.max(0, b.m2 - b.mean * b.mean)}
                <tr>
                  <td class="num">{signed(b.value)}</td>
                  <td class="num">{grouped(b.n)}</td>
                  <td class="num">{pct(b.freq, 3)}</td>
                  <td class="num" class:good={b.mean >= 0} class:bad={b.mean < 0}>{signedPct(b.mean, 3)}</td>
                  <td class="num">{pct(Math.sqrt(b.meanVar), 3)}</td>
                  <td class="num">{fixed(Math.sqrt(v), 3)}</td>
                  <td class="num">{b.measuredCov ? fixed(v > 0 ? (b.cov - b.mean * b.mean) / v : 0, 3) : "(assumed)"}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {:else}
        <p class="note">Run a simulation to see the results for each count.</p>
      {/if}
    </Card>
  </div>
</div>
