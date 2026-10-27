<script lang="ts">
  import { parseNumber } from "../format";

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
  } = $props();

  const show = (v: number) => (format ? format(v) : String(Number(v.toFixed(decimals))));
  let text = $state("");
  let invalid = $state(false);

  $effect(() => {
    text = show(value);
    invalid = false;
  });

  function commit() {
    const v = parseNumber(text);
    if (v === null) {
      invalid = true;
      return;
    }
    const f = 10 ** decimals;
    const c = Math.min(max, Math.max(min, Math.round(v * f) / f));
    invalid = false;
    text = show(c);
    if (c !== value) onchange(c);
  }
</script>

<input
  class="input"
  class:invalid
  type="text"
  inputmode="decimal"
  style:width
  bind:value={text}
  {disabled}
  aria-label={label}
  onchange={commit}
  onkeydown={(e) => e.key === "Enter" && commit()}
/>
