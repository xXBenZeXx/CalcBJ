// Back navigation: the Android back button or gesture, a browser's Back button, a mouse's back button in the desktop
// app. Tauri's Android app hands the back button to the web view's history: one entry back, or out of the app when
// there is no entry before. So CalcBJ keeps a few history entries of its own above the one it was opened in:
//   - one while the page is not the first page (Game rules): back returns there, and the next back leaves the app;
//   - one more for each open layer (the More sheet, a dialog): back closes it.
// A layer that closes in another way (a tap, Esc, a button) takes its entry out again, so that back always does what
// the screen suggests. The URL never changes, and the page is still remembered with the settings.
//
// Adding and rewriting entries happens at once; going back is asynchronous (the popstate event says when it is done),
// so a change that comes after it waits for that event.
import type { PageId } from "./state.svelte";

export interface BackOptions {
  /** The first page. It has no entry of its own: back from there leaves the app. */
  home: PageId;
  /** Every page an entry may name. */
  pages: readonly PageId[];
  /** The page on screen. */
  page(): PageId;
  /** Shows a page that back (or a browser's Forward) leads to. */
  show(page: PageId): void;
}

/** What one of CalcBJ's history entries stands for: the page on screen and the number of layers open over it. */
interface Entry {
  page: PageId;
  layers: number;
}

/** An open layer; `onback` closes it when back takes its entry. */
interface Layer {
  onback: () => void;
}

let options: BackOptions | null = null;
/** The open layers, oldest first. */
const layers: Layer[] = [];
/** CalcBJ's entries in the history (above the one the app was opened in), as of the last change or popstate event. */
let entries: Entry[] = [];
/** While going back: how many of CalcBJ's entries will be left. The popstate event then only confirms it. */
let landing: number | null = null;
let timer: ReturnType<typeof setTimeout> | undefined;
let scheduled = false;

/** Sets up the history for the page restored at startup. Called once, when the interface is on screen. */
export function startBackNavigation(o: BackOptions): void {
  const again = options !== null;
  options = o;
  if (again) return; // The dev server reloaded the interface; the history is still set up.
  // Every page starts at its top (App.svelte), and closing a layer must not scroll the page under it.
  history.scrollRestoration = "manual";
  const here = entryOf(history.state);
  const navigation = performance.getEntriesByType?.("navigation")?.[0] as PerformanceNavigationTiming | undefined;
  // Back or Forward from another site, or across a reload in a browser: this entry says what to show, and nothing is
  // added (an entry added here would bring the user straight back after every Back).
  const page = navigation?.type === "back_forward" ? (here?.page ?? o.home) : o.page();
  if (here && here.layers > 0) {
    // A reload, or Back or Forward, while the More sheet or a dialog was open: the entries below this one are still
    // there, but the layers are gone. sync() below steps back over their entries, as when a layer closes.
    entries = stackOf(here);
  } else if (here) {
    // A reload, or Back or Forward onto one of CalcBJ's entries.
    history.replaceState(page === o.home ? null : stateOf({ page, layers: 0 }), "");
    entries = stackOf({ page, layers: 0 });
  } else {
    // Opened on the page it was left on: back goes to Game rules first.
    if (page !== o.home) history.pushState(stateOf({ page, layers: 0 }), "");
    entries = stackOf({ page, layers: 0 });
  }
  if (page !== o.page()) o.show(page);
  window.addEventListener("popstate", landed);
  sync();
}

/** Tells the history that the screen changed: the page, or (from openLayer) the layers. */
export function historyChanged(): void {
  if (scheduled || !options) return;
  scheduled = true;
  // Once the current event is handled, so that one tap that closes the More sheet and switches page is one change.
  queueMicrotask(() => {
    scheduled = false;
    sync();
  });
}

/** A layer opened: back closes it with `onback`. Returns the function to call when it closes in any other way. */
export function openLayer(onback: () => void): () => void {
  const layer: Layer = { onback };
  layers.push(layer);
  historyChanged();
  return () => {
    const i = layers.indexOf(layer);
    if (i < 0) return; // Back closed it and took its entry already.
    layers.splice(i, 1);
    historyChanged();
  };
}

/** Brings the history in line with the screen. */
function sync(): void {
  if (!options || landing !== null) return; // Going back; landed() carries on.
  const want = stackOf({ page: options.page(), layers: layers.length });
  let keep = 0;
  while (keep < entries.length && keep < want.length && same(entries[keep], want[keep])) keep++;
  const extra = entries.length - keep;
  if (extra === 0 && want.length === keep) return;
  // When entries go and others come, the last one that goes is rewritten instead: choosing a page in the More sheet
  // turns the sheet's entry into the page's, without a step back and a new entry.
  const back = want.length > keep ? extra - 1 : extra;
  if (back > 0) {
    landing = entries.length - back;
    timer = setTimeout(gaveUp, 3000);
    history.go(-back);
    return;
  }
  for (let i = keep; i < want.length; i++) {
    if (i < entries.length) history.replaceState(stateOf(want[i]), "");
    else history.pushState(stateOf(want[i]), "");
  }
  entries = want;
}

/** The popstate event: the history went back or forward, for sync() or for the user. */
function landed(): void {
  if (!options) return;
  const here = entryOf(history.state) ?? { page: options.home, layers: 0 };
  const now = stackOf(here);
  const ours = landing !== null && now.length === landing;
  landing = null;
  clearTimeout(timer);
  entries = now;
  if (!ours) {
    // The user went back (or forward in a browser): show what this entry stands for. Layers over it close. A closed
    // layer can't be opened again, so the entry of one (a browser's Forward) is taken out again by sync() below.
    while (layers.length > here.layers) layers.pop()?.onback();
    if (options.page() !== here.page) options.show(here.page);
  }
  sync();
}

/** No popstate event after going back (the browser did not go): carry on from the entry it shows. */
function gaveUp(): void {
  if (!options) return;
  landing = null;
  entries = stackOf(entryOf(history.state) ?? { page: options.home, layers: 0 });
}

/** CalcBJ's entries in the history up to `top`: the page's entry (if it is not the first page), then the layers'. */
function stackOf(top: Entry): Entry[] {
  const stack: Entry[] = top.page === options?.home ? [] : [{ page: top.page, layers: 0 }];
  for (let n = 1; n <= top.layers; n++) stack.push({ page: top.page, layers: n });
  return stack;
}

function same(a: Entry, b: Entry): boolean {
  return a.page === b.page && a.layers === b.layers;
}

function stateOf(entry: Entry): object {
  return { calcbj: 1, page: entry.page, layers: entry.layers };
}

/** The entry `state` stands for, or null when it is not one of CalcBJ's (such as the one the app was opened in). */
function entryOf(state: unknown): Entry | null {
  const s = state as { calcbj?: unknown; page?: unknown; layers?: unknown } | null;
  const page = s?.page as PageId;
  if (s?.calcbj !== 1 || !options?.pages.includes(page)) return null;
  const n = s.layers;
  return { page, layers: typeof n === "number" && Number.isInteger(n) && n > 0 ? n : 0 };
}
