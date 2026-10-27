//! The engine compiled to WebAssembly, for checking that it deals exactly the same rounds as natively and in C#
//! (same checksums as `EngineTest.exe bench`) and for measuring its speed. Only numbers cross the boundary, so no
//! JavaScript glue is needed; run.mjs loads it in Node.

use calcbj_engine::*;
use std::cell::RefCell;

thread_local! {
    static PLAN: RefCell<Option<SimPlan>> = const { RefCell::new(None) };
}

fn game(decks: i32, h17: bool, das: bool, surrender: SurrenderRule, max_split: i32) -> Rules {
    Rules { decks, h17, das, surrender, max_split_hands: max_split, ..Rules::default() }
}

/// The benchmark games of `EngineTest.exe bench`.
fn setup(index: u32) -> (Rules, CountSystem, StrategyConfig, i32) {
    let h17 = game(6, true, true, SurrenderRule::Late, 4);
    let hi_lo = CountSystem::default();
    let i18 = StrategyConfig { mode: IndexMode::I18Fab4, ..StrategyConfig::default() };
    let full = StrategyConfig { mode: IndexMode::FullIndices, ..StrategyConfig::default() };
    match index {
        0 => (h17, hi_lo, i18, 1),
        1 => (h17, hi_lo, i18, 3),
        2 => (h17, hi_lo, i18, 7),
        3 => (h17, hi_lo, full, 3),
        4 => {
            let ko = CountSystem::presets()[10].clone();
            let sc = StrategyConfig { mode: IndexMode::I18Fab4, index_min: -40, index_max: 30, ..StrategyConfig::default() };
            (game(2, true, true, SurrenderRule::None, 4), ko, sc, 2)
        }
        _ => (game(1, true, false, SurrenderRule::None, 2), hi_lo, full, 1),
    }
}

/// Builds the strategy table and simulation plan of benchmark game `index`.
#[unsafe(no_mangle)]
pub extern "C" fn prepare(index: u32) {
    let (r, cs, sc, spots) = setup(index);
    let pt = strategy::build(&r, &cs, &sc);
    PLAN.with(|p| *p.borrow_mut() = Some(SimPlan::new(&r, &cs, &pt, spots)));
}

/// Simulates `rounds` rounds on one worker, exactly as `simulate(plan, rounds, 1, seed)` does natively, and returns
/// the benchmark checksum.
#[unsafe(no_mangle)]
pub extern "C" fn run(rounds: u32, seed: u32) -> u64 {
    PLAN.with(|p| {
        let plan = p.borrow();
        let plan = plan.as_ref().expect("prepare() first");
        let mut w = SimWorker::new(plan, sim::worker_seed(seed as u64, 0));
        w.shuffle();
        let mut d = SimData::create(plan.spots);
        let mut left = rounds as i64;
        while left > 0 {
            let k = left.min(sim::CHUNK);
            w.run(k);
            d.add_from(&w);
            w.reset();
            left -= k;
        }
        let mut sum: i64 = 0;
        for i in 0..d.num_bins {
            sum = sum
                .wrapping_mul(31)
                .wrapping_add(d.n[i].wrapping_mul(7))
                .wrapping_add(d.sa[i].wrapping_mul(3))
                .wrapping_add(d.qa[i])
                .wrapping_add(d.ab[i]);
        }
        sum as u64
    })
}
