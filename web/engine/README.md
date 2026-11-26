# CalcBJ engine (Rust)

CalcBJ's engine: combinatorial analysis, index generation, simulation and bet-ramp analysis. The apps (desktop,
Android, web) run it as WebAssembly through `../engine-wasm`.

| File | |
|---|---|
| `src/types.rs` | rules, count systems (13 presets), index settings |
| `src/dealer.rs` | exact dealer probabilities with card removal |
| `src/ca.rs` | combinatorial analysis of one shoe composition (optionally regressed to a count after taking out the cards on the table), basic strategy and off-the-top expectation with exact card removal |
| `src/strategy.rs` | strategy tables per count, generated indices (with each count bin's unseen cards and average count from `bin_moments`), overrides, index list |
| `src/sim.rs` | shoe simulator, count lookup, multi-threaded driver (native only) |
| `src/analysis.rs` | bet-ramp evaluation, ROR, trip ruin, optimiser |
| `src/num.rs` | .NET behaviour that results and texts depend on: `Math.Max`/`Math.Min` ties, round-half-to-even, number formatting |

No dependencies; `serde_json` is only used by the tests. The optional `serde` feature adds JSON conversion of rules,
counts, ramps and results (enum values have the names that `.bjev` files use); `../engine-wasm` uses it to expose the
engine to the app (`../app`) as WebAssembly.

The engine started as a line-by-line translation of the engine of CalcBJ's first version, a Windows program in C#,
and gave exactly its results. That program was retired in November 2026 (its code is in the git history up to commit
`d24b7e2`); comments such as "(C#: BasicFor)" name a function's C# original. `src/num.rs` keeps .NET's rounding and
number formatting, so keys, texts and saved results stay as they were.

## Tests

```
cargo test                                            # about 40 seconds
cargo test --test offtop -- --ignored --nocapture     # the long simulations, about 2 minutes on 16 threads
```

The tests are optimised builds (`[profile.dev]` in `Cargo.toml`), since they simulate millions of rounds.

Checks of the maths:

| Test | |
|---|---|
| `tests/ca.rs`, `known_house_edges` | the house edges of 8D, 6D S17 and 6D H17 within 0.03% of the published figures |
| `tests/basic.rs` | basic strategy's close one- and two-deck plays as an independent exact calculation and the published charts have them; 16 v T with surrender; no single change of any decision gains more than 0.0001% of exact expectation (12 games, about 16 seconds) |
| `tests/count_model.rs` | the composition of the unseen cards at a count (`ca::regress`, used by the index generation) moves with their tag sum as in random draws of unseen cards, for counts with separate red tags too |
| `tests/sim.rs`, `threaded_simulation_agrees_with_analysis` | 40 million shuffle-every-round rounds on all threads against the off-the-top analysis |
| `tests/offtop.rs` (only on request) | 2 billion shuffle-every-round rounds for each of 11 games (1 to 8 decks, peek/ENHC/OBO, LS/ES, RSA/HSA, 6:5, D10-11) against the off-the-top analysis, within 3.5 SE |
| `tests/results_version.rs` | a fingerprint of the fixtures' simulation results (strategy decisions, seeded tallies) for the current `RESULTS_VERSION` (below) |

Reference results: `examples/fixtures.rs` writes the engine's results to `tests/fixtures/*.json`, and the other tests
work everything out again and compare. All values agree bit for bit; the tolerances only allow for another
platform's `exp` and `ln`.

| Fixture | Contents |
|---|---|
| `ca.json` | 18 rule sets: rule texts, cut cards, and the full analysis (flags and per-cell EVs) off the top, for two count-shifted shoes and a depleted shoe; basic strategy with exact card removal; effects of removal; dealer probabilities for every upcard |
| `counts.json` | 18 count systems: tags, scale, IRC, texts, true-count equivalents, shifted shoes, count lookups for three shoes |
| `strategy.json` | 16 strategy tables (basic, I18, I18 + Fab 4, full, KO/Red Seven running counts, custom red tags, overrides, reversed range): every flag at every count, the analysis at every count of the index range, the index list |
| `sim.json` | 16 seeded single-thread simulations (about 10 million rounds, 1–7 spots, peek/ENHC/OBO, ES/LS, reshuffles): every tally must be identical |
| `analysis.json` | per-count statistics, 18 bet-ramp evaluations, 18 optimiser runs, normal/ROR formulas |

After a change that is meant to change results, write the fixtures again and look at what changed before committing:

```
cargo run --example fixtures
git diff --stat tests/fixtures
```

Without such a change the files come out the same. Leave out `--release`: its link-time optimisation works out some
`exp` and `ln` of constants while compiling, a last bit differently from the tests. Each file is written on a thread
of its own, as each test runs on its own thread: the engine keeps some results per thread (dealer probabilities, basic
strategies), and a few values of the analyses at a count come out a last bit differently after other rules were
analysed on the same thread.

## Results version

The apps keep simulation results per settings, under a key that ends with `RESULTS_VERSION` (`src/lib.rs`), for
example `…#spots=3#results=2`. Results stored under an older version (or none: CalcBJ up to 0.1.6) are shown as
outdated, and Run starts those settings again from zero instead of adding rounds to them. So raise `RESULTS_VERSION`
with every change that alters what a simulation of the same settings returns. When the fixtures' strategy decisions or
seeded tallies change, `tests/results_version.rs` fails and says what to set; a change that only moves other numbers
(an analysis's last bits, texts) leaves it alone.

The C# engine wrote the first fixtures. The Rust engine wrote `ca.json`, `counts.json` and `sim.json` byte for byte
the same; in `strategy.json` and `analysis.json` 223 values differ from the C# ones in the last bits (at most 3e-15
relative), from `exp` and `ln` and from that order of the analyses.

## Speed

Rounds per second on the Ryzen 7 9800X3D. Both benchmarks print the same checksum, so native code and WebAssembly deal
exactly the same cards from the same seed.

| Game | Rust native, 1 thread / 16 | Rust as WebAssembly in Node, 1 thread |
|---|---|---|
| 6D H17 LS Hi-Lo I18F4, 1 spot | 23.6M / 206M | 15.1M |
| 6D H17 LS Hi-Lo I18F4, 3 spots | 9.8M / 89M | 8.4M |
| 6D H17 LS Hi-Lo I18F4, 7 spots | 4.7M / 43M | 4.0M |
| 6D H17 LS Hi-Lo full, 3 spots | 9.0M / 85M | 7.7M |
| 2D H17 KO I18F4, 2 spots | 14.2M / 132M | 12.0M |
| 1D H17 Hi-Lo full, 1 spot | 21.8M / 187M | 18.2M |

```
cargo run --release --example bench          # Rust native
```

`..\wasm-bench` builds the engine as a 121 KB WebAssembly module and runs the same benchmark in Node:

```
cd ..\wasm-bench
cargo build --release --target wasm32-unknown-unknown
node run.mjs
```

## Numbers as .NET has them

Keys of settings and saved results, and texts such as index lists, are written as the C# program wrote them, and some
results depend on how .NET breaks ties. The code keeps to that, and changes to it should too (the fixtures show any
difference):

- `dmax`/`dmin` stand for .NET's `Math.Max`/`Math.Min` on doubles (which of two equal values, NaN).
- `round_ties_even` stands for `Math.Round` (halves to even); `f64::round` is the one with halves away from zero
  (`MidpointRounding.AwayFromZero`).
- Keys and texts use `dotnet_general` (`double.ToString()`) and `dotnet_fixed` (formats like `"0.##"`).
