//! CalcBJ engine: combinatorial analysis, index generation, simulation and bet-ramp analysis. The apps run it as
//! WebAssembly (`web/engine-wasm`).
//!
//! Ranks are 1..=10 everywhere (1 = ace, 10 = any ten-value card). The engine started as a line-by-line translation of
//! the C# engine of CalcBJ's first version (retired in November 2026; in the git history up to commit `d24b7e2`), and
//! its modules follow those files: [`types`] (Types.cs), [`ca`] (Combinatorial.cs and DealerDag.cs), [`strategy`]
//! (Strategy.cs), [`sim`] (Simulator.cs) and [`analysis`] (Analysis.cs); "C#:" in a comment names the original. The
//! tests compare every result with the fixtures in `tests/fixtures`, which `examples/fixtures.rs` writes.

/// Which simulation results this engine gives. The apps keep results per settings under a key that ends with this
/// version (`#results=2`), treat results stored under an older one as outdated and start a new run from zero instead of
/// adding to them. Raise it with every change that alters what a simulation of the same settings returns (strategy
/// tables, the simulator); `tests/results_version.rs` fails when the fixtures' results change, as the reminder. Keys
/// without a version (stored by CalcBJ up to 0.1.6) count as older; version 2 is the engine since 0.1.4's exact basic
/// strategy.
pub const RESULTS_VERSION: u32 = 2;

pub mod analysis;
pub mod ca;
mod dealer;
mod num;
pub mod sim;
pub mod strategy;
pub mod types;

pub use analysis::{AnalysisResult, BetRamp, BinStat, OptimizerSettings};
pub use ca::{CaEngine, CaResult, ExactAdjust, basic_for, effects_of_removal, flag, full_shoe, house_edge_off_top};
pub use dealer::{dealer_outcomes, dealer_states};
pub use num::{dotnet_fixed, dotnet_general};
#[cfg(not(target_arch = "wasm32"))]
pub use sim::simulate;
pub use sim::{BinLookup, SimData, SimPlan, SimWorker};
pub use strategy::{IndexInfo, PlayTable};
pub use types::{
    CountMetric, CountSystem, DeckEstimation, DoubleRule, HandKind, HoleCardRule, IndexMode, IndexOverride, PlayKind,
    Rules, StrategyConfig, SurrenderRule, TcRounding, cards,
};
