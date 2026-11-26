//! Shuffle-every-round simulations against the off-the-top analysis, for 1 to 8 decks and the other rule variants
//! (peek, ENHC and OBO, surrender, resplit and hit split aces, 6:5, doubling on 10 and 11 only). After every round
//! the shoe is full again, so the simulated flat-bet result has to agree with the analysis's expectation of basic
//! strategy (`basic_result.player_ev`) within the statistical error.
//!
//! 2 billion rounds per game make one standard error 0.0026%, enough to catch the 0.01-0.02% the split approximation
//! used to cost with one or two decks. That takes about 3 minutes for the 11 games on 16 threads, so `cargo test`
//! skips it; run it with
//!
//! ```text
//! cargo test --test offtop -- --ignored --nocapture
//! ```
//!
//! `CALCBJ_OFFTOP_ROUNDS` sets another number of rounds per game.

use calcbj_engine::*;

fn rules(
    decks: i32,
    h17: bool,
    das: bool,
    surrender: SurrenderRule,
    max_split_hands: i32,
    resplit_aces: bool,
) -> Rules {
    Rules { decks, h17, das, surrender, max_split_hands, resplit_aces, ..Rules::default() }
}

#[test]
#[ignore = "takes minutes: cargo test --test offtop -- --ignored --nocapture"]
fn simulations_agree_with_the_analysis_off_the_top() {
    use SurrenderRule::{Early, Late, None};
    let rounds: i64 = std::env::var("CALCBJ_OFFTOP_ROUNDS").ok().and_then(|v| v.parse().ok()).unwrap_or(2_000_000_000);
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get()) as i32;
    let games = [
        ("6D S17 DAS LS SP4", rules(6, false, true, Late, 4, false)),
        ("6D H17 DAS noSurr SP4", rules(6, true, true, None, 4, false)),
        ("1D H17 noDAS noSurr SP2", rules(1, true, false, None, 2, false)),
        ("1D S17 DAS SP4 RSA", rules(1, false, true, None, 4, true)),
        ("1D S17 DAS SP4 RSA HSA", Rules { hit_split_aces: true, ..rules(1, false, true, None, 4, true) }),
        ("2D H17 DAS LS SP4", rules(2, true, true, Late, 4, false)),
        ("2D S17 DAS LS SP4 RSA", rules(2, false, true, Late, 4, true)),
        ("6D S17 DAS ENHC", Rules { hole_card: HoleCardRule::Enhc, ..rules(6, false, true, None, 4, false) }),
        ("8D S17 DAS ES", rules(8, false, true, Early, 4, false)),
        (
            "6D H17 DAS 6:5 D10-11",
            Rules { blackjack_pays: 1.2, double_on: DoubleRule::TenToEleven, ..rules(6, true, true, None, 4, false) },
        ),
        (
            "4D S17 DAS RSA HSA OBO",
            Rules { hit_split_aces: true, hole_card: HoleCardRule::EnhcObo, ..rules(4, false, true, None, 4, true) },
        ),
    ];
    let mut failures = Vec::new();
    for (label, r) in games {
        let r = Rules { penetration: 0.0, ..r };
        let cs = CountSystem::default();
        let sc = StrategyConfig { mode: IndexMode::BasicOnly, ..StrategyConfig::default() };
        let pt = strategy::build(&r, &cs, &sc);
        let d = simulate(&SimPlan::new(&r, &cs, &pt, 1), rounds, threads, 777);
        let flat = analysis::evaluate(&d, &BetRamp::create(0, 0, 1.0), 1.0, 100.0, 1000.0);
        let ca = pt.basic_result.player_ev;
        let z = (flat.ev - ca) / flat.se_ev;
        println!(
            "{label:<26} sim {:8.4}% +- {:.4}%   CA {:8.4}%   diff {:7.4}% ({z:.1} SE)  {:.1}M/s",
            flat.ev * 100.0,
            flat.se_ev * 100.0,
            ca * 100.0,
            (flat.ev - ca) * 100.0,
            d.rounds as f64 / d.seconds / 1e6
        );
        if z.abs() > 3.5 {
            failures.push(format!("{label}: simulation and analysis differ by {z:.1} SE"));
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}
