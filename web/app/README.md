# CalcBJ web, desktop and phone apps

A Svelte interface for the Rust engine (`../engine`), which runs as WebAssembly. The same pages ship three ways:

* **Desktop app** for Mac and Windows (`src-tauri`): a normal app with an installer, everything inside it, no
  internet needed. See [Desktop app](#desktop-app).
* **Phone app** for Android (also `src-tauri`), an APK built and tested by GitHub Actions; an iPhone app is prepared.
  See [Phone apps](#phone-apps).
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

The apps' builds have scripts of their own, which the Tauri CLI runs (`beforeDevCommand` and `beforeBuildCommand` in
`src-tauri`): `dev:desktop` and `build:desktop`, `dev:android` and `build:android`, `dev:ios` and `build:ios`. Each is
the Vite mode of the same name (`vite.config.ts`), which sets `__DESKTOP__`, or `__MOBILE__` with `__ANDROID__` or
`__IOS__` (`src/vite-env.d.ts`; `src/lib/platform.ts` derives `nativeApp` and `touchScreen` from them). The apps'
builds have no service worker, since their files are inside them.

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
files. In the browser, saving downloads the file. The apps use their own Rust commands (`save_scenario` and
`open_scenario`, called from `src/lib/desktop.ts`): the system's Save and Open dialogs on the desktop; on Android the
system's "Save to" screen (any folder and name) and its file picker; on the iPhone the app's Documents folder, which
the Files app shows as On My iPhone › CalcBJ, and the Files picker for opening.

`tests/fixtures/windows-sample.bjev` was saved by the Windows app. `tools\BjevTool.cs` in the repository writes such a
sample (`BjevTool write <file>`) and prints what the Windows app reads from a file (`BjevTool read <file>`), which is
how files saved here are checked; build it with all `src\**\*.cs` files and `/main:BjevTool`. On the dev server,
`window.calcbj` has `scenarioText()` and `openScenarioText(text, name)` for tests without the file picker.

## Installing and offline use

This is about the web app; the desktop and phone apps have their files inside. The built web app has a web manifest
and a service worker (`sw.js`, written by `vite.config.ts`). The first visit caches
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
| `src/lib/platform.ts` | where CalcBJ runs: the desktop or a phone app (`nativeApp`), a touch screen (`touchScreen`) |
| `src/lib/insets.ts`, `src/lib/back.ts` | the phones' safe areas (CSS variables `--safe-*`), and back navigation (the Android back button) through history entries |
| `src/lib/desktop.ts`, `src-tauri/` | the desktop and phone apps: their window, installers, the Android activity (`src-tauri/android`) and the file commands (`src-tauri/src/lib.rs`) |
| `scripts/android-project.mjs`, `scripts/android-smoke.mjs` | creating the Android project, and the emulator test |
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
`src-tauri/src/lib.rs`, called from `src/lib/desktop.ts`) and has no service worker. The engine and the simulation
workers are the same WebAssembly as in the browser. The same project builds the phone apps ([below](#phone-apps)); the
desktop app keeps its identifier `com.calcbj.desktop`, and with it its data folder.

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

## Phone apps

The Android app and the (prepared) iPhone app are the same `src-tauri` project, built with Tauri 2's mobile commands.
For them Tauri merges `src-tauri/tauri.android.conf.json` or `tauri.ios.conf.json` over `tauri.conf.json`: their own
identifier `com.calcbj.app` (the desktop app keeps `com.calcbj.desktop`), the pages built in `android` or `ios` mode,
and for the iPhone iOS 16 or later. `src-tauri/Info.ios.plist` adds to the iPhone app's Info.plist (Documents shown in
the Files app, the export-compliance answer for TestFlight). The `ios` build targets Safari 16. For
`tauri android dev` on a phone, the dev server listens on the address in `TAURI_DEV_HOST`, which the Tauri CLI sets.

The native projects in `src-tauri/gen/android` and `gen/apple` are Tauri's templates and are not committed: they are
made again with `tauri android init` and `tauri ios init`. CalcBJ's own native code is
`src-tauri/android/MainActivity.kt` (system bar colours, room for the keyboard, and `window.CalcBJInsets`, which gives
the page the system bars' size for the safe areas, as older Android System WebViews report 0 there).
`node scripts/android-project.mjs` makes the Android project when it is missing and then copies that activity and the
launcher icons into it (from `public/icons/icon-maskable-512.png`, through `tauri icon` into a scratch folder, so that
`src-tauri/icons` stays as it is); running it again is safe. After a change of the identifier or a Tauri CLI upgrade,
delete `src-tauri/gen/android` and run it again.

### On GitHub Actions

`.github/workflows/phone.yml` (when it runs, installing and the signing secrets are in the repository's README, Phone
apps):

1. **Android build** on Ubuntu 24.04: Java 21, Node 24, Rust with the Android targets, and NDK r29 (r28 or later
   links the native library for 16 KB memory pages); `npm ci`, `node scripts/android-project.mjs`, then
   * `npm run tauri -- android build --ci --debug --apk --target x86_64`: a debug APK for the emulator (only debug
     builds let the WebView be inspected), at
     `src-tauri/gen/android/app/build/outputs/apk/universal/debug/app-universal-debug.apk`;
   * `npm run tauri -- android build --ci --apk --target aarch64`: the release APK for phones, at
     `…/apk/universal/release/app-universal-release-unsigned.apk`. The workflow signs it with zipalign and apksigner
     from the secrets and checks it: 16 KB alignment (zipalign, and every native library's LOAD segments), arm64-v8a
     code only, package name, versionCode and versionName (aapt2). Without the secrets it stays unsigned, with a
     warning.
2. **Emulator test**: Android 16 (API 36, Google APIs, x86_64) with KVM, where `scripts/android-smoke.mjs` installs the
   debug APK and drives the app through adb and the WebView's DevTools protocol. (Not Android 17: on GitHub's runners
   its emulator image restarts its system server soon after booting.) The test waits until the package service answers
   several times in a row before it installs, and tries the install up to three times. Must pass: no errors at startup;
   a house edge on Game rules (the engine works); the phone layout (bottom tabs, no "F5"); all nine pages open; a
   10M-round simulation finishes and shows results; back closes the More sheet, then returns to Game rules, then leaves
   the app; settings and results survive force-stop and restart; a run interrupted by Home and force-stop keeps a
   checkpoint in `calcbj.sims.v1`; no crash. Best effort (warnings only): the safe areas clear the system bars, a text
   field near the bottom stays visible above the keyboard, Save scenario writes `scenario.bjev` to Downloads through the
   system's screen and Open scenario reads it back, no page errors after startup. Screenshots, logcat, screen dumps,
   `results.json` and `summary.md` are in the `android-smoke-test` artifact; the summary (with the WebView version) is
   also on the run's page.
3. **Release**, after the desktop workflow on `main`: the signed APK is attached to the release `v<version>`.
4. **iPhone**, only by hand and not tested yet: `tauri ios init`, the icons, then an unsigned IPA (artifact) or a
   TestFlight upload.

The Android version: versionName is `version` in `src-tauri/tauri.conf.json`, and versionCode is major × 1,000,000 +
minor × 1,000 + patch (0.1.2 gives 1002), so a raised version installs as an update.

### Building for Android on your own computer

Needs a JDK 17 or later, the Android SDK with platform 37, the NDK r28 or later (Android Studio installs all three)
and the environment variables `JAVA_HOME`, `ANDROID_HOME` and `NDK_HOME`, plus the Rust targets
(`rustup target add aarch64-linux-android x86_64-linux-android`). This PC has none of that, which is why GitHub
builds the APK. Then, in `web/app`:

```
node scripts/android-project.mjs                        # the Android project, with CalcBJ's activity and icons
npm run tauri -- android dev                            # on a connected phone or a running emulator
npm run tauri -- android build --apk --target aarch64   # a release APK (unsigned; CI signs it)
npm run tauri -- android build --debug --apk --target x86_64
node scripts/android-smoke.mjs --apk src-tauri/gen/android/app/build/outputs/apk/universal/debug --out smoke
```

The last line runs the emulator test against a debug build on the one device adb sees (`ANDROID_SERIAL` picks one of
several); it clears the app's data there first. The iPhone app needs a Mac with Xcode: `npm run tauri -- ios init`,
then `npm run tauri -- ios dev`.

## Publishing the web app

`dist/` is plain static files with relative paths, so any static host or folder works; installing and offline use
need https. Serving `.wasm` files as `application/wasm` lets the engine compile while it downloads; without it the
app still loads.
