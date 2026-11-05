// Where CalcBJ runs. The build flags come from vite.config.ts: the web app (default mode), the desktop app ("desktop")
// or the phone apps ("android", "ios"); the last three are built by src-tauri.

/** The desktop or a phone app: scenario files go through the app's own Rust commands, and there is no offline cache. */
export const nativeApp = __DESKTOP__ || __MOBILE__;

/**
 * A phone or tablet, the apps or the web app on one: no mouse to hover with and no keyboard, so no shortcut hints.
 * Checked once at startup.
 */
export const touchScreen =
  __MOBILE__ || (typeof matchMedia === "function" && matchMedia("(hover: none) and (pointer: coarse)").matches);

/**
 * The page really runs inside one of the apps: Tauri puts __TAURI_INTERNALS__ into its web view before the page's
 * scripts run. An app build opened in an ordinary browser (for example `vite --mode android` to try the phone layout)
 * has no app around it, so it opens and saves files the web app's way instead.
 *
 * Code that loads the apps' file commands also checks the build flags themselves: Vite writes their values into the
 * code, which lets the web app's build leave that code out; it can't tell that from this constant.
 */
export const insideApp = nativeApp && "__TAURI_INTERNALS__" in window;
