//! Simulation speed: the same games, seeds and single-thread checksum as the WebAssembly benchmark in `../wasm-bench`.
//! Run with `cargo run --release --example bench [threads]`.

use calcbj_engine::*;

fn game(decks: i32, h17: bool, das: bool, surrender: SurrenderRule, max_split: i32) -> Rules {
    Rules { decks, h17, das, surrender, max_split_hands: max_split, ..Rules::default() }
}

fn bench(label: &str, r: &Rules, cs: &CountSystem, sc: &StrategyConfig, spots: i32, rounds: i64, threads: i32) {
    let pt = strategy::build(r, cs, sc);
    let plan = SimPlan::new(r, cs, &pt, spots);
    let d1 = simulate(&plan, 4_000_000, 1, 99);
    let mut sum: i64 = 0;
    for i in 0..d1.num_bins {
        sum = sum
            .wrapping_mul(31)
            .wrapping_add(d1.n[i].wrapping_mul(7))
            .wrapping_add(d1.sa[i].wrapping_mul(3))
            .wrapping_add(d1.qa[i])
            .wrapping_add(d1.ab[i]);
    }
    let rate1 = d1.rounds as f64 / d1.seconds / 1e6;
    let d = simulate(&plan, rounds, threads, 1234);
    println!(
        "{label:<34} 1 thread {rate1:6.2}M/s   {threads} threads {:6.1}M/s   checksum {:016X}",
        d.rounds as f64 / d.seconds / 1e6,
        sum as u64
    );
}

fn main() {
    let threads = std::env::args()
        .nth(1)
        .and_then(|a| a.parse().ok())
        .unwrap_or_else(|| std::thread::available_parallelism().map_or(1, |n| n.get() as i32));
    let h17 = game(6, true, true, SurrenderRule::Late, 4);
    let cs = CountSystem::default();
    let i18 = StrategyConfig { mode: IndexMode::I18Fab4, ..StrategyConfig::default() };
    let full = StrategyConfig { mode: IndexMode::FullIndices, ..StrategyConfig::default() };
    let ko = CountSystem::presets()[10].clone();
    let ko_sc = StrategyConfig { mode: IndexMode::I18Fab4, index_min: -40, index_max: 30, ..StrategyConfig::default() };
    let d2 = game(2, true, true, SurrenderRule::None, 4);
    let d1 = game(1, true, false, SurrenderRule::None, 2);
    bench("6D H17 LS Hi-Lo I18F4, 1 spot", &h17, &cs, &i18, 1, 300_000_000, threads);
    bench("6D H17 LS Hi-Lo I18F4, 3 spots", &h17, &cs, &i18, 3, 200_000_000, threads);
    bench("6D H17 LS Hi-Lo I18F4, 7 spots", &h17, &cs, &i18, 7, 100_000_000, threads);
    bench("6D H17 LS Hi-Lo full, 3 spots", &h17, &cs, &full, 3, 200_000_000, threads);
    bench("2D H17 KO RC I18F4, 2 spots", &d2, &ko, &ko_sc, 2, 200_000_000, threads);
    bench("1D H17 Hi-Lo full, 1 spot", &d1, &cs, &full, 1, 300_000_000, threads);
}
