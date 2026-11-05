<script lang="ts">
  import Card from "../lib/components/Card.svelte";
  import { touchScreen } from "../lib/platform";
  import { keyHint } from "../lib/text";
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
          <b>Simulation</b> — press {touchScreen ? "Run simulation" : "Run (F5)"}. Each round is dealt from a real shoe to
          every spot; your seat's results are stored for every count value. Simulations only run when you press Run,
          {__MOBILE__ ? "on the number of CPU threads set on that page." : "on every CPU thread of this device."}
          {#if __MOBILE__}
            A run pauses while CalcBJ is in the background and carries on when you come back; if the app is closed in
            between, the rounds simulated so far are kept.
          {/if}
        </li>
        <li>
          <b>Bet ramp &amp; results</b> — change units and hands per count and everything updates instantly, without
          re-simulating. The optimiser builds SCORE-maximising or Kelly ramps from the simulated EV and variance.
        </li>
        <li>
          <b>Bankroll &amp; risk, Charts, Sensitivity sweep, Compare</b> — risk of ruin, trip outcomes, how results change with
          one parameter, and side-by-side comparisons. Save scenarios{keyHint("Ctrl+S")}: the files open in the Windows app
          too.
        </li>
      </ol>
    </Card>

    <Card title="How indices are computed">
      <p>
        An index is the count at the moment you decide, which includes your cards and the dealer's upcard. So for every
        count value and hand, the program takes those cards out of the shoe and estimates the average make-up of the
        remaining cards when the count, including the cards on the table, has that value (a linear regression of the
        composition on the count, using your tags, red and black ones included); how deep in the shoe that count usually
        comes up sets the number of unseen cards. Then it runs a full combinatorial analysis of that composition: exact
        dealer probabilities with card removal for every
        two-card hand, optimal hitting, doubling, splitting (with resplits) and surrender. The best play at each count
        becomes the index. Index numbers follow your rounding method: with floored true counts (TC 0 means 0 to 0.99) some
        come out a point higher than tables that assume exact counts; 16 v T, for example, stands from +1.
      </p>
    </Card>

    <Card title="Accuracy">
      <p>
        The basic strategy expectation off the top plays the computed strategy exactly, with card removal for every hit,
        double and split hand, and agrees with shuffle-every-round simulations of 1 to 8 decks to within about 0.005%.
        House edges match published figures (6D S17 DAS 0.41%, H17 0.62%). This version uses the same engine as the Windows
        app, tested to give identical results. Precision of a simulated win rate is shown as ± one standard error; use more
        rounds to shrink it. Strategy is total-dependent (by hand total, not exact cards).
      </p>
    </Card>

    {#if __DESKTOP__}
      <Card title="Offline use">
        <p>
          CalcBJ runs entirely on this computer and needs no internet connection; nothing is sent anywhere. Settings and
          results are kept between sessions. To move them to another computer, save a scenario file and open it there.
        </p>
      </Card>
    {:else if __MOBILE__}
      <Card title="Offline use">
        <p>
          CalcBJ runs entirely on this device and needs no internet connection; nothing is sent anywhere. Settings and
          results stay in the app between sessions.
        </p>
        <p>
          To move them to another device, save a scenario file and open it there.
          {#if __IOS__}
            Saved files are in the Files app under On My iPhone › CalcBJ (On My iPad on an iPad).
          {:else}
            When you save, you choose the folder and the file name.
          {/if}
          The files also open in the Windows app and in the desktop app for Windows and Mac.
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

    {#if !touchScreen}
      <Card title="Shortcuts">
        <p>F5 run simulation · Esc stop · Ctrl+S save scenario · Ctrl+O open scenario</p>
      </Card>
    {/if}
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
