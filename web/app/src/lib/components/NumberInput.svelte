<script lang="ts">
  import { parseNumber, round } from "../format";
  import { touchScreen } from "../platform";

  /** A number field that commits on Enter or when it loses focus, clamped to [min, max] and rounded to `decimals`. */
  let {
    value,
    onchange,
    min = -Infinity,
    max = Infinity,
    decimals = 0,
    width = "6.5em",
    disabled = false,
    label = undefined,
    format = undefined,
    signBelow = false,
  }: {
    value: number;
    onchange: (v: number) => void;
    min?: number;
    max?: number;
    decimals?: number;
    width?: string;
    disabled?: boolean;
    label?: string;
    format?: (v: number) => string;
    /** Puts the ± button under the field instead of beside it, for narrow table columns. */
    signBelow?: boolean;
  } = $props();

  const show = (v: number) => (format ? format(v) : String(Number(v.toFixed(decimals))));
  let text = $state("");
  let invalid = $state(false);
  let input: HTMLInputElement;

  // The iPhone's number pad has no minus key (and some Android keyboards lack one too), so on touch screens a field
  // that takes negative numbers gets a ± button.
  let signButton = $derived(touchScreen && min < 0);

  $effect(() => {
    text = show(value);
    invalid = false;
  });

  /** Rounded to `decimals` (half away from zero, as the Windows app's number boxes) and clamped to [min, max]. */
  function fit(v: number): number {
    return Math.min(max, Math.max(min, round(v, decimals)));
  }

  function set(v: number) {
    const c = fit(v);
    invalid = false;
    text = show(c);
    if (c !== value) onchange(c);
  }

  function commit() {
    const v = parseNumber(text);
    if (v === null) invalid = true;
    else set(v);
  }

  /** ± flips the number in the field, typed or not; on an empty field or 0 it starts a negative number instead. */
  function flipSign() {
    const v = parseNumber(text);
    if (v !== null && fit(v) !== 0) {
      set(-fit(v));
      return;
    }
    // A second ± takes the minus away again.
    text = text.trim().startsWith("-") ? show(value) : "-";
    invalid = false;
    input.focus();
  }

  /** Tapping ± leaves the focus, and with it the keyboard, in a field that is being typed in. */
  const keepFocus = (e: MouseEvent) => e.preventDefault();

  /** A minus that ± started, with no digits typed after it, is dropped when the field loses focus. */
  function onblur() {
    if (signButton && text.trim() === "-") {
      text = show(value);
      invalid = false;
    }
  }
</script>

{#snippet field()}
  <input
    bind:this={input}
    class="input"
    class:invalid
    type="text"
    inputmode="decimal"
    style:width
    bind:value={text}
    {disabled}
    aria-label={label}
    onchange={commit}
    {onblur}
    onkeydown={(e) => e.key === "Enter" && commit()}
  />
{/snippet}

{#if signButton}
  <span class="signed" class:below={signBelow}>
    {@render field()}
    <button type="button" class="sign" aria-label="Change sign" {disabled} onmousedown={keepFocus} onclick={flipSign}>±</button>
  </span>
{:else}
  {@render field()}
{/if}

<style>
  .signed {
    display: inline-flex;
    max-width: 100%;
  }

  .signed .input {
    min-width: 0;
    border-top-right-radius: 0;
    border-bottom-right-radius: 0;
  }

  .sign {
    flex: none;
    width: 32px;
    min-height: 32px;
    padding: 0;
    border: 1px solid var(--border);
    border-left: 0;
    border-radius: 0 6px 6px 0;
    background: var(--grid-header);
    color: var(--text);
    font-size: 16px;
    font-weight: 600;
    line-height: 1;
    cursor: pointer;
    /* Two quick taps flip the sign twice instead of zooming the page. */
    touch-action: manipulation;
  }

  .sign:active {
    background: var(--border);
  }

  .sign:disabled {
    opacity: 0.55;
    cursor: default;
  }

  .below {
    flex-direction: column;
  }

  .below .input {
    border-radius: 6px 6px 0 0;
  }

  .below .sign {
    width: auto;
    min-height: 28px;
    border-left: 1px solid var(--border);
    border-top: 0;
    border-radius: 0 0 6px 6px;
  }
</style>
