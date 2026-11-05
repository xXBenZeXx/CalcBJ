<script lang="ts">
  import type { Snippet } from "svelte";
  import { openLayer } from "../back";
  import { touchScreen } from "../platform";

  /** Modal dialog; `open` is bound. Escape, Cancel or back (Android, a browser) closes it without confirming. */
  let {
    open = $bindable(false),
    title,
    confirmText,
    onconfirm,
    children,
  }: { open?: boolean; title: string; confirmText: string; onconfirm: () => void; children: Snippet } = $props();

  let dialog: HTMLDialogElement;

  $effect(() => {
    if (!open) return;
    if (!dialog.open) dialog.showModal();
    // While it is open it has a history entry, so that back closes it (lib/back.ts).
    const release = openLayer(() => (open = false));
    return () => {
      release();
      if (dialog.open) dialog.close();
    };
  });
</script>

<dialog bind:this={dialog} onclose={() => (open = false)}>
  <!-- On touch screens the title takes the focus when the dialog opens rather than its first field, which would
       bring up the keyboard over the dialog before it is read. With a mouse and keyboard the first field has it: the
       browser focuses the first element that can take the focus, so there the title can't. -->
  {#if touchScreen}
    <h2 tabindex="-1" autofocus>{title}</h2>
  {:else}
    <h2>{title}</h2>
  {/if}
  <form
    method="dialog"
    onsubmit={(e) => {
      e.preventDefault();
      onconfirm();
      open = false;
    }}
  >
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
    width: min(460px, calc(100vw - 32px - var(--safe-left) - var(--safe-right)));
    /* A tall dialog (a phone in landscape) scrolls inside, clear of a camera cutout and the bars. */
    max-height: calc(100% - 32px - var(--safe-top) - var(--safe-bottom));
  }

  dialog::backdrop {
    background: rgb(10 18 25 / 45%);
  }

  h2 {
    font-size: 17px;
    font-weight: 600;
    margin-bottom: 10px;
  }

  h2:focus {
    outline: none;
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
