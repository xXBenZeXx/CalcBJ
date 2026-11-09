<script lang="ts">
  import Card from "../lib/components/Card.svelte";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import { engine } from "../lib/engine/engine";
  import type { DoubleRule, HoleCardRule, Rules, SurrenderRule } from "../lib/engine/types";
  import { pct, signedPct, trim } from "../lib/format";
  import { app } from "../lib/state.svelte";

  const PAYOUTS: [number, string][] = [
    [1.5, "3:2"],
    [1.4, "7:5"],
    [1.2, "6:5"],
    [1.0, "1:1"],
    [2.0, "2:1"],
  ];

  function payoutText(p: number): string {
    return PAYOUTS.find(([v]) => Math.abs(v - p) < 1e-9)?.[1] ?? trim(p, 2) + ":1";
  }

  /** The list, plus the payout in use when it isn't one of them (it can come from a scenario file). */
  let payouts = $derived(
    PAYOUTS.some(([v]) => Math.abs(v - app.rules.blackjackPays) < 1e-9)
      ? PAYOUTS
      : [...PAYOUTS, [app.rules.blackjackPays, payoutText(app.rules.blackjackPays)] as [number, string]],
  );

  let info = $derived(engine.rulesInfo(app.rules, app.spots));
  let ev = $derived(engine.houseEdge(app.rules));

  // Player expectation if only one rule were different. Some twenty games take about a second to work out: a worker
  // does that, and each row is filled in when its value is there (null until then).
  let effects = $state.raw<{ label: string; ev: number | null; delta: number | null }[]>([]);
  $effect(() => {
    const base = ev;
    const rows = ruleVariants($state.snapshot(app.rules));
    const show = (values: (number | null)[]) => {
      effects = rows.map(([label], i) => {
        const e = values[i];
        return { label, ev: e, delta: e === null ? null : e - base };
      });
    };
    const job = engine.houseEdges(
      rows.map(([, rules]) => rules),
      show,
    );
    show(job.known);
    return job.cancel;
  });

  /** The rules with one of them different, each with what changed. */
  function ruleVariants(r: Rules): [string, Rules][] {
    const rows: [string, Rules][] = [];
    const add = (label: string, change: Partial<Rules>) => rows.push([label, { ...r, ...change }]);
    add(r.h17 ? "Dealer stands on soft 17" : "Dealer hits soft 17", { h17: !r.h17 });
    for (const d of [1, 2, 4, 6, 8]) if (d !== r.decks) add(d === 1 ? "1 deck" : `${d} decks`, { decks: d });
    add(r.das ? "No double after split" : "Double after split allowed", { das: !r.das });
    const surrender: [SurrenderRule, string][] = [
      ["None", "No surrender"],
      ["Late", "Late surrender"],
      ["Early", "Early surrender"],
    ];
    for (const [s, label] of surrender) if (s !== r.surrender) add(label, { surrender: s });
    const doubles: [DoubleRule, string][] = [
      ["AnyTwo", "Double on any two cards"],
      ["NineToEleven", "Double on 9–11 only"],
      ["TenToEleven", "Double on 10–11 only"],
    ];
    for (const [d, label] of doubles) if (d !== r.doubleOn) add(label, { doubleOn: d });
    for (let m = 2; m <= 4; m++) if (m !== r.maxSplitHands) add(`Split to ${m} hands`, { maxSplitHands: m });
    add(r.resplitAces ? "No resplitting aces" : "Resplit aces allowed", { resplitAces: !r.resplitAces });
    add(r.hitSplitAces ? "No hitting split aces" : "Hit split aces allowed", { hitSplitAces: !r.hitSplitAces });
    for (const p of [1.5, 1.2, 1.4])
      if (Math.abs(p - r.blackjackPays) > 1e-9) add("Blackjack pays " + payoutText(p), { blackjackPays: p });
    const holes: [HoleCardRule, string][] = [
      ["USPeek", "US peek"],
      ["ENHC", "No hole card (ENHC)"],
      ["ENHC_OBO", "No hole card, OBO"],
    ];
    for (const [h, label] of holes) if (h !== r.holeCard) add(label, { holeCard: h });
    return rows;
  }

  /** Decks and penetration move what a running count (or an unbalanced one) means: the ramp follows. */
  function setShoe(change: { decks?: number; penetration?: number }) {
    const old = $state.snapshot(app.rules);
    Object.assign(app.rules, change);
    app.rulesChanged(old);
  }
</script>

<div class="cols" style="--left: 54fr; --right: 46fr">
  <div class="col">
    <Card title="Shoe">
      <div class="fields">
        <Field label="Number of decks">
          <select value={app.rules.decks} onchange={(e) => setShoe({ decks: Number(e.currentTarget.value) })}>
            {#each [1, 2, 3, 4, 5, 6, 7, 8] as d (d)}<option value={d}>{d}</option>{/each}
          </select>
        </Field>
        <Field label="Penetration (% dealt)">
          <NumberInput
            value={app.rules.penetration * 100}
            min={20}
            max={95}
            decimals={1}
            width="5.5em"
            onchange={(v) => setShoe({ penetration: v / 100 })}
          />
        </Field>
        <p class="note">
          Cut card after {info.cutCard} of {info.totalCards} cards · {trim(info.cutCard / 52, 2)} of {app.rules.decks}
          decks dealt
        </p>
        <label class="check"><input type="checkbox" bind:checked={app.rules.burnCard} /> Burn one card after each shuffle</label>
      </div>
    </Card>

    <Card title="Dealer">
      <div class="fields">
        <Field label="Soft 17">
          <select value={app.rules.h17 ? "h17" : "s17"} onchange={(e) => (app.rules.h17 = e.currentTarget.value === "h17")}>
            <option value="s17">Dealer stands (S17)</option>
            <option value="h17">Dealer hits (H17)</option>
          </select>
        </Field>
        <Field label="Hole card">
          <select bind:value={app.rules.holeCard}>
            <option value="USPeek">US peek (hole card)</option>
            <option value="ENHC">No hole card, lose all (ENHC)</option>
            <option value="ENHC_OBO">No hole card, original bets only (OBO)</option>
          </select>
        </Field>
        <label class="check"><input type="checkbox" bind:checked={app.rules.insurance} /> Insurance / even money offered</label>
      </div>
    </Card>

    <Card title="Player options">
      <div class="fields">
        <Field label="Blackjack pays">
          <select bind:value={app.rules.blackjackPays}>
            {#each payouts as [v, label] (v)}<option value={v}>{label}</option>{/each}
          </select>
        </Field>
        <Field label="Double down on">
          <select bind:value={app.rules.doubleOn}>
            <option value="AnyTwo">Any two cards</option>
            <option value="NineToEleven">9, 10, 11 only</option>
            <option value="TenToEleven">10, 11 only</option>
          </select>
        </Field>
        <label class="check"><input type="checkbox" bind:checked={app.rules.das} /> Double after split (DAS)</label>
        <Field label="Split up to">
          <select bind:value={app.rules.maxSplitHands}>
            <option value={1}>No splitting</option>
            <option value={2}>2 hands</option>
            <option value={3}>3 hands</option>
            <option value={4}>4 hands</option>
          </select>
        </Field>
        <label class="check"><input type="checkbox" bind:checked={app.rules.resplitAces} /> Resplit aces (RSA)</label>
        <label class="check"><input type="checkbox" bind:checked={app.rules.hitSplitAces} /> Hit split aces (HSA)</label>
        <Field label="Surrender">
          <select bind:value={app.rules.surrender}>
            <option value="None">Not allowed</option>
            <option value="Late">Late surrender</option>
            <option value="Early">Early surrender</option>
          </select>
        </Field>
      </div>
    </Card>

    <Card title="Table">
      <div class="fields">
        <Field label="Spots dealt per round">
          <select bind:value={app.spots}>
            {#each [1, 2, 3, 4, 5, 6, 7] as s (s)}<option value={s}>{s}</option>{/each}
          </select>
        </Field>
        <p class="note">
          {app.spots === 1
            ? "Heads-up. Covariance for playing two hands is then estimated (ρ ≈ 0.37)."
            : "You play the last seat (third base); the seat beside it is used when your ramp calls for two or three hands."}
        </p>
        <Field label="Rounds per hour">
          <NumberInput value={app.rph} min={10} max={400} disabled={app.autoRph} width="5.5em" onchange={(v) => app.setManualRph(v)} />
        </Field>
        <label class="check">
          <input type="checkbox" checked={app.autoRph} onchange={(e) => app.setAutoRph(e.currentTarget.checked)} />
          Estimate rounds per hour from the number of spots
        </label>
      </div>
    </Card>
  </div>

  <div class="col">
    <Card title="Basic strategy expectation (off the top)">
      {#if ev <= 0}
        <p class="huge bad">{pct(-ev, 3)}</p>
        <p class="note">
          House edge for a perfect basic-strategy player on the first round of a fresh shoe (player expectation
          {signedPct(ev, 3)}).
        </p>
      {:else}
        <p class="huge good">+{pct(ev, 3)}</p>
        <p class="note">
          Player edge for a perfect basic-strategy player on the first round of a fresh shoe. With a cut card a flat bet
          does a little worse than that (about 0.1% with one deck): simulate basic strategy to see it.
        </p>
      {/if}
    </Card>

    <Card title="Effect of changing one rule">
      <p class="note">
        Change in player expectation if only this rule were different (combinatorial analysis, basic strategy).
      </p>
      <div class="scroll-x">
        <table class="table">
          <thead><tr><th>Rule change</th><th class="num">Player EV</th><th class="num">Effect</th></tr></thead>
          <tbody>
            {#each effects as e (e.label)}
              <tr>
                <td>{e.label}</td>
                <td class="num">{e.ev === null ? "…" : signedPct(e.ev, 3)}</td>
                <td
                  class="num strong"
                  class:good={e.delta !== null && e.delta > 0.00005}
                  class:bad={e.delta !== null && e.delta < -0.00005}
                  class:muted={e.delta === null || Math.abs(e.delta) <= 0.00005}
                >
                  {e.delta === null ? "…" : signedPct(e.delta, 3)}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>
  </div>
</div>

<style>
  .huge {
    font-size: 40px;
    font-weight: 600;
    line-height: 1.1;
    font-variant-numeric: tabular-nums;
  }
</style>
