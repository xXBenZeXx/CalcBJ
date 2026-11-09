//! CalcBJ engine: the Rust port of the C# engine in `src/Engine`, for the web version of CalcBJ.
//!
//! Ranks are 1..=10 everywhere (1 = ace, 10 = any ten-value card). The modules follow the C# files:
//! [`types`] (Types.cs), [`ca`] (Combinatorial.cs and DealerDag.cs), [`strategy`] (Strategy.cs),
//! [`sim`] (Simulator.cs) and [`analysis`] (Analysis.cs). Every result matches the C# engine exactly; the tests compare
//! against fixtures written by `tools/Oracle.cs` (see `make-fixtures.ps1`).

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
