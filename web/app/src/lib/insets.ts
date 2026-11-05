// Safe areas: the edges of the screen under the status bar, a camera cutout, the navigation bar or the iPhone's home
// indicator. app.css sets --safe-top, --safe-right, --safe-bottom and --safe-left to env(safe-area-inset-*), which the
// web view fills in. The Android app draws edge to edge, but older Android System WebView versions report 0 there,
// so its MainActivity (src-tauri/android) hands the real values to the page as window.CalcBJInsets and fires the
// "calcbj-insets" event when they change. Without that bridge (iPhone, desktop, browser) env() stays in charge.

declare global {
  interface Window {
    /** The Android app's insets: get() returns {"top":n,"right":n,"bottom":n,"left":n} in CSS pixels. */
    CalcBJInsets?: { get(): string };
  }
}

const SIDES = ["top", "right", "bottom", "left"] as const;

/** Copies the Android app's insets into the CSS variables; does nothing without its bridge. */
function apply(): void {
  const bridge = window.CalcBJInsets;
  if (!bridge) return;
  let insets: Record<string, unknown>;
  try {
    insets = JSON.parse(bridge.get());
  } catch {
    return; // Keep the values from before (or env()).
  }
  const style = document.documentElement.style;
  for (const side of SIDES) {
    const value = insets?.[side];
    if (typeof value === "number" && Number.isFinite(value) && value >= 0) style.setProperty(`--safe-${side}`, `${value}px`);
  }
}

/** Keeps the safe areas up to date. Called first thing at startup, so a startup error is also clear of the bars. */
export function trackInsets(): void {
  apply();
  // Turning the phone moves the bars; the activity also fires its event when they change in other ways.
  window.addEventListener("resize", apply);
  window.addEventListener("calcbj-insets", apply);
}
