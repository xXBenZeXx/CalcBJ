# CalcBJ — Blackjack Advantage Calculator

A Windows desktop tool for card counters: enter the game, pick a count and bet ramp, and get win rate,
standard deviation, SCORE, N0, risk of ruin, bankroll requirements and optimal bets.

**Run:** `dist\CalcBJ.exe` (single file, ~230 KB). Needs only the .NET Framework 4.8 that ships with Windows 10/11.

**Mac and Windows desktop app:** the installers (`.dmg` for Mac, `-setup.exe` for Windows) are on the GitHub
repository's Releases page; see [Web and desktop version](#web-and-desktop-version).

## What it does

| Page | |
|---|---|
| Game rules | Decks, penetration, H17/S17, peek/ENHC/OBO, BJ payout, doubling, DAS, splits, RSA/HSA, surrender, spots, rounds per hour. The basic-strategy house edge and the effect of every single rule change are computed instantly by combinatorial analysis. |
| Count & strategy | 13 built-in counts (Hi-Lo, KO, Hi-Opt I/II, Omega II, Zen, Wong Halves, Uston APC/SS, RPC, Canfield, Silver Fox, Red Seven) or your own tags, including separate red-card tags. True-count method (deck estimation, floor/truncate/round) or running-count play. Index plays: basic, Illustrious 18, I18 + Fab 4, or a full index set — **generated for your exact count and rules**, plus your own overrides. Colour strategy chart viewable at any count. |
| Simulation | Multi-threaded shoe simulator (≈160M heads-up / 65M three-spot rounds/s on a Ryzen 7 9800X3D). Stores per-count EV, variance and two-seat covariance. Runs only when you press Run (F5) unless you tick "Re-run automatically"; results are kept per configuration, so returning to settings you already ran is instant and Run adds rounds to them. |
| Bet ramp & results | Edit units and hands per count (0 = wong out). Win rate, SD, edge, SCORE, DI, N0, ROR, bankroll for 5%/13.5%/1% ROR, Kelly unit, certainty equivalent — all instant. Presets, linear ramps and an optimiser (max SCORE within a spread, or Kelly for your bankroll, optional wonging and multiple hands). |
| Bankroll & risk | Lifetime and trip risk of ruin, chance of a losing trip, percentile outcomes, ROR curve, result bands over time. |
| Charts | Count frequencies vs. advantage, bets vs. advantage, contribution by count, result distribution. |
| Sensitivity sweep | Re-simulate across penetration, decks, spots or index set; or the optimal SCORE for each max bet. For running and unbalanced counts (KO, Red Seven, Uston SS), whose values mean something else with another number of decks, each deck count gets its own index range and ramp (the preset again, or your ramp mapped by Hi-Lo equivalent). |
| Compare games | Snapshot scenarios and compare them side by side (best value highlighted). |

Scenarios save to `.bjev` files (including simulation results); reports export to HTML. Shortcuts: F5 run, Esc stop, Ctrl+S, Ctrl+O.

## How it works

* **Combinatorial engine** (`src/Engine/Combinatorial.cs`, `DealerDag.cs`): exact dealer probabilities with card
  removal for every player two-card hand and upcard (the dealer's draws are enumerated as multisets, so this is fast:
  ~1–3 ms per full analysis), optimal hit/stand/double/split (with resplits)/surrender, total-dependent decisions.
  The house edge off the top then plays that strategy exactly: hits and doubles with full card removal for every
  hand (splits keep the analysis's approximation).
* **Index generation** (`src/Engine/Strategy.cs`): a play's index is the count at which the player decides, which
  includes the player's cards and the dealer's upcard. So for each count value and each hand, those cards are taken
  out of the shoe and the rest is estimated by linear regression on the tags, to the composition whose count (with
  the cards on the table) is that value. The number of unseen cards and their average count in each count bin come
  from the distribution of the count over the shoe. The full analysis of that composition gives the best play at
  that count. For Hi-Lo, 6 decks S17, all 22 I18 + Fab 4 indices are within one point of the published numbers, which
  are for unfloored true counts. The simulator floors (TC 0 means 0 to 0.99), so 16 v T, for example, stands from +1.
  Simulating 18 such plays count by count in 1, 2 and 6 decks gives the generated decision at every count but one
  borderline one (a 0.05% difference).
* **Simulator** (`src/Engine/Simulator.cs`): real shoes, all spots, peek/no-peek, insurance, surrender, splits; every
  decision uses the count at that moment. Results are tallied exactly per count bin, so bet ramps never need re-simulation.
* **Analysis** (`src/Engine/Analysis.cs`): ramp evaluation, multi-hand covariance, ROR (exponential and trip/reflection
  formulas), Kelly/SCORE optimiser.

Validation (`tools/EngineTest.cs`, which exits with code 1 when a check fails): the house edge for 8D, 6D S17 and
6D H17 (DAS, no surrender) is 0.435%, 0.410% and 0.622%, within 0.03% of published figures (`ca`). For 1 to 8
decks and the other rule variants, 200 million shuffle-every-round rounds agree with the off-the-top analysis
within 2 SE (`offtop`).

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Uses `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` (C# 5, no SDK needed; the simulator needs `/unsafe`).
Output: `dist\CalcBJ.exe`. If a copy is running, the build renames it to `CalcBJ.old.exe` and writes the new EXE beside it.

## Performance notes

* The simulator's hot loop deals lazily (each card is a single Fisher–Yates step, so only dealt cards cost a random
  number), keeps rank and tag packed in one value per card, skips the count lookup for decisions without index plays,
  and uses pinned pointers. What remains is mostly unpredictable game branching (hit/stand, busts, dealer draws).
* The same engine on the .NET 8 runtime runs about 8–12% faster (`dotnet tools\bin\EngineTest.exe bench` with a
  runtimeconfig.json); building natively for .NET 8 needs the .NET 8 SDK.
* Pages use small custom layout containers (`ArrangePanel`, `VStack`, `FieldGrid`, `TileGrid`) that measure every
  control once; nested auto-size TableLayoutPanels made opening the window take ~6 s.

Tests: `tools\EngineTest.cs` (engine checks: `ca` and `offtop` fail with exit code 1, `idx`, `sim`, `misc` print) and `tools\UiTest.cs`
(drives the real UI in-process; compile with all `src` files and `/main:UiTest`).

## Web and desktop version

For Mac (and Windows), CalcBJ is also a desktop app that needs no internet, and for phones a web app that can be
installed from the browser and then works offline. Both use the engine in Rust compiled to WebAssembly and the
interface in TypeScript and Svelte; the desktop app wraps them with Tauri, and GitHub Actions builds its installers.

* `web\engine`: the engine port, bit-for-bit the same results as the C# engine (checked against fixtures written by
  `tools\Oracle.cs`). See `web\engine\README.md`.
* `web\engine-wasm`: its WebAssembly interface.
* `web\app`: the interface, with all nine pages. Simulations run on every CPU thread in Web Workers. It opens and
  saves the same `.bjev` files as this app (`tools\BjevTool.cs` checks them); the HTML report is not ported. Run it
  with `npm install` and `npm run dev` in `web\app`; see `web\app\README.md`.
* `web\app\src-tauri`: the desktop app (Mac `.dmg`, Windows installer), built by `.github\workflows\desktop.yml`.
