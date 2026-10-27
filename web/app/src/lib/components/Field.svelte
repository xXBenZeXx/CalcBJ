<script lang="ts">
  import type { Snippet } from "svelte";

  /** A labelled row; the label activates the first control inside. */
  let { label, children }: { label: string; children: Snippet } = $props();
</script>

<label class="field">
  <span class="label">{label}</span>
  <span class="control">{@render children()}</span>
</label>

<style>
  .field {
    display: flex;
    align-items: center;
    gap: 12px;
    min-height: 34px;
  }

  .label {
    flex: 0 0 11.5rem;
    color: var(--text);
  }

  .control {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    min-width: 0;
  }

  @media (max-width: 520px) {
    .label {
      flex-basis: 42%;
    }
  }
</style>
