# CalcBJ — Blackjack Advantage Calculator

A tool for card counters: enter the game, pick a count and bet ramp, and get win rate, standard deviation, SCORE, N0,
risk of ruin, bankroll requirements and optimal bets. Everything is computed on the device; no internet needed.

**Desktop app for Windows, Mac and Linux:** the installers (`-setup.exe` for Windows, `.dmg` for Mac, `.AppImage`,
`.deb` or `.rpm` for Linux) are on the GitHub repository's Releases page; see [Apps](#apps).

**Android app:** `CalcBJ_<version>_android-arm64.apk` on the same Releases page; see [Phone apps](#phone-apps).

## What it does

| Page | |
|---|---|
| Game rules | Decks, penetration, H17/S17, peek/ENHC/OBO, BJ payout, doubling, DAS, splits, RSA/HSA, surrender, spots, rounds per hour. The basic-strategy house edge and the effect of every single rule change are computed instantly by combinatorial analysis. |
| Count & strategy | 13 built-in counts (Hi-Lo, KO, Hi-Opt I/II, Omega II, Zen, Wong Halves, Uston APC/SS, RPC, Canfield, Silver Fox, Red Seven) or your own tags, including separate red-card tags. True-count method (deck estimation, floor/truncate/round) or running-count play. Index plays: basic, Illustrious 18, I18 + Fab 4, or a full index set — **generated for your exact count and rules**, plus your own overrides. Colour strategy chart viewable at any count. |
| Simulation | Shoe simulator on every CPU thread (≈80M three-spot rounds/s on a Ryzen 7 9800X3D). Stores per-count EV, variance and two-seat covariance. Runs only when you press Run (F5); results are kept per configuration, so returning to settings you already ran is instant and Run adds rounds to them. |
| Bet ramp & results | Edit units and hands per count (0 = wong out). Win rate, SD, edge, SCORE, DI, N0, ROR, bankroll for 5%/13.5%/1% ROR, Kelly unit, certainty equivalent — all instant. Presets, linear ramps and an optimiser (max SCORE within a spread, or Kelly for your bankroll, optional wonging and multiple hands). |
| Bankroll & risk | Lifetime and trip risk of ruin, chance of a losing trip, percentile outcomes, ROR curve, result bands over time. |
| Charts | Count frequencies vs. advantage, bets vs. advantage, contribution by count, result distribution. |
| Sensitivity sweep | Re-simulate across penetration, decks, spots or index set; or the optimal SCORE for each max bet. For running and unbalanced counts (KO, Red Seven, Uston SS), whose values mean something else with another number of decks, each deck count gets its own index range and ramp (the preset again, or your ramp mapped by Hi-Lo equivalent). |
| Compare games | Snapshot scenarios and compare them side by side (best value highlighted). |

Scenarios save to `.bjev` files (including simulation results and Compare entries). Shortcuts: F5 run, Esc stop, Ctrl+S, Ctrl+O.

## How it works

The engine is Rust (`web/engine`), compiled to WebAssembly for the apps.

* **Combinatorial engine** (`web/engine/src/ca.rs`, `dealer.rs`): exact dealer probabilities with card
  removal for every player two-card hand and upcard (the dealer's draws are enumerated as multisets, so this is fast:
  ~1–3 ms per full analysis), optimal hit/stand/double/split (with resplits)/surrender, total-dependent decisions.
  Basic strategy itself takes every card the player draws out of the shoe too (hits with the best play after them,
  doubles, splits), and in peek games the player's draws know that the dealer has no blackjack, so close one- and
  two-deck plays come out as in the published charts (e.g. double 11 v A with 2 decks S17). Where a total's two-card
  hands double or surrender, its hit/stand fallback comes from the hands of three or more cards that use it (16 v T
  with surrender: stand).
  The house edge off the top then plays that strategy exactly, as the simulator does: hits, doubles and split hands
  with full card removal (a split hand with its own cards and the pair cards out of the shoe; leaving the other split
  hands' cards in makes no measurable difference).
* **Index generation** (`web/engine/src/strategy.rs`): a play's index is the count at which the player decides, which
  includes the player's cards and the dealer's upcard. So for each count value and each hand, those cards are taken
  out of the shoe and the rest is estimated by linear regression of the rank counts on the tag sum (red and black
  cards that count differently included), to the composition whose count (with the cards on the table) is that value. The number of unseen cards and their average count in each count bin come
  from the distribution of the count over the shoe. The full analysis of that composition gives the best play at
  that count. Its draw odds stay as they are after the first three cards (exact removal at every count would be too
  slow), so the change that exact removal makes is worked out off the top and carried over to every count, scaled by
  the cards left. Two more things follow the count: the hands of a total are weighted by how often each comes up at
  that count (with one deck T,2 is nine in ten of the 12s at Hi-Lo +1), and a total's stand-or-hit also counts its
  hands of three or more cards, which the simulator plays by the same decision (16 v T stands from 0, as published).
  Checked against a Monte-Carlo of 180 million one-deck rounds with exact values at the simulator's real decision
  points: on the 91 index lines tested the generated indices earn 0.269 and 0.106 units per 100 rounds in two games,
  where version 0.1.3 earned 0.252 and 0.103 (the best possible: 0.287 and 0.112). For Hi-Lo, 6 decks S17, the I18 + Fab 4 indices
  are within one point of the published numbers, which are for unfloored true counts. The simulator floors (TC 0 means
  0 to 0.99), so 12 v 2, for example, stands from +4.
* **Simulator** (`web/engine/src/sim.rs`): real shoes, all spots, peek/no-peek, insurance, surrender, splits; every
  decision uses the count at that moment. Results are tallied exactly per count bin, so bet ramps never need re-simulation.
  The hot loop deals lazily (each card is a single Fisher–Yates step, so only dealt cards cost a random number), keeps
  rank and tag packed in one value per card and skips the count lookup for decisions without index plays; what remains
  is mostly unpredictable game branching (hit/stand, busts, dealer draws).
* **Analysis** (`web/engine/src/analysis.rs`): ramp evaluation, multi-hand covariance, ROR (exponential and
  trip/reflection formulas), Kelly/SCORE optimiser.

Validation (the engine's tests, `cargo test` in `web/engine`): the house edge for 8D, 6D S17 and 6D H17 (DAS, no
surrender) is 0.432%, 0.406% and 0.618%, within 0.03% of published figures (`tests/ca.rs`). Basic strategy is checked
to be the best there is: changing any one decision of any chart cell gains at most 0.0001% of exact expectation
(`tests/basic.rs`, 12 games in about 16 seconds; over 600 rule sets the largest gain was 0.00007%). For 1 to 8 decks
and the other rule variants, single and double deck with resplits and doubling after splits included, 2 billion
shuffle-every-round rounds per game agree with the off-the-top analysis within 3.5 SE, about 0.009% (`tests/offtop.rs`,
about 2 minutes on 16 threads, so it runs only when asked for; in the last run all 11 games were within 0.006% and
2.3 SE). Random draws of unseen cards agree with the composition the index generation uses at a count, for counts with
separate red tags too (`tests/count_model.rs`). Every other result (analyses, strategy tables, index lists, seeded
simulations, ramp evaluations, the optimiser) is compared with reference results in `web/engine/tests/fixtures`, so
any change to a number shows up. See `web/engine/README.md`.

## Building and testing

The installers and the Android app are built by GitHub Actions (see [Apps](#apps)). On a PC with Rust (with the
`wasm32-unknown-unknown` target) and Node.js:

```
cd web\engine
cargo test                                            # the engine's checks, about 40 seconds
cargo test --test offtop -- --ignored --nocapture     # the long simulations, about 2 minutes
cd ..\app
npm install
npm run dev                                           # the app in the browser at http://localhost:5173
```

CalcBJ started out as a Windows program in C# (.NET Framework) with an engine of its own, which the Rust engine was
translated from line by line and checked against until both gave the same results. That program was retired in
November 2026; its code is in the git history up to commit `d24b7e2`.

## Apps

The desktop app for Windows, Mac and Linux, the Android app and the web app are one program: the engine in Rust
compiled to WebAssembly, and the interface in TypeScript and Svelte. The desktop and phone apps wrap it with Tauri, and
GitHub Actions builds the installers and the APK. In a browser it is a web app that can be installed and then works
offline.

* `web\engine`: the engine. See `web\engine\README.md`.
* `web\engine-wasm`: its WebAssembly interface.
* `web\app`: the interface, with all nine pages. Simulations run on every CPU thread in Web Workers. Run it with
  `npm install` and `npm run dev` in `web\app`; see `web\app\README.md`.
* `web\app\src-tauri`: the desktop app (Windows installer, Mac `.dmg`, Linux `.AppImage`, `.deb` and `.rpm`), built by
  `.github\workflows\desktop.yml`, which also starts the Linux app once on a virtual screen as a test, and the phone
  apps (the Android APK; the iPhone app is prepared), built by `.github\workflows\phone.yml`.

## Phone apps

The Android app is the same interface and engine as the web and desktop apps, wrapped by Tauri 2 into an app of its
own (the same `web\app\src-tauri` project, with the pages built for Android). It works without internet, opens and
saves the same `.bjev` files, and keeps its settings and results on the phone. An iPhone app is prepared in the same
project but not built yet ([below](#iphone-later)).

`.github\workflows\phone.yml` builds it on GitHub's machines (this PC has no Android SDK):

* on every push to a branch other than `main` that changes `web\`: an APK, tested on an Android emulator by
  `web\app\scripts\android-smoke.mjs` (startup without errors, the engine, all nine pages, a short simulation, the back
  button, settings and results kept across a restart, a checkpoint of an interrupted run, scenario files). The run's
  page shows a summary; screenshots and logcat are in its `android-smoke-test` artifact. Nothing is released.
* after every successful "Desktop apps" run on `main`: the same, and then the signed
  `CalcBJ_<version>_android-arm64.apk` is attached to that version's release, next to the desktop installers.
* by hand: Actions → Phone apps → Run workflow.

### Installing on Android

Android 7.0 or later, on a phone with a 64-bit ARM processor (practically every phone of the last years).

1. On the phone, open the repository's Releases page and download `CalcBJ_<version>_android-arm64.apk` from the latest
   release. The repository is private, so sign in to GitHub in the browser first (or use the GitHub app).
2. Open the downloaded file. Android asks whether the browser (or the file manager) may install apps: allow it, go back
   and tap Install. If Play Protect warns about an unknown app, choose More details → Install anyway.

From a PC with Android's platform tools, `adb install -r CalcBJ_<version>_android-arm64.apk` does the same.

**Updates:** the app does not update itself. Install the newer APK over the installed one in the same way; settings,
results and Compare entries stay. That works because every APK is signed with the same key (below). A higher
`version` makes a new release; a new build of the same version replaces the release's APK and installs over the old one
too. Uninstalling the app deletes its data, so save a scenario first to keep it.

### The signing key (once)

Android installs an update only when it is signed with the same key as the app on the phone, so the workflow signs
every APK with one key kept in the repository's secrets. Without them it still builds, but the APK is not signed (phones
won't install it) and is not attached to the release.

This PC has no Java, but Git for Windows comes with OpenSSL, so everything runs in PowerShell. Git puts only its
`cmd` folder on the PATH, so the first line tells this PowerShell window where `openssl` is. The `pkcs12` command
asks for a password twice, at least 6 characters; it protects the file and the key alike:

```powershell
Set-Alias openssl "C:\Program Files\Git\mingw64\bin\openssl.exe"
New-Item -ItemType Directory -Force "$env:USERPROFILE\calcbj-signing" | Out-Null; cd "$env:USERPROFILE\calcbj-signing"
openssl req -x509 -newkey rsa:2048 -sha256 -days 10000 -nodes -keyout key.pem -out cert.pem -subj "/CN=CalcBJ"
openssl pkcs12 -export -inkey key.pem -in cert.pem -name calcbj -out calcbj-release.p12
Remove-Item key.pem, cert.pem
```

(Without Git's OpenSSL, Docker Desktop does it too, once its engine is running:
`docker run --rm -it -v "$env:USERPROFILE\calcbj-signing:/keys" eclipse-temurin:21-jdk keytool -genkeypair -keystore
/keys/calcbj-release.p12 -storetype PKCS12 -alias calcbj -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=CalcBJ"`.)

Then put it into the repository's secrets, in the same folder (the last command asks for the password):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$PWD\calcbj-release.p12")) | gh secret set ANDROID_KEYSTORE_BASE64 --repo xXBenZeXx/CalcBJ
gh secret set ANDROID_KEY_ALIAS --body calcbj --repo xXBenZeXx/CalcBJ
gh secret set ANDROID_KEY_PASSWORD --repo xXBenZeXx/CalcBJ
```

**Back up `calcbj-release.p12` and its password** outside GitHub (a password manager, a USB stick): secrets cannot be
read back. Without the key no update can ever be installed over the app again: every phone would have to uninstall
CalcBJ, losing its settings and results, and install an app signed with a new key. Google's developer verification
(next section) also ties the package name to this key's certificate.

### Google's developer verification (from 2027)

Google is bringing in a rule that apps on certified Android phones (those with Google Play) come from registered
developers. It started on 30 September 2026 in Brazil, Indonesia, Singapore and Thailand, for apps from Google Play and
a few other stores; it is planned for all apps worldwide in 2027, with no published date for Germany or the EU yet.
Until then nothing changes here, and apps already installed reportedly keep working afterwards. Once it applies, a
self-built APK installs only:

* with `adb install` from a PC (always allowed), or
* after the "advanced flow" on the phone (a switch in Developer options, a one-time 24-hour wait, a warning at each
  install), or
* normally, once the package name `com.calcbj.app` is registered with a free **limited distribution** account in
  Google's Android Developer Console: no fee and no ID document, but a Google account with 2-step verification and a
  payments profile (name and address); up to 50 phones, each authorised once with a QR code. Registering asks for the
  SHA-256 fingerprint of the signing certificate, which the workflow's summary shows ("Signing certificate SHA-256").
  These accounts can only register package names that Android has not seen before, so registering early is the safe
  order.

### iPhone (later)

The iPhone app is prepared but not built yet: `phone.yml` has a job for it that runs only by hand (Run workflow,
"iPhone"), on a Mac runner, and it has not been tested. Without a Mac there are three ways onto an iPhone:

* **TestFlight** (iPhone = testflight): needs the Apple Developer Program (99 USD a year; 98.99 € in Germany in the
  Apple Developer app). Register the bundle ID `com.calcbj.app` and create the app in App Store Connect, create a team
  API key with Admin access (Users and Access → Integrations), and add the secrets `APPLE_API_KEY_ID`,
  `APPLE_API_ISSUER` and `APPLE_API_PRIVATE_KEY` (the text of the `.p8` file, which can be downloaded only once) and
  the variables `APPLE_DEVELOPMENT_TEAM` (the Team ID) and `APP_STORE_APP_ID` (the app's Apple ID number in App Store
  Connect). The run uploads the build, which then appears in the TestFlight app. Testing it yourself needs no App
  Review; each build runs for 90 days. (A public App Store listing would risk rejection: the guidelines bar card
  counters.)
* **Unsigned IPA with Sideloadly** (iPhone = unsigned, free): the run's `ios-unsigned-ipa` artifact, signed with a
  free Apple ID and installed over USB by Sideloadly on Windows (which needs iTunes and iCloud from Apple's website).
  The signature lasts 7 days, so it has to be refreshed every week; at most 3 such apps; Developer Mode must be on
  (Settings → Privacy & Security).
* **The web app on GitHub Pages**: with GitHub Pro, Pages also works from this private repository, at no extra cost.
  Publish `web\app\dist` (built with `npm run build`; there is no Pages workflow yet), open the address in Safari and
  choose Share → Add to Home Screen: it then works offline. The site itself is public (anyone with the address can use
  it; the repository stays private), iOS may clear its storage when the phone runs short of space, and saving `.bjev`
  files from a Home Screen app is unreliable.

### Costs

The repository is private, so GitHub counts Actions time against the account's included usage: $12 a month with
GitHub Free and $18 with GitHub Pro, at $0.006 a minute on Linux and $0.062 on a Mac. An Android run (build and
emulator test) should take roughly 20 to 30 Linux minutes (about $0.15), an iPhone run about 10 Mac minutes (about
$0.60); these are estimates until the first runs. Artifacts count towards the account's storage (500 MB with Free,
1 GB with Pro): the workflow keeps them for 14 days, the emulator's debug APK for 3.
