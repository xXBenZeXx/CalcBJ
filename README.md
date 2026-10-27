# CalcBJ — Blackjack Advantage Calculator

A Windows desktop tool for card counters: enter the game, pick a count and bet ramp, and get win rate,
standard deviation, SCORE, N0, risk of ruin, bankroll requirements and optimal bets.

**Run:** `dist\CalcBJ.exe` (single file, ~210 KB). Needs only the .NET Framework 4.8 that ships with Windows 10/11.

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
| Sensitivity sweep | Re-simulate across penetration, decks, spots or index set; or the optimal SCORE for each max bet. |
| Compare games | Snapshot scenarios and compare them side by side (best value highlighted). |

Scenarios save to `.bjev` files (including simulation results); reports export to HTML. Shortcuts: F5 run, Esc stop, Ctrl+S, Ctrl+O.

## How it works

* **Combinatorial engine** (`src/Engine/Combinatorial.cs`, `DealerDag.cs`): exact dealer probabilities with card
  removal for every player two-card hand and upcard (the dealer's draws are enumerated as multisets, so this is fast:
  ~1–3 ms per full analysis), optimal hit/stand/double/split (with resplits)/surrender, total-dependent decisions.
* **Index generation** (`src/Engine/Strategy.cs`): for each count value the expected composition of the unseen cards is
  estimated by linear regression on the tags, at mid-shoe depth; the full analysis of that composition gives the best
  play at that count. For Hi-Lo this reproduces the published I18/Fab 4 numbers to within a point (about half a point lower
  with floored true counts, which is what the simulator uses).
* **Simulator** (`src/Engine/Simulator.cs`): real shoes, all spots, peek/no-peek, insurance, surrender, splits; every
  decision uses the count at that moment. Results are tallied exactly per count bin, so bet ramps never need re-simulation.
* **Analysis** (`src/Engine/Analysis.cs`): ramp evaluation, multi-hand covariance, ROR (exponential and trip/reflection
  formulas), Kelly/SCORE optimiser.

Validation (see `tools/EngineTest.cs`): 6D S17 DAS −0.40%, 6D H17 DAS −0.62%, 8D −0.43% (matches published figures);
shuffle-every-round simulations agree with the combinatorial engine within ~0.01% for shoe games.

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

Tests: `tools\EngineTest.cs` (engine checks: `ca`, `idx`, `sim`, `offtop`, `misc`) and `tools\UiTest.cs`
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
