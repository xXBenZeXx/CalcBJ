<script lang="ts">
  import type { Snippet } from "svelte";

  /** Modal dialog; `open` is bound. Escape or Cancel closes it without confirming. */
  let {
    open = $bindable(false),
    title,
    confirmText,
    onconfirm,
    children,
  }: { open?: boolean; title: string; confirmText: string; onconfirm: () => void; children: Snippet } = $props();

  let dialog: HTMLDialogElement;

  $effect(() => {
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  });
</script>

<dialog bind:this={dialog} onclose={() => (open = false)}>
  <form
    method="dialog"
    onsubmit={(e) => {
      e.preventDefault();
      onconfirm();
      open = false;
    }}
  >
    <h2>{title}</h2>
    <div class="body">{@render children()}</div>
    <div class="buttons end">
      <button class="btn btn-primary" type="submit">{confirmText}</button>
      <button class="btn" type="button" onclick={() => (open = false)}>Cancel</button>
    </div>
  </form>
</dialog>

<style>
  dialog {
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--card);
    color: var(--text);
    padding: 18px 20px;
    width: min(460px, calc(100vw - 32px));
  }

  dialog::backdrop {
    background: rgb(10 18 25 / 45%);
  }

  h2 {
    font-size: 17px;
    font-weight: 600;
    margin-bottom: 10px;
  }

  .body {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .end {
    justify-content: flex-end;
    margin-top: 16px;
  }
</style>
