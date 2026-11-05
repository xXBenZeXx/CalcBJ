<script lang="ts">
  import { type ChartSeries, niceStep, ticks } from "./chart";

  /**
   * SVG chart: bars, lines, areas, bands and points over category labels or numeric x values, on one y-axis.
   * Hover, a tap or the arrow keys show every series at the nearest x; "Table" shows the same data as a table.
   */
  let {
    series,
    labels = undefined,
    x = undefined,
    height = 220,
    yFormat,
    xFormat = (v: number) => String(v),
    yMin = undefined,
    yMax = undefined,
    includeZero = true,
    xTitle = "",
    label,
    tableHeader = "Value",
  }: {
    series: ChartSeries[];
    /** Category labels (bar-style x positions); otherwise `x` gives numeric positions, ascending. */
    labels?: string[];
    x?: number[];
    height?: number;
    yFormat: (v: number) => string;
    xFormat?: (v: number) => string;
    yMin?: number;
    yMax?: number;
    includeZero?: boolean;
    xTitle?: string;
    /** What the chart shows (for screen readers). */
    label: string;
    /** Heading of the x column in the table view. */
    tableHeader?: string;
  } = $props();

  let width = $state(0);
  let table = $state(false);
  let hover = $state<number | null>(null);
  /**
   * A finger lifting ends the touch (pointerleave follows pointerup), so on a touch screen a tap pins the readout
   * instead; tapping the same point again, or anywhere outside the chart, clears it.
   */
  let pinned = $state(false);
  /**
   * The touch in progress: the point pinned before it (if any), whether it began on that point, where it began, and
   * whether it has moved on to other points since.
   */
  let touch: { before: number | null; again: boolean; start: number; moved: boolean } | null = null;
  let svg = $state<SVGSVGElement>();

  const M = { l: 54, r: 14, t: 10 };
  let bottom = $derived(26 + (xTitle ? 16 : 0));
  let plotW = $derived(Math.max(10, width - M.l - M.r));
  let plotH = $derived(Math.max(10, height - M.t - bottom));
  let count = $derived(labels ? labels.length : (x?.length ?? 0));
  /** The point the readout is on; a pinned one can be left behind when the data gets shorter. */
  let point = $derived(hover !== null && hover < count ? hover : null);
  let categorical = $derived(!!labels);
  let hasBars = $derived(series.some((s) => s.kind === "bar"));
  let legend = $derived(series.length >= 2);

  let xRange = $derived.by(() => {
    if (categorical || !x || x.length === 0) return [0, 1];
    const lo = x[0];
    const hi = x[x.length - 1];
    return hi > lo ? [lo, hi] : [lo, lo + 1];
  });

  let yDomain = $derived.by(() => {
    let lo = Infinity;
    let hi = -Infinity;
    for (const s of series)
      for (const list of [s.values, s.high ?? []])
        for (const v of list)
          if (v !== null && Number.isFinite(v)) {
            lo = Math.min(lo, v);
            hi = Math.max(hi, v);
          }
    if (includeZero || hasBars) {
      lo = Math.min(lo, 0);
      hi = Math.max(hi, 0);
    }
    if (yMin !== undefined) lo = yMin;
    if (yMax !== undefined) hi = yMax;
    if (!Number.isFinite(lo) || !Number.isFinite(hi)) return { lo: 0, hi: 1, step: 0.25 };
    if (hi <= lo) hi = lo + (Math.abs(lo) || 1);
    const step = niceStep(hi - lo, 4);
    return {
      lo: yMin !== undefined ? yMin : Math.floor(lo / step + 1e-9) * step,
      hi: yMax !== undefined ? yMax : Math.ceil(hi / step - 1e-9) * step,
      step,
    };
  });

  let band = $derived(categorical ? plotW / Math.max(1, count) : 0);
  let barWidth = $derived(Math.max(2, Math.min(24, band * 0.7)));

  function xAt(i: number): number {
    if (categorical) return M.l + (i + 0.5) * band;
    const [lo, hi] = xRange;
    return M.l + (((x?.[i] ?? lo) - lo) / (hi - lo)) * plotW;
  }

  function yAt(v: number): number {
    const { lo, hi } = yDomain;
    return M.t + ((hi - Math.min(hi, Math.max(lo, v))) / (hi - lo)) * plotH;
  }

  let baseline = $derived(yAt(Math.max(yDomain.lo, Math.min(yDomain.hi, 0))));

  /** Line path through the non-null values, broken at gaps. */
  function linePath(values: (number | null)[]): string {
    let d = "";
    let pen = false;
    values.forEach((v, i) => {
      if (v === null || !Number.isFinite(v)) {
        pen = false;
        return;
      }
      d += `${pen ? "L" : "M"}${xAt(i).toFixed(1)},${yAt(v).toFixed(1)}`;
      pen = true;
    });
    return d;
  }

  function areaPath(low: (number | null)[], high: (number | null)[] | null): string {
    const idx = low.map((_, i) => i).filter((i) => low[i] !== null && (high === null || high[i] !== null));
    if (idx.length < 2) return "";
    const top = idx.map((i) => `${xAt(i).toFixed(1)},${yAt((high ?? low)[i] as number).toFixed(1)}`);
    const under = high
      ? [...idx].reverse().map((i) => `${xAt(i).toFixed(1)},${yAt(low[i] as number).toFixed(1)}`)
      : [`${xAt(idx[idx.length - 1]).toFixed(1)},${baseline.toFixed(1)}`, `${xAt(idx[0]).toFixed(1)},${baseline.toFixed(1)}`];
    return `M${top.join("L")}L${under.join("L")}Z`;
  }

  /** Bar from the baseline with a 4px rounded data end. */
  function barPath(i: number, v: number): string {
    const x0 = xAt(i) - barWidth / 2;
    const x1 = x0 + barWidth;
    const y = yAt(v);
    const h = Math.abs(y - baseline);
    if (h < 0.5) return "";
    const r = Math.min(4, barWidth / 2, h);
    if (y < baseline)
      return `M${x0},${baseline}V${y + r}Q${x0},${y} ${x0 + r},${y}H${x1 - r}Q${x1},${y} ${x1},${y + r}V${baseline}Z`;
    return `M${x0},${baseline}V${y - r}Q${x0},${y} ${x0 + r},${y}H${x1 - r}Q${x1},${y} ${x1},${y - r}V${baseline}Z`;
  }

  let yTicks = $derived(ticks(yDomain.lo, yDomain.hi, yDomain.step));

  let xTicks = $derived.by((): { pos: number; text: string }[] => {
    if (categorical && labels) {
      const every = Math.max(1, Math.ceil(count / Math.max(1, Math.floor(plotW / 38))));
      return labels.flatMap((t, i) => (i % every === 0 ? [{ pos: xAt(i), text: t }] : []));
    }
    const [lo, hi] = xRange;
    const step = niceStep(hi - lo, Math.max(2, Math.min(6, Math.floor(plotW / 80))));
    return ticks(lo, hi, step).map((v) => ({ pos: M.l + ((v - lo) / (hi - lo)) * plotW, text: xFormat(v) }));
  });

  function xLabel(i: number): string {
    return labels ? labels[i] : xFormat(x?.[i] ?? 0);
  }

  function valueText(s: ChartSeries, i: number): string | null {
    const f = s.format ?? yFormat;
    const v = s.values[i];
    if (v === null || v === undefined || !Number.isFinite(v)) return null;
    if (s.kind === "band") {
      const h = s.high?.[i];
      return h === null || h === undefined ? null : `${f(v)} to ${f(h)}`;
    }
    return f(v);
  }

  function nearest(px: number): number {
    if (count === 0) return 0;
    if (categorical) return Math.max(0, Math.min(count - 1, Math.floor((px - M.l) / band)));
    let best = 0;
    for (let i = 1; i < count; i++) if (Math.abs(xAt(i) - px) < Math.abs(xAt(best) - px)) best = i;
    return best;
  }

  function pointerX(e: PointerEvent): number {
    return e.clientX - (e.currentTarget as SVGElement).getBoundingClientRect().left;
  }

  function onpointerdown(e: PointerEvent) {
    const px = pointerX(e);
    const i = nearest(px);
    if (e.pointerType === "mouse") {
      pinned = false;
    } else {
      const before = pinned ? point : null;
      // Near the pinned point counts as the same point: on a dense line a point is only a pixel or two wide.
      const again = before !== null && (i === before || (!categorical && Math.abs(px - xAt(before)) <= 12));
      touch = { before, again, start: i, moved: false };
    }
    hover = i;
  }

  function onpointermove(e: PointerEvent) {
    if (e.pointerType === "mouse") pinned = false;
    // A pen hovering over a pinned readout leaves it where it is.
    else if (!touch && pinned) return;
    const i = nearest(pointerX(e));
    if (touch && i !== touch.start) touch.moved = true;
    hover = i;
  }

  function onpointerup() {
    if (!touch) return;
    // Tapping the pinned point again clears it; any other tap, or a drag, pins the readout where it ends.
    if (touch.again && !touch.moved) clear();
    else pinned = true;
    touch = null;
  }

  /** The browser took the touch over to scroll the page: the readout goes back to how it was before. */
  function onpointercancel() {
    if (!touch) return;
    hover = touch.before;
    pinned = touch.before !== null;
    touch = null;
  }

  function onpointerleave() {
    if (!pinned && !touch) hover = null;
  }

  function clear() {
    pinned = false;
    hover = null;
  }

  // A tap anywhere outside the chart clears a pinned readout. Scrolling does not: a touch that scrolls the page ends
  // in pointercancel, not pointerup.
  $effect(() => {
    if (!pinned) return;
    const away = (e: PointerEvent) => {
      if (!svg?.contains(e.target as Node)) clear();
    };
    document.addEventListener("pointerup", away, true);
    return () => document.removeEventListener("pointerup", away, true);
  });

  function onkeydown(e: KeyboardEvent) {
    if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
      e.preventDefault();
      const d = e.key === "ArrowRight" ? 1 : -1;
      hover = point === null ? (d > 0 ? 0 : count - 1) : Math.max(0, Math.min(count - 1, point + d));
    } else if (e.key === "Escape") {
      clear();
    }
  }

  let tipLeft = $derived(point === null ? 0 : xAt(point) + 12 > width - 190 ? Math.max(0, xAt(point) - 202) : xAt(point) + 12);

  /** What a screen reader announces for the point the arrow keys are on. */
  let spoken = $derived.by(() => {
    if (point === null) return "Use the arrow keys to read the values";
    const i = point;
    const parts = series.flatMap((s) => {
      const t = valueText(s, i);
      return t === null ? [] : [`${s.name} ${t}`];
    });
    return `${xLabel(i)}: ${parts.join(", ")}`;
  });
</script>

<div class="chart" bind:clientWidth={width}>
  <div class="head">
    {#if legend}
      <ul class="legend">
        {#each series as s (s.name)}
          <li>
            <span
              class="key {s.kind === 'line' ? 'line' : s.kind === 'point' ? 'dot' : 'box'}"
              style:background={s.color}
              style:opacity={s.kind === "band" || s.kind === "area" ? 0.35 + (s.opacity ?? 0.1) : 1}
            ></span>{s.name}
          </li>
        {/each}
      </ul>
    {:else}
      <span></span>
    {/if}
    <button class="toggle" type="button" onclick={() => (table = !table)}>{table ? "Chart" : "Table"}</button>
  </div>

  {#if table}
    <div class="scroll-x table-view">
      <table class="table">
        <thead>
          <tr>
            <th class="num">{tableHeader}</th>
            {#each series as s (s.name)}<th class="num">{s.name}</th>{/each}
          </tr>
        </thead>
        <tbody>
          {#each Array.from({ length: count }, (_, i) => i) as i (i)}
            <tr>
              <td class="num">{xLabel(i)}</td>
              {#each series as s (s.name)}<td class="num">{valueText(s, i) ?? "—"}</td>{/each}
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {:else if width > 0}
    <div class="plot">
      <svg
        bind:this={svg}
        {width}
        {height}
        role="slider"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={Math.max(0, count - 1)}
        aria-valuenow={point ?? 0}
        aria-valuetext={spoken}
        tabindex="0"
        {onpointerdown}
        {onpointermove}
        {onpointerup}
        {onpointercancel}
        {onpointerleave}
        onblur={clear}
        {onkeydown}
      >
        {#each yTicks as t (t)}
          <line class="grid" x1={M.l} x2={M.l + plotW} y1={yAt(t)} y2={yAt(t)} />
          <text class="tick" x={M.l - 6} y={yAt(t) + 4} text-anchor="end">{yFormat(t)}</text>
        {/each}
        {#each xTicks as t (t.pos)}
          <text class="tick" x={t.pos} y={M.t + plotH + 16} text-anchor="middle">{t.text}</text>
        {/each}
        {#if xTitle}
          <text class="tick" x={M.l + plotW / 2} y={height - 3} text-anchor="middle">{xTitle}</text>
        {/if}

        {#each series as s (s.name)}
          {#if s.kind === "band" || s.kind === "area"}
            <path d={areaPath(s.values, s.kind === "band" ? (s.high ?? null) : null)} fill={s.color} fill-opacity={s.opacity ?? 0.1} />
            {#if s.kind === "area"}<path class="line" d={linePath(s.values)} stroke={s.color} />{/if}
          {:else if s.kind === "bar"}
            {#each s.values as v, i (i)}
              {#if v !== null && Number.isFinite(v)}
                <path d={barPath(i, v)} fill={s.colors?.[i] ?? s.color} class:lifted={point === i} />
              {/if}
            {/each}
          {/if}
        {/each}

        <line class="axis" x1={M.l} x2={M.l + plotW} y1={baseline} y2={baseline} />

        {#if point !== null && !hasBars}
          <line class="crosshair" x1={xAt(point)} x2={xAt(point)} y1={M.t} y2={M.t + plotH} />
        {/if}

        {#each series as s (s.name)}
          {#if s.kind === "line"}
            <path class="line" d={linePath(s.values)} stroke={s.color} />
          {/if}
          {#if s.kind === "point" || (s.kind === "line" && s.markers)}
            {#each s.values as v, i (i)}
              {#if v !== null && Number.isFinite(v)}
                <circle class="dot" cx={xAt(i)} cy={yAt(v)} r="4.5" fill={s.color} />
              {/if}
            {/each}
          {/if}
          {#if point !== null && (s.kind === "line" || s.kind === "area") && !s.markers}
            {@const v = s.values[point]}
            {#if v !== null && Number.isFinite(v)}<circle class="dot" cx={xAt(point)} cy={yAt(v)} r="4.5" fill={s.color} />{/if}
          {/if}
        {/each}

        <rect class="hit" x={M.l} y={M.t} width={plotW} height={plotH} />
      </svg>

      {#if point !== null}
        <div class="tip" style:left="{tipLeft}px" role="status">
          <div class="tip-x">{xLabel(point)}</div>
          {#each series as s (s.name)}
            {@const text = valueText(s, point)}
            {#if text !== null}
              <div class="tip-row">
                <span class="tip-key" style:background={s.colors?.[point] ?? s.color}></span>
                <strong>{text}</strong>
                {#if series.length > 1}<span class="tip-name">{s.name}</span>{/if}
              </div>
            {/if}
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .chart {
    position: relative;
    min-width: 0;
  }

  .head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    min-height: 22px;
  }

  .legend {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 4px 14px;
    color: var(--muted);
    font-size: 12px;
  }

  .legend li {
    display: flex;
    align-items: center;
    gap: 6px;
  }

  .key {
    display: inline-block;
    flex: none;
  }

  .key.box {
    width: 12px;
    height: 12px;
    border-radius: 3px;
  }

  .key.line {
    width: 16px;
    height: 2px;
    border-radius: 1px;
  }

  .key.dot {
    width: 9px;
    height: 9px;
    border-radius: 50%;
  }

  .toggle {
    border: 0;
    background: none;
    color: var(--muted);
    font-size: 12px;
    padding: 2px 4px;
    cursor: pointer;
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  /* A finger needs a bigger target than the small link-like text. */
  @media (pointer: coarse) {
    .toggle {
      min-height: 32px;
      padding: 6px 10px;
    }
  }

  .plot {
    position: relative;
  }

  svg {
    display: block;
    overflow: visible;
    touch-action: pan-y;
    font-variant-numeric: tabular-nums;
  }

  /* A finger moving along the chart reads values; it should not select the axis labels or open a callout. */
  @media (pointer: coarse) {
    svg {
      -webkit-user-select: none;
      user-select: none;
      -webkit-touch-callout: none;
    }
  }

  svg:focus {
    outline: none;
  }

  svg:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }

  .grid {
    stroke: var(--chart-grid);
    stroke-width: 1;
  }

  .axis {
    stroke: var(--chart-axis);
    stroke-width: 1;
  }

  .crosshair {
    stroke: var(--chart-axis);
    stroke-width: 1;
  }

  .tick {
    fill: var(--muted);
    font-size: 11px;
  }

  .line {
    fill: none;
    stroke-width: 2;
    stroke-linejoin: round;
    stroke-linecap: round;
  }

  .dot {
    stroke: var(--card);
    stroke-width: 2;
  }

  .lifted {
    filter: brightness(1.18);
  }

  .hit {
    fill: transparent;
    cursor: crosshair;
  }

  .tip {
    position: absolute;
    top: 6px;
    z-index: 2;
    pointer-events: none;
    min-width: 120px;
    max-width: 200px;
    background: var(--card);
    border: 1px solid var(--border);
    border-radius: 6px;
    box-shadow: 0 4px 14px rgb(0 0 0 / 12%);
    padding: 6px 9px;
    font-size: 12px;
  }

  .tip-x {
    color: var(--muted);
    margin-bottom: 2px;
  }

  .tip-row {
    display: flex;
    align-items: center;
    gap: 6px;
    white-space: nowrap;
  }

  .tip-row strong {
    font-variant-numeric: tabular-nums;
  }

  .tip-key {
    width: 12px;
    height: 2px;
    border-radius: 1px;
    flex: none;
  }

  .tip-name {
    color: var(--muted);
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .table-view {
    max-height: 340px;
    overflow-y: auto;
  }
</style>
