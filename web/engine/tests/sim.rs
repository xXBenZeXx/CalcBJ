//! Simulator against the C# engine (fixtures/sim.json): from the same seed a single thread must deal exactly the
//! same rounds, so every tally has to match exactly. Also checks the multi-threaded driver against the
//! combinatorial analysis.

mod common;

use calcbj_engine::*;
use common::*;

#[test]
fn seeded_runs_match_exactly() {
    let fx = fixture("sim.json");
    let mut c = Check::new("simulation");
    for case in list(&fx["cases"]) {
        let label = text(&case["label"]);
        let (r, cs, sc) = (rules(&case["rules"]), count(&case["count"]), config(&case["config"]));
        let spots = int(&case["spots"]) as i32;
        let rounds = int(&case["rounds"]);
        let seed: u64 = text(&case["seed"]).parse().expect("seed");
        let pt = strategy::build(&r, &cs, &sc);
        c.eq(&format!("{label}: strategy hash"), fnv_bytes(&pt.flags), text(&case["flagsHash"]).to_string());
        let plan = SimPlan::new(&r, &cs, &pt, spots);
        let got = simulate(&plan, rounds, 1, seed);
        let want = sim_data(&case["data"]);
        c.eq(&format!("{label}: rounds"), got.rounds, want.rounds);
        c.eq(&format!("{label}: shoes"), got.shoes, want.shoes);
        c.eq(&format!("{label}: reshuffle fallbacks"), got.emergency, want.emergency);
        for (name, g, w) in [
            ("hands", &got.n, &want.n),
            ("sum A", &got.sa, &want.sa),
            ("squares A", &got.qa, &want.qa),
            ("sum B", &got.sb, &want.sb),
            ("squares B", &got.qb, &want.qb),
            ("products", &got.ab, &want.ab),
        ] {
            c.eq(&format!("{label}: {name}"), g, w);
        }
    }
    c.finish();
}

/// Reshuffling after every round makes each round an independent deal from a full shoe, so the flat-bet result has
/// to agree with the combinatorial analysis within the statistical error.
#[test]
fn threaded_simulation_agrees_with_analysis() {
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get()) as i32;
    let r = Rules { h17: false, penetration: 0.0, ..Rules::default() };
    let cs = CountSystem::default();
    let sc = StrategyConfig { mode: IndexMode::BasicOnly, ..StrategyConfig::default() };
    let pt = strategy::build(&r, &cs, &sc);
    let plan = SimPlan::new(&r, &cs, &pt, 1);
    let d = simulate(&plan, 40_000_000, threads, 2024);
    assert_eq!(d.rounds, 40_000_000);
    let flat = analysis::evaluate(&d, &BetRamp::create(0, 0, 1.0), 1.0, 100.0, 1000.0);
    let ca = pt.basic_result.player_ev;
    let z = (flat.ev - ca) / flat.se_ev;
    println!(
        "{}: sim {:.4}% ± {:.4}%, analysis {:.4}%, {:.1} SE, {:.1}M rounds/s on {threads} threads",
        r.describe(),
        flat.ev * 100.0,
        flat.se_ev * 100.0,
        ca * 100.0,
        z,
        d.rounds as f64 / d.seconds / 1e6
    );
    assert!(z.abs() < 4.0, "simulation differs from the analysis by {z:.1} standard errors");
}

#[test]
fn merge_adds_tallies() {
    let r = Rules::default();
    let cs = CountSystem::default();
    let pt = strategy::build(&r, &cs, &StrategyConfig::default());
    let plan = SimPlan::new(&r, &cs, &pt, 2);
    let a = simulate(&plan, 300_000, 1, 1);
    let b = simulate(&plan, 200_000, 1, 2);
    let m = SimData::merge(Some(&a), Some(&b)).expect("merged");
    assert_eq!(m.rounds, 500_000);
    assert_eq!(m.shoes, a.shoes + b.shoes);
    for i in 0..m.num_bins {
        assert_eq!(m.n[i], a.n[i] + b.n[i]);
        assert_eq!(m.ab[i], a.ab[i] + b.ab[i]);
    }
    assert_eq!(SimData::merge(None, Some(&b)).expect("copy").n, b.n);
    assert!(SimData::merge(None, None).is_none());
}
