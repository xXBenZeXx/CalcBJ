<script lang="ts">
  import type { Snippet } from "svelte";

  let { title, children }: { title: string; children: Snippet } = $props();
</script>

<section class="card">
  <h2>{title}</h2>
  {@render children()}
</section>

<style>
  .card {
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 14px 16px 16px;
    display: flex;
    flex-direction: column;
    gap: 10px;
    min-width: 0;
  }

  h2 {
    font-size: 15px;
    font-weight: 600;
  }
</style>
