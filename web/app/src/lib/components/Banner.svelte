<script lang="ts">
  let { text, tone = "info" }: { text: string; tone?: "info" | "warn" } = $props();
</script>

<div class="banner" class:warn={tone === "warn"} role="status">{text}</div>

<style>
  .banner {
    border-radius: var(--radius);
    padding: 10px 14px;
    background: var(--info-bg);
    color: var(--info);
    border: 1px solid color-mix(in srgb, var(--info) 30%, transparent);
  }

  .warn {
    background: var(--warn-bg);
    color: var(--warn);
    border-color: color-mix(in srgb, var(--warn) 35%, transparent);
  }
</style>
