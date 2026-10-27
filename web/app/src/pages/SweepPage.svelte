<script lang="ts">
  import Card from "../lib/components/Card.svelte";
  import Chart from "../lib/components/Chart.svelte";
  import { compact } from "../lib/components/chart";
  import Field from "../lib/components/Field.svelte";
  import NumberInput from "../lib/components/NumberInput.svelte";
  import type { AnalysisResult } from "../lib/engine/types";
  import { count, fixed, grouped, money, pct, signedPct } from "../lib/format";
  import { simulation } from "../lib/simulation.svelte";
  import { SWEEP_METRICS, SWEEP_PARAMETERS, SWEEP_ROUNDS, sweep } from "../lib/sweep.svelte";

  /** The value plotted for a point, or null when it has none (N0 of a losing ramp). */
  function metricValue(r: AnalysisResult | null, metric: number): number | null {
    if (!r || !r.valid) return null;
    switch (metric) {
      case 1:
        return r.winPerHour;
      case 2:
        return r.edgePct;
      case 3:
        return r.ror * 100;
      case 4:
        return r.ev > 0 ? r.hoursN0 : null;
      case 5:
        return r.sdPerHour;
      default:
        return r.score;
    }
  }

  const FORMATS: ((v: number) => string)[] = [
    (v) => fixed(v, 2),
    money,
    (v) => signedPct(v / 100, 2),
    (v) => pct(v / 100, 2),
    (v) => grouped(v) + " h",
    money,
  ];
  const AXIS: ((v: number) => string)[] = [
    (v) => String(Number(v.toFixed(2))),
    (v) => (v < 0 ? "-$" : "$") + compact(Math.abs(v)),
    (v) => `${Number(v.toFixed(2))}%`,
    (v) => `${Number(v.toFixed(1))}%`,
    compact,
    (v) => "$" + compact(v),
  ];

  let values = $derived(sweep.points.map((p) => metricValue(p.result, sweep.metric)));
  let busyElsewhere = $derived(simulation.running && !sweep.running);
</script>

<div class="cols" style="--left: 38fr; --right: 62fr">
  <div class="col">
    <Card title="Sweep">
      <div class="fields">
        <Field label="Parameter">
          <select value={sweep.parameter} disabled={sweep.running} onchange={(e) => sweep.setParameter(Number(e.currentTarget.value))}>
            {#each SWEEP_PARAMETERS as name, i (name)}<option value={i}>{name}</option>{/each}
          </select>
        </Field>
        <Field label="From">
          <NumberInput value={sweep.from} min={-1000} max={100000} decimals={sweep.decimals} onchange={(v) => (sweep.from = v)} />
        </Field>
        <Field label="To">
          <NumberInput value={sweep.to} min={-1000} max={100000} decimals={sweep.decimals} onchange={(v) => (sweep.to = v)} />
        </Field>
        <Field label="Step">
          <NumberInput value={sweep.step} min={0.1} max={100000} decimals={sweep.decimals} onchange={(v) => (sweep.step = v)} />
        </Field>
        <Field label="Rounds per point">
          <select bind:value={sweep.rounds} disabled={sweep.running}>
            {#each SWEEP_ROUNDS as r (r)}<option value={r}>{count(r)}</option>{/each}
          </select>
        </Field>
      </div>
      <p class="note">{sweep.note}</p>
      <div class="buttons">
        <button class="btn btn-primary" disabled={sweep.running || busyElsewhere} onclick={() => sweep.run()}>Run sweep</button>
        <button class="btn" disabled={!sweep.running} onclick={() => sweep.stop()}>Stop</button>
      </div>
      {#if busyElsewhere}<p class="note">A simulation is running; the sweep can start when it has finished.</p>{/if}
      {#if sweep.status}<p class="note">{sweep.status}</p>{/if}
    </Card>

    <Card title="Points">
      <div class="scroll-x">
        <table class="table">
          <thead>
            <tr>
              <th class="num">Value</th>
              <th class="num">Win/h</th>
              <th class="num">Edge</th>
              <th class="num">SCORE</th>
              <th class="num">N0 (h)</th>
              <th class="num">ROR</th>
            </tr>
          </thead>
          <tbody>
            {#each sweep.points as p, i (i)}
              {@const r = p.result}
              <tr>
                <td class="num">{p.label}</td>
                {#if r && r.valid}
                  <td class="num">{money(r.winPerHour)}</td>
                  <td class="num">{signedPct(r.edgePct / 100, 2)}</td>
                  <td class="num">{fixed(r.score, 1)}</td>
                  <td class="num">{r.ev > 0 ? grouped(r.hoursN0) : "—"}</td>
                  <td class="num">{pct(r.ror, 1)}</td>
                {:else}
                  <td class="num">—</td><td class="num">—</td><td class="num">—</td><td class="num">—</td><td class="num">—</td>
                {/if}
              </tr>
            {:else}
              <tr><td colspan="6" class="muted">No points yet.</td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    </Card>
  </div>

  <div class="col">
    <Card title="Chart">
      <div class="fields">
        <Field label="Show">
          <select bind:value={sweep.metric}>
            {#each SWEEP_METRICS as name, i (name)}<option value={i}>{name}</option>{/each}
          </select>
        </Field>
      </div>
      {#if sweep.points.length}
        <Chart
          label={`${SWEEP_METRICS[sweep.metric]} for each ${SWEEP_PARAMETERS[sweep.parameter]}`}
          labels={sweep.points.map((p) => p.label)}
          xTitle={SWEEP_PARAMETERS[sweep.parameter]}
          tableHeader="Value"
          yFormat={AXIS[sweep.metric]}
          includeZero={false}
          height={420}
          series={[
            {
              name: SWEEP_METRICS[sweep.metric],
              kind: "line",
              markers: true,
              values,
              color: "var(--series-1)",
              format: FORMATS[sweep.metric],
            },
          ]}
        />
      {:else}
        <p class="note">Run a sweep to see how the result changes with the parameter.</p>
      {/if}
    </Card>
  </div>
</div>
