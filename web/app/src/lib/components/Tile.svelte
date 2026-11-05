<script lang="ts">
  import { touchScreen } from "../platform";

  let {
    title,
    value,
    sub = "",
    tone = undefined,
    tip = undefined,
  }: { title: string; value: string; sub?: string; tone?: "good" | "bad"; tip?: string } = $props();

  // Touch screens show no tooltips, so there the explanation opens under the value with the "i" button instead.
  let explain = $state(false);
</script>

<div class="tile" title={touchScreen ? undefined : tip}>
  <div class="top">
    <div class="title">{title}</div>
    {#if tip && touchScreen}
      <button
        type="button"
        class="info"
        aria-label={`About ${title}`}
        aria-expanded={explain}
        onclick={() => (explain = !explain)}
      >
        <span aria-hidden="true">i</span>
      </button>
    {/if}
  </div>
  <div class="value" class:good={tone === "good"} class:bad={tone === "bad"}>{value}</div>
  {#if sub}<div class="sub">{sub}</div>{/if}
  {#if tip && touchScreen && explain}<p class="tip">{tip}</p>{/if}
</div>

<style>
  .tile {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 8px 10px;
    min-width: 0;
  }

  .top {
    display: flex;
    align-items: flex-start;
    gap: 4px;
  }

  .title {
    flex: 1;
    min-width: 0;
    font-size: 11.5px;
    font-weight: 600;
    color: var(--muted);
    text-transform: uppercase;
    letter-spacing: 0.02em;
  }

  /* A 32 px target around an 18 px circle; the negative margins keep it from making the title row taller. */
  .info {
    flex: none;
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    margin: -7px -9px -8px 0;
    padding: 0;
    border: 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }

  .info span {
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    border: 1.5px solid currentColor;
    border-radius: 50%;
    font: italic 600 11px/1 Georgia, "Times New Roman", serif;
  }

  .info[aria-expanded="true"] {
    color: var(--accent);
  }

  .value {
    font-size: 20px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    margin-top: 2px;
  }

  .sub {
    font-size: 12px;
    color: var(--muted);
    margin-top: 2px;
  }

  .tip {
    font-size: 12px;
    margin-top: 6px;
    padding-top: 6px;
    border-top: 1px solid var(--border);
  }
</style>
