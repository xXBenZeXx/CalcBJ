<script lang="ts">
  import type { IndexInfo } from "../engine/types";
  import { CHART_UPCARDS, RANK_NAMES, hardCell, pairCell, softCell } from "../text";

  /** Colour-coded chart of hard totals, soft totals or pairs; index plays are flagged and every cell explains itself. */
  let {
    kind,
    codes,
    cellEv,
    indices,
    showIndices,
    atLabel,
  }: {
    kind: "Hard" | "Soft" | "Pair";
    codes: string[];
    cellEv: (number | null)[];
    indices: IndexInfo[];
    showIndices: boolean;
    atLabel: string | null;
  } = $props();

  /**
   * The tapped cell (total × 16 + upcard), whose explanation shows right under this chart: on a phone, below all three
   * charts it would be far off screen. Each chart keeps its own, so opening one never moves the chart being tapped.
   */
  let selected = $state<number | null>(null);

  const EV_NAMES = ["stand", "hit", "double", "surrender", "split"];
  const DESCRIPTIONS: Record<string, string> = {
    H: "Hit",
    S: "Stand",
    D: "Double, otherwise hit",
    Ds: "Double, otherwise stand",
    P: "Split",
    R: "Surrender, otherwise hit",
    Rs: "Surrender, otherwise stand",
    Rp: "Surrender, otherwise split",
  };
  const SHORT_PLAYS: Record<string, string> = {
    Stand: "S",
    Hit: "H",
    Double: "D",
    "No double": "nD",
    Split: "P",
    "No split": "nP",
    Surrender: "R",
    "No surrender": "nR",
  };

  let totals = $derived(
    kind === "Hard"
      ? [5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]
      : kind === "Soft"
        ? [13, 14, 15, 16, 17, 18, 19, 20]
        : [2, 3, 4, 5, 6, 7, 8, 9, 10, 1],
  );

  let marks = $derived.by(() => {
    const m = new Map<number, IndexInfo[]>();
    for (const i of indices) {
      if (i.kind !== kind) continue;
      const key = i.total * 16 + i.upcard;
      m.set(key, [...(m.get(key) ?? []), i]);
    }
    return m;
  });

  function rowLabel(t: number): string {
    if (kind === "Hard") return String(t);
    if (kind === "Soft") return "A," + (t - 11);
    return RANK_NAMES[t] + "," + RANK_NAMES[t];
  }

  function cellOf(t: number, u: number): number {
    return kind === "Hard" ? hardCell(t, u) : kind === "Soft" ? softCell(t, u) : pairCell(t, u);
  }

  /** Pairs that are not split are played like their total. */
  function codeOf(t: number, u: number): string {
    const code = codes[cellOf(t, u)];
    if (kind !== "Pair" || code) return code;
    return codes[t === 1 ? softCell(12, u) : hardCell(2 * t, u)];
  }

  function shortIndex(i: IndexInfo): string {
    const p = SHORT_PLAYS[i.play] ?? "I";
    if (i.condition === "always") return p + "*";
    const v = Math.round(i.threshold);
    return p + (i.atOrAbove ? "≥" : "≤") + (v > 0 ? "+" + v : String(v));
  }

  function details(t: number, u: number, code: string): string {
    const lines = [
      `${rowLabel(t)} vs ${RANK_NAMES[u]} — ${DESCRIPTIONS[code] ?? code}${atLabel ? " at " + atLabel : " (basic strategy)"}`,
    ];
    for (const i of marks.get(t * 16 + u) ?? []) lines.push("Index: " + i.text);
    const cell = cellOf(t, u);
    const evs = EV_NAMES.flatMap((name, k) => {
      const v = cellEv[cell * 5 + k];
      return v === null || v === undefined ? [] : [`${name} ${v >= 0 ? "+" : ""}${v.toFixed(3)}`];
    });
    if (evs.length) lines.push("EV per unit: " + evs.join("   "));
    return lines.join("\n");
  }

  /** Worked out again when the chart changes, so it always matches the cell (another count shown, for example). */
  let selectedText = $derived.by(() => {
    if (selected === null) return "";
    const t = Math.floor(selected / 16);
    const u = selected % 16;
    return totals.includes(t) ? details(t, u, codeOf(t, u)) : "";
  });
</script>

<table class="chart">
  <thead>
    <tr>
      <th>{kind}</th>
      {#each CHART_UPCARDS as u (u)}<th>{RANK_NAMES[u]}</th>{/each}
    </tr>
  </thead>
  <tbody>
    {#each totals as t (t)}
      <tr>
        <th>{rowLabel(t)}</th>
        {#each CHART_UPCARDS as u (u)}
          {@const cell = t * 16 + u}
          {@const code = codeOf(t, u)}
          {@const list = showIndices ? marks.get(cell) : undefined}
          {@const text = details(t, u, code)}
          <td data-action={code.charAt(0) || "-"}>
            <button
              type="button"
              title={text}
              class:selected={selected === cell}
              onclick={() => (selected = selected === cell ? null : cell)}
            >
              <span class="code">{code}</span>
              {#if list}
                <span class="mark">{list.map(shortIndex).join(" ")}</span>
                <span class="flag" aria-hidden="true"></span>
              {/if}
            </button>
          </td>
        {/each}
      </tr>
    {/each}
  </tbody>
</table>
{#if selectedText}<p class="details pre">{selectedText}</p>{/if}

<style>
  .chart {
    border-collapse: separate;
    border-spacing: 1px;
    table-layout: fixed;
    width: 100%;
    max-width: 560px;
    font-variant-numeric: tabular-nums;
  }

  th {
    font-size: 12px;
    font-weight: 600;
    color: var(--muted);
    background: var(--grid-header);
    padding: 3px 2px;
  }

  thead th:first-child,
  tbody th {
    width: 3.4em;
    color: var(--text);
  }

  td {
    padding: 0;
    height: 32px;
    background: var(--cell-hit);
  }

  td[data-action="S"] {
    background: var(--cell-stand);
  }

  td[data-action="D"] {
    background: var(--cell-double);
  }

  td[data-action="P"] {
    background: var(--cell-split);
  }

  td[data-action="R"] {
    background: var(--cell-surrender);
  }

  button {
    position: relative;
    width: 100%;
    height: 100%;
    border: 0;
    padding: 0 1px;
    background: none;
    color: var(--cell-text);
    cursor: pointer;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    line-height: 1.05;
  }

  button.selected {
    outline: 2px solid var(--accent);
    outline-offset: -2px;
  }

  /* Only with a mouse: on a touch screen the outline would stay on the last cell tapped. */
  @media (hover: hover) {
    button:hover {
      outline: 2px solid var(--accent);
      outline-offset: -2px;
    }
  }

  .code {
    font-size: 13px;
    font-weight: 600;
  }

  .mark {
    font-size: 9px;
    color: var(--index-mark);
    font-weight: 600;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .flag {
    position: absolute;
    top: 0;
    right: 0;
    border-style: solid;
    border-width: 0 7px 7px 0;
    border-color: transparent var(--index-mark) transparent transparent;
  }

  .details {
    background: var(--grid-header);
    border-radius: 6px;
    padding: 8px 10px;
    font-size: 12.5px;
  }

  @media (max-width: 560px) {
    .mark {
      display: none;
    }

    td {
      height: 28px;
    }
  }
</style>
