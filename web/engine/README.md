# CalcBJ engine (Rust)

The CalcBJ engine ported to Rust for the web version of CalcBJ (Windows, Mac, iPhone, Android), which compiles it to
WebAssembly. It follows the C# engine in `src/Engine` file by file and gives exactly the same results.

| Rust | C# | |
|---|---|---|
| `src/types.rs` | Types.cs | rules, count systems (13 presets), index settings |
| `src/dealer.rs` | DealerDag.cs | exact dealer probabilities with card removal |
| `src/ca.rs` | Combinatorial.cs | combinatorial analysis of one shoe composition (optionally regressed to a count after taking out the cards on the table), exact off-the-top expectation |
| `src/strategy.rs` | Strategy.cs | strategy tables per count, generated indices (with each count bin's unseen cards and average count from `bin_moments`), overrides, index list |
| `src/sim.rs` | Simulator.cs | shoe simulator, count lookup, multi-threaded driver (native only) |
| `src/analysis.rs` | Analysis.cs | bet-ramp evaluation, ROR, trip ruin, optimiser |
| `src/num.rs` | | .NET behaviour the results depend on: `Math.Max`/`Math.Min` ties, round-half-to-even, number formatting |

No dependencies; `serde_json` is only used by the tests. The optional `serde` feature adds JSON conversion of rules,
counts, ramps and results (enum values use the C# names); `../engine-wasm` uses it to expose the engine to the web app
(`../app`) as WebAssembly.

## Tests against the C# engine

`tools\Oracle.cs` runs the C# engine and writes its results to `tests\fixtures\*.json`. `cargo test` recomputes
everything in Rust and compares:

| Fixture | Contents |
|---|---|
| `ca.json` | 18 rule sets: rule texts, cut cards, and the full analysis (flags and per-cell EVs) off the top, for two count-shifted shoes and a depleted shoe; dealer probabilities for every upcard |
| `counts.json` | 18 count systems: tags, scale, IRC, texts, true-count equivalents, shifted shoes, count lookups for three shoes |
| `strategy.json` | 16 strategy tables (basic, I18, I18 + Fab 4, full, KO/Red Seven running counts, custom red tags, overrides, reversed range): every flag at every count, per-count EVs, the index list |
| `sim.json` | 16 seeded single-thread simulations (about 10 million rounds, 1–7 spots, peek/ENHC/OBO, ES/LS, reshuffles): every tally must be identical |
| `analysis.json` | per-count statistics, 18 bet-ramp evaluations, 18 optimiser runs, normal/ROR formulas |

Everything is bit-for-bit identical, except about ten values that go through `exp`/`ln`; these differ only in the last bit
(at most 6e-17). The tests also check that a 40-million-round threaded simulation agrees with the analysis.

```
cargo test
```

After a change to the C# engine, regenerate the fixtures (uses the C# compiler that ships with Windows):

```
powershell -ExecutionPolicy Bypass -File make-fixtures.ps1
```

## Speed

Rounds per second on the Ryzen 7 9800X3D. The checksum column of both benchmarks is the same, so all three deal
exactly the same cards from the same seed.

| Game | C# (.NET Framework), 1 thread / 16 | Rust native, 1 thread / 16 | Rust as WebAssembly in Node, 1 thread |
|---|---|---|---|
| 6D H17 LS Hi-Lo I18F4, 1 spot | 17.1M / 154M | 23.6M / 206M | 15.1M |
| 6D H17 LS Hi-Lo I18F4, 3 spots | 6.9M / 65M | 9.8M / 89M | 8.4M |
| 6D H17 LS Hi-Lo I18F4, 7 spots | 3.1M / 30M | 4.7M / 43M | 4.0M |
| 6D H17 LS Hi-Lo full, 3 spots | 6.3M / 57M | 9.0M / 85M | 7.7M |
| 2D H17 KO I18F4, 2 spots | 10.4M / 93M | 14.2M / 132M | 12.0M |
| 1D H17 Hi-Lo full, 1 spot | 17.5M / 157M | 21.8M / 187M | 18.2M |

```
cargo run --release --example bench          # Rust native (compare with tools\bin\EngineTest.exe bench)
```

`..\wasm-bench` builds the engine as a 121 KB WebAssembly module and runs the same benchmark in Node:

```
cd ..\wasm-bench
cargo build --release --target wasm32-unknown-unknown
node run.mjs
```

## Keeping the two engines identical

- Port changes in the same order of operations as the C# code; floating-point results depend on it.
- Use `dmax`/`dmin` where C# uses `Math.Max`/`Math.Min` on doubles, and `round_ties_even` where it uses `Math.Round`
  without `MidpointRounding.AwayFromZero` (`f64::round` is the away-from-zero one).
- Keys and texts use `dotnet_general` (`double.ToString()`) and `dotnet_fixed` (formats like `"0.##"`).
