<script lang="ts">
  import Card from "../lib/components/Card.svelte";
</script>

<div class="cols">
  <div class="col">
    <Card title="Workflow">
      <ol>
        <li>
          <b>Game rules</b> — enter the table rules. The basic-strategy house edge and the value of each rule update
          immediately.
        </li>
        <li>
          <b>Count &amp; strategy</b> — pick a count (or type your own tags), the true-count method and which index plays you
          use. Indices are generated for your exact count and rules; you can override any of them with your own numbers.
        </li>
        <li>
          <b>Simulation</b> — press Run (F5). Each round is dealt from a real shoe to every spot; your seat's results are
          stored for every count value. Simulations only run when you press Run, on every CPU thread of this device.
        </li>
        <li>
          <b>Bet ramp &amp; results</b> — change units and hands per count and everything updates instantly, without
          re-simulating. The optimiser builds SCORE-maximising or Kelly ramps from the simulated EV and variance.
        </li>
        <li>
          <b>Bankroll &amp; risk, Charts, Sensitivity sweep, Compare</b> — risk of ruin, trip outcomes, how results change with
          one parameter, and side-by-side comparisons. Save scenarios (Ctrl+S): the files open in the Windows app too.
        </li>
      </ol>
    </Card>

    <Card title="How indices are computed">
      <p>
        For every count value the program estimates the average composition of the unseen cards (a linear regression of the
        composition on the count, using your tags) at mid-shoe depth, then runs a full combinatorial analysis of that
        composition: exact dealer probabilities with card removal for every two-card hand, optimal hitting, doubling,
        splitting (with resplits) and surrender. The best play at each count becomes the index. Index numbers follow your
        rounding method: with floored true counts they come out about half a point lower than tables that assume exact
        counts.
      </p>
    </Card>

    <Card title="Accuracy">
      <p>
        The simulator is checked against the combinatorial engine on shuffle-every-round games (agreement within ~0.01% for
        shoe games). Typical 6-deck house edges match published figures (6D S17 DAS 0.40%, H17 0.62%). This version uses the
        same engine as the Windows app, tested to give identical results. Precision of a simulated win rate is shown as ± one
        standard error; use more rounds to shrink it. Strategy is total-dependent (by hand total, not exact cards).
      </p>
    </Card>

    {#if __DESKTOP__}
      <Card title="Offline use">
        <p>
          CalcBJ runs entirely on this computer and needs no internet connection; nothing is sent anywhere. Settings and
          results are kept between sessions. To move them to another computer, save a scenario file and open it there.
        </p>
      </Card>
    {:else}
      <Card title="Install and offline use">
        <p>
          <b>Windows, Mac, Android:</b> in Chrome or Edge, choose Install (in the address bar, the browser menu, or
          “Install app” in CalcBJ's menu). <b>iPhone and iPad:</b> in Safari, tap Share, then Add to Home Screen.
        </p>
        <p>
          After the first visit CalcBJ works without an internet connection. Everything is calculated on this device and
          nothing is sent anywhere. Settings and results stay in this browser; to move them to another device, save a
          scenario file and open it there.
        </p>
      </Card>
    {/if}
  </div>

  <div class="col">
    <Card title="Glossary">
      <dl>
        <dt>Win rate</dt>
        <dd>Expected profit per hour (or per 100 rounds) at your unit.</dd>
        <dt>SD</dt>
        <dd>Standard deviation of results over an hour (or 100 rounds).</dd>
        <dt>Player edge</dt>
        <dd>Win rate divided by the average initial amount wagered.</dd>
        <dt>DI (desirability index)</dt>
        <dd>1000 × EV ÷ SD per round. Independent of bet size.</dd>
        <dt>SCORE</dt>
        <dd>Win per 100 rounds for a $10,000 bankroll bet with optimal Kelly sizing; equals DI².</dd>
        <dt>N0</dt>
        <dd>
          Rounds needed before expectation equals one standard deviation: (SD ÷ EV)². After N0 rounds you are ahead with
          about 84% probability.
        </dd>
        <dt>Risk of ruin</dt>
        <dd>Chance of losing the whole bankroll if you never resize your bets: exp(−2·EV·B ÷ variance).</dd>
        <dt>Kelly-optimal unit</dt>
        <dd>Unit size that maximises bankroll growth for this ramp shape: B × EV ÷ E[W²].</dd>
        <dt>Certainty equivalent</dt>
        <dd>Risk-adjusted value of the game to a log-utility player with your bankroll: EV − variance ÷ (2B).</dd>
        <dt>Covariance / multiple hands</dt>
        <dd>
          When your ramp uses 2 or 3 hands, the variance includes the measured covariance between neighbouring seats (≈ 0.48
          units² per pair of hands).
        </dd>
        <dt>Wonging</dt>
        <dd>0 units in a row means you sit out those counts; rates are still per observed round.</dd>
      </dl>
    </Card>

    <Card title="Shortcuts">
      <p>F5 run simulation · Esc stop · Ctrl+S save scenario · Ctrl+O open scenario</p>
    </Card>
  </div>
</div>

<style>
  ol {
    margin: 0;
    padding-left: 1.3em;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  p + p {
    margin-top: 8px;
  }

  dl {
    margin: 0;
  }

  dt {
    font-weight: 600;
    margin-top: 8px;
  }

  dt:first-child {
    margin-top: 0;
  }

  dd {
    margin: 2px 0 0;
    color: var(--muted);
  }
</style>
