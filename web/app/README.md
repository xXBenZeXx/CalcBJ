# CalcBJ web and desktop app

A Svelte interface for the Rust engine (`../engine`), which runs as WebAssembly. The same pages ship two ways:

* **Desktop app** for Mac and Windows (`src-tauri`): a normal app with an installer, everything inside it, no
  internet needed. See [Desktop app](#desktop-app).
* **Web app** for any browser, including phones, which can also be installed from the browser and then works
  offline.

Every calculation runs on the device; nothing is sent to a server.

## Running it

```
npm install
npm run dev        # builds the engine, then serves http://localhost:5173
npm run build      # type-checks and builds static files into dist/
npm run preview    # serves dist/ at http://localhost:4173 (with the offline support, unlike npm run dev)
```

`npm run dev` and `npm run build` first build `../engine-wasm` with cargo (Rust with the `wasm32-unknown-unknown`
target) and copy it to `src/lib/engine/calcbj.wasm`.

## Pages

All nine pages of the Windows app, with its features and texts: Game rules, Count & strategy, Simulation, Bet ramp &
results, Bankroll & risk, Charts, Sensitivity sweep, Compare games and Guide. The layout switches to bottom tabs on
phones (the last five pages are under More) and follows the system's light or dark mode. Every chart shows its values
on hover, touch or the arrow keys, and has a Table view.

Not ported: the HTML report.

## Scenario files

Open scenario (Ctrl+O) and Save scenario (Ctrl+S) read and write the Windows app's `.bjev` files, so a scenario moves
between the two apps either way: the settings, the simulation results (a simulation of the same settings counts as
already run) and the Compare entries. The number of CPU threads is a property of the device and is not taken from
files. Saving downloads the file through the browser.

`tests/fixtures/windows-sample.bjev` was saved by the Windows app. `tools\BjevTool.cs` in the repository writes such a
sample (`BjevTool write <file>`) and prints what the Windows app reads from a file (`BjevTool read <file>`), which is
how files saved here are checked; build it with all `src\**\*.cs` files and `/main:BjevTool`. On the dev server,
`window.calcbj` has `scenarioText()` and `openScenarioText(text, name)` for tests without the file picker.

## Installing and offline use

The built app has a web manifest and a service worker (`sw.js`, written by `vite.config.ts`). The first visit caches
every file; after that the app opens without a network, and falls back to the cached copy when the network takes
more than 3 seconds to answer. Install it with "Install app…" in the sidebar or the browser's install button (Chrome
and Edge on Windows, Mac and Android), or in Safari on iPhone and iPad with Share → Add to Home Screen.

Both need https, except on localhost. A new build gets a new cache: the next visit with a network loads it, and the
previous version's files stay cached until the update after that, for windows still open on the old version.

The icons in `public/icons` come from the Windows icon: `tools\bin\MakeIcon.exe --web web\app\public\icons` (the
maskable icon and the Apple touch icon fill the whole square).

## How it fits together

| File | |
|---|---|
| `src/lib/engine/engine.ts` | the engine on the main thread: JSON requests to the WebAssembly module (house edge, strategy tables, charts, ramp evaluation, optimiser, risk formulas) |
| `src/lib/engine/wasm.ts` | the request protocol of `../engine-wasm` |
| `src/lib/engine/sim-worker.ts` | a simulation worker: its own engine instance and shoe |
| `src/lib/simulation.svelte.ts` | one worker per CPU thread; workers take 500,000-round chunks until the target is reached, and Stop keeps the chunks that finished |
| `src/lib/state.svelte.ts` | settings, results kept per configuration, Run and Stop, Compare snapshots; settings and results are saved in the browser |
| `src/lib/sweep.svelte.ts` | the sensitivity sweep: one simulation (or optimiser run) per value; in a decks sweep, running and unbalanced counts get each deck count's own index range and ramp (`rampForRules`) |
| `src/lib/scenario.ts`, `src/lib/files.ts` | the `.bjev` format, and opening and saving files |
| `src/lib/desktop.ts`, `src-tauri/` | the desktop app: its window, installers and file dialogs |
| `src/lib/components/Chart.svelte` | the charts (bars, lines, areas, bands) with the hover readout and the table view |
| `src/pages/` | the pages |
| `vite.config.ts` | the build, and the service worker with the list of files to cache |

Like the Windows app, simulations only run when you press Run (F5); Esc stops. On a Ryzen 7 9800X3D (16 threads),
200 million rounds of 6 decks H17, Hi-Lo with I18 + Fab 4 and 3 spots take 2.5 s (about 80M rounds/s, the
Windows EXE does 65M/s).

## Desktop app

`src-tauri` wraps the pages in a window of their own with [Tauri](https://tauri.app) 2: the system's web view
(WebView2 on Windows, WebKit on Mac) shows `dist/` built with `vite build --mode desktop` (`npm run build:desktop`),
which sets `__DESKTOP__`. That build opens and saves scenario files through the system's dialogs (two commands in
`src-tauri/src/main.rs`, called from `src/lib/desktop.ts`) and has no service worker. The engine and the simulation
workers are the same WebAssembly as in the browser.

The installers are built by GitHub Actions (`.github/workflows/desktop.yml`) on every push to `main` that changes
`web/`, or by hand under Actions → Desktop apps → Run workflow. Both are attached to the release named after the
version in `src-tauri/tauri.conf.json` (the repository's Releases page):

* **Mac**: `CalcBJ_<version>_universal.dmg`, one app for Apple silicon and Intel, macOS 12 or later. It is signed
  ad hoc, not with an Apple developer certificate, so the first launch has to be allowed in System Settings →
  Privacy & Security → Open Anyway.
* **Windows**: `CalcBJ_<version>_x64-setup.exe`, installs for the current user without administrator rights.
  SmartScreen warns about the unsigned installer (More info → Run anyway). It uses the WebView2 runtime that comes
  with Windows 10 and 11.

For a new release, raise `version` in `src-tauri/tauri.conf.json` (otherwise the files of the current release are
replaced). Building locally (`npm run tauri build`) needs the Microsoft C++ Build Tools on Windows or Xcode's
command line tools on a Mac; this PC has neither, which is why GitHub builds them.

The icons in `src-tauri/icons` come from `tools\bin\MakeIcon.exe --mac <file.png>` (the tile on Apple's icon grid)
and `npm run tauri icon <file.png>`, except `icon.ico`, which is the Windows app's `assets\app.ico`.

## Publishing the web app

`dist/` is plain static files with relative paths, so any static host or folder works; installing and offline use
need https. Serving `.wasm` files as `application/wasm` lets the engine compile while it downloads; without it the
app still loads.
