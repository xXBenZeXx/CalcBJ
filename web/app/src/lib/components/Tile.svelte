<script lang="ts">
  let {
    title,
    value,
    sub = "",
    tone = undefined,
    tip = undefined,
  }: { title: string; value: string; sub?: string; tone?: "good" | "bad"; tip?: string } = $props();
</script>

<div class="tile" title={tip}>
  <div class="title">{title}</div>
  <div class="value" class:good={tone === "good"} class:bad={tone === "bad"}>{value}</div>
  {#if sub}<div class="sub">{sub}</div>{/if}
</div>

<style>
  .tile {
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 8px 10px;
    min-width: 0;
  }

  .title {
    font-size: 11.5px;
    font-weight: 600;
    color: var(--muted);
    text-transform: uppercase;
    letter-spacing: 0.02em;
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
</style>
