//! Writes the engine's reference results to `tests/fixtures/*.json`, which `cargo test` checks the engine against.
//!
//! `cargo run --example fixtures [folder]` (default: `tests/fixtures`). Run it after a change that is meant to change
//! results, then look at the differences with `git diff` before committing them: every changed number is a result
//! that changed. Without such a change it rewrites the files unchanged.
//!
//! The tests then get bit-for-bit the same numbers, because this computes them the way they do: built with the same
//! settings (not `--release`, whose link-time optimisation works out some `exp` and `ln` of constants while compiling,
//! a last bit differently) and each file on a thread of its own, as each test runs on its own thread. The engine keeps
//! some results per thread (dealer probabilities, basic strategies), and a few strategy table values come out a last
//! bit differently when other rules were analysed first.
//!
//! The files were first written by CalcBJ's original C# engine (`tools/Oracle.cs`, removed in November 2026). This is
//! the same program in Rust: the same cases in the same order and the same JSON layout, with numbers written as .NET's
//! "G17" format writes them (NaN as null, infinities as "Infinity" and "-Infinity").

use calcbj_engine::strategy::{self, CELLS_PER_BIN, NUM_BINS};
use calcbj_engine::*;
use std::path::{Path, PathBuf};

fn main() {
    let dir = std::env::args()
        .nth(1)
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests").join("fixtures"));
    std::fs::create_dir_all(&dir).expect("fixture folder");
    type Write = fn() -> Json;
    let files: [(&str, Write); 5] = [
        ("ca.json", ca_fixture),
        ("counts.json", count_fixture),
        ("strategy.json", strategy_fixture),
        ("sim.json", sim_fixture),
        ("analysis.json", analysis_fixture),
    ];
    for (name, write) in files {
        let json = std::thread::spawn(write).join().expect("fixture thread");
        save(&dir, name, json);
    }
}

fn save(dir: &Path, name: &str, w: Json) {
    let path = dir.join(name);
    std::fs::write(&path, w.text.as_bytes()).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    println!("{name:<14} {:>11} bytes", w.text.len());
}

// ---------------- Inputs ----------------

fn r(decks: i32, h17: bool, das: bool, surrender: SurrenderRule, max_split_hands: i32, resplit_aces: bool) -> Rules {
    Rules { decks, h17, das, surrender, max_split_hands, resplit_aces, ..Rules::default() }
}

fn rules_pen(decks: i32, penetration: f64) -> Rules {
    Rules { decks, penetration, ..Rules::default() }
}

const NO_SURR: SurrenderRule = SurrenderRule::None;
const LATE: SurrenderRule = SurrenderRule::Late;
const EARLY: SurrenderRule = SurrenderRule::Early;

fn rule_cases() -> Vec<(&'static str, Rules)> {
    vec![
        ("8D S17 DAS noSurr SP4", r(8, false, true, NO_SURR, 4, false)),
        ("6D S17 DAS noSurr SP4", r(6, false, true, NO_SURR, 4, false)),
        ("6D H17 DAS noSurr SP4", r(6, true, true, NO_SURR, 4, false)),
        ("6D H17 DAS LS SP4 (defaults)", Rules::default()),
        ("6D S17 DAS LS SP4 RSA", r(6, false, true, LATE, 4, true)),
        ("2D H17 DAS noSurr SP4", r(2, true, true, NO_SURR, 4, false)),
        ("2D S17 DAS D9-11", Rules { double_on: DoubleRule::NineToEleven, ..r(2, false, true, NO_SURR, 4, false) }),
        ("1D H17 noDAS SP2", r(1, true, false, NO_SURR, 2, false)),
        (
            "1D S17 noDAS SP2 D10-11 pen 62.5",
            Rules { double_on: DoubleRule::TenToEleven, penetration: 0.625, ..r(1, false, false, NO_SURR, 2, false) },
        ),
        ("6D S17 DAS ENHC", Rules { hole_card: HoleCardRule::Enhc, ..r(6, false, true, NO_SURR, 4, false) }),
        ("6D S17 DAS ENHC LS", Rules { hole_card: HoleCardRule::Enhc, ..r(6, false, true, LATE, 4, false) }),
        (
            "4D S17 DAS RSA HSA OBO",
            Rules { hit_split_aces: true, hole_card: HoleCardRule::EnhcObo, ..r(4, false, true, NO_SURR, 4, true) },
        ),
        (
            "6D H17 DAS 6:5 D10-11",
            Rules { blackjack_pays: 1.2, double_on: DoubleRule::TenToEleven, ..r(6, true, true, NO_SURR, 4, false) },
        ),
        ("6D S17 DAS ES", r(6, false, true, EARLY, 4, false)),
        (
            "8D H17 ES SP3 7:5 no insurance",
            Rules { blackjack_pays: 1.4, insurance: false, ..r(8, true, false, EARLY, 3, false) },
        ),
        ("6D S17 no splits ES OBO", Rules { hole_card: HoleCardRule::EnhcObo, ..r(6, false, true, EARLY, 1, false) }),
        ("5D H17 LS RSA 2:1 pen 83 no burn", five_deck()),
        (
            "2D H17 LS SP2 BJ 1.25 CSM",
            Rules { penetration: 0.0, blackjack_pays: 1.25, ..r(2, true, true, LATE, 2, false) },
        ),
    ]
}

fn five_deck() -> Rules {
    Rules { penetration: 0.83, burn_card: false, blackjack_pays: 2.0, ..r(5, true, true, LATE, 4, true) }
}

fn preset(name: &str) -> CountSystem {
    CountSystem::presets().into_iter().find(|c| c.name == name).unwrap_or_else(|| panic!("no count named {name}"))
}

fn rc_fractional() -> CountSystem {
    CountSystem {
        name: "Hi-Lo RC, fractional IRC".to_string(),
        metric: CountMetric::RunningCount,
        irc_base: 3.0,
        irc_per_deck: -0.5,
        ..preset("Hi-Lo")
    }
}

fn custom_red() -> CountSystem {
    CountSystem {
        name: "Custom red tags".to_string(),
        red_tags: Some([-1.0, 0.5, 1.0, 1.0, 1.5, 1.0, 1.0, 0.0, 0.0, -1.0]),
        ..preset("Wong Halves")
    }
}

fn omega_exact_round() -> CountSystem {
    CountSystem { deck_est: DeckEstimation::Exact, rounding: TcRounding::Round, ..preset("Omega II") }
}

fn halves_quarter_truncate() -> CountSystem {
    CountSystem { deck_est: DeckEstimation::QuarterDeck, rounding: TcRounding::Truncate, ..preset("Wong Halves") }
}

fn count_cases() -> Vec<CountSystem> {
    let mut list = CountSystem::presets();
    list.push(CountSystem {
        name: "Hi-Lo exact/round".to_string(),
        deck_est: DeckEstimation::Exact,
        rounding: TcRounding::Round,
        ..preset("Hi-Lo")
    });
    list.push(CountSystem { name: "Halves quarter/truncate".to_string(), ..halves_quarter_truncate() });
    list.push(CountSystem {
        name: "Zen full-deck".to_string(),
        deck_est: DeckEstimation::FullDeck,
        ..preset("Zen Count")
    });
    list.push(rc_fractional());
    list.push(custom_red());
    list
}

fn sc(mode: IndexMode, index_min: i32, index_max: i32) -> StrategyConfig {
    StrategyConfig { mode, index_min, index_max, overrides: Vec::new() }
}

fn ov(kind: HandKind, total: i32, upcard: i32, play: PlayKind, at_or_above: bool, index: f64) -> IndexOverride {
    IndexOverride { enabled: true, kind, total, upcard, play, at_or_above, index }
}

fn with_overrides() -> StrategyConfig {
    use HandKind::{Hard, Insurance, Pair, Soft};
    use PlayKind::{Double, Hit, Insure, NoDouble, NoSplit, Split, Stand, Surrender};
    StrategyConfig {
        overrides: vec![
            ov(Hard, 16, 10, Stand, true, 1.0),
            ov(Insurance, 0, 1, Insure, true, 2.5),
            ov(Pair, 10, 6, Split, true, 4.0),
            ov(Soft, 19, 6, Double, true, 1.0),
            ov(Hard, 12, 2, Hit, false, 2.0),
            ov(Hard, 15, 10, Surrender, true, 0.0),
            ov(Pair, 8, 10, NoSplit, true, 8.0),
            ov(Hard, 11, 1, NoDouble, false, -2.0),
            IndexOverride { enabled: false, ..ov(Hard, 13, 2, Hit, true, -1.0) },
            ov(Soft, 10, 5, Double, true, 0.0), // not a soft total: ignored
            ov(Hard, 9, 2, Double, true, 0.7),
        ],
        ..StrategyConfig::default()
    }
}

// ---------------- ca.json ----------------

fn ca_fixture() -> Json {
    let mut w = Json::new();
    let hi_lo = CountSystem::default();
    let omega = preset("Omega II");
    w.obj();
    w.key("cases").arr();
    for (case_no, (label, r)) in rule_cases().into_iter().enumerate() {
        w.obj();
        w.key("label").str(label);
        w.key("rules");
        rules_json(&mut w, &r);
        w.key("key").str(&r.key());
        w.key("describe").str(&r.describe());
        w.key("payoutText").str(&Rules::payout_text(r.blackjack_pays));
        w.key("totalCards").int(r.total_cards() as i64);
        w.key("cutCard").arr();
        for s in 1..=7 {
            w.int(r.cut_card(s) as i64);
        }
        w.end();
        w.key("midDepthDecks").num(strategy::mid_depth_decks(&r));
        let mut dbl = String::new();
        for soft in [false, true] {
            for t in 2..=21 {
                dbl.push(if r.can_double_total(t, soft) { '1' } else { '0' });
            }
        }
        w.key("canDouble").str(&dbl);
        let mut eng = CaEngine::new(&r);
        w.key("offTop");
        ca_json(&mut w, &eng.evaluate(&full_shoe(r.decks as f64)), true);
        w.key("houseEdge").num(house_edge_off_top(&r));
        // Basic strategy with exact card removal, and (for every third rule set: ten exact evaluations each) the
        // effects of removal.
        let (basic, adj, _) = basic_for(&r);
        w.key("basic");
        ca_json(&mut w, &basic, false);
        w.key("basicShifts").nums(&adj.stand_shift).key("basicRatios").nums(&adj.stand_ratio);
        if case_no % 3 == 0 {
            w.key("eor").nums(&effects_of_removal(&r));
        }
        let mut dep = full_shoe(r.decks as f64);
        dep[1] -= 1.0;
        dep[5] -= 3.0;
        dep[6] -= 2.0;
        dep[10] -= 4.0;
        let shoes = [
            ("Hi-Lo +4 at mid depth", strategy::shifted_shoe(&hi_lo, 4.0, strategy::mid_depth_decks(&r))),
            ("Omega II -3 at 40%", strategy::shifted_shoe(&omega, -3.0, r.decks as f64 * 0.4)),
            ("depleted", dep),
        ];
        w.key("shoes").arr();
        for (shoe_label, shoe) in &shoes {
            w.obj().key("label").str(shoe_label).key("shoe").nums(shoe).key("result");
            ca_json(&mut w, &eng.evaluate(shoe), false);
            w.end();
        }
        w.end();
        w.end();
    }
    w.end();

    w.key("dealer").arr();
    for h17 in [false, true] {
        for up in 1..=10usize {
            let mut c6 = full_shoe(6.0);
            c6[up] -= 1.0;
            let d6 = dealer_outcomes(up, h17, &c6);
            let mut c1 = full_shoe(1.0);
            c1[up] -= 1.0;
            c1[10] -= 1.0;
            c1[6] -= 1.0;
            let d1 = dealer_outcomes(up, h17, &c1);
            w.obj().key("up").int(up as i64).key("h17").bool(h17).key("count").int(dealer_states(up, h17) as i64);
            w.key("shoe6").nums(&c6).key("dist6").nums(&d6).key("shoe1").nums(&c1).key("dist1").nums(&d1).end();
        }
    }
    w.end();
    w.end();
    w
}

fn ca_json(w: &mut Json, res: &CaResult, full: bool) {
    w.obj();
    w.key("playerEV").num(res.player_ev);
    w.key("insuranceEV").num(res.insurance_ev);
    w.key("insure").bool(res.insure);
    w.key("flags").str(&hex(&res.flags));
    if full {
        w.key("cellEV").nums(&res.cell_ev);
    } else {
        let (mut sum, mut nan) = (0.0, 0);
        for &v in &res.cell_ev {
            if v.is_nan() {
                nan += 1;
            } else {
                sum += v;
            }
        }
        w.key("cellEVSum").num(sum).key("cellEVNaN").int(nan);
    }
    let mut bj = 0.0;
    for &v in &res.cell_bj {
        bj += v;
    }
    w.key("cellBJSum").num(bj);
    w.end();
}

// ---------------- counts.json ----------------

fn count_fixture() -> Json {
    let mut w = Json::new();
    let rules_list = [Rules::default(), rules_pen(1, 0.625), rules_pen(8, 0.83)];
    w.obj();
    w.key("rules").arr();
    for r in &rules_list {
        rules_json(&mut w, r);
    }
    w.end();
    w.key("systems").arr();
    for cs in count_cases() {
        w.obj();
        w.key("count");
        count_json(&mut w, &cs);
        w.key("scale").int(cs.scale() as i64);
        w.key("imbalance").num(cs.imbalance_per_deck());
        w.key("balanced").bool(cs.is_balanced());
        w.key("level").num(cs.level());
        w.key("irc").arr();
        for d in [1, 2, 5, 6, 8] {
            w.num(cs.irc(d));
        }
        w.end();
        w.key("tagText").str(&cs.tag_text());
        w.key("key").str(&cs.key());
        w.key("tagOfCode").arr();
        for code in 0..20 {
            w.num(cs.tag_of_code(code));
        }
        w.end();
        w.key("rankTag").arr();
        for rank in 1..=10 {
            w.num(cs.rank_tag(rank));
        }
        w.end();
        w.key("representative").arr();
        for v in [-12, -1, 0, 1, 7] {
            w.num(strategy::representative(&cs, v));
        }
        w.end();
        w.key("tcEquivalent").arr();
        for r in &rules_list {
            w.arr();
            for mv in [-10.0, -3.5, 0.0, 2.5, 9.0] {
                w.num(strategy::tc_equivalent(&cs, r, mv));
            }
            w.end();
        }
        w.end();
        w.key("shifted").arr();
        for t in [-6.0, -2.5, 0.0, 1.0, 3.7, 8.0, 25.0] {
            for dr in [0.5, 2.75, 5.0] {
                w.obj().key("tcEq").num(t).key("decks").num(dr);
                w.key("shoe").nums(&strategy::shifted_shoe(&cs, t, dr)).end();
            }
        }
        w.end();
        let scale = cs.scale();
        w.key("metricValue").arr();
        for rc in [-60, -13, -7, -1, 0, 1, 2, 5, 12, 40] {
            for cr in [0, 1, 5, 6, 13, 25, 26, 27, 51, 52, 77, 78, 150, 260, 312] {
                w.arr().int(rc as i64).int(cr as i64).int(sim::metric_value(&cs, scale, rc, cr) as i64).end();
            }
        }
        w.end();
        w.key("binLookups").arr();
        for r in &rules_list {
            let b = sim::build_bins(r, &cs);
            w.obj().key("rcMin").int(b.rc_min as i64).key("rcMax").int(b.rc_max as i64);
            w.key("width").int(b.width as i64).key("rows").int((b.rows.len() / b.width as usize) as i64);
            w.key("crRow").ints(b.cr_row.iter().map(|&x| x as i64));
            w.key("rowsHash").str(&fnv_shorts(&b.rows)).end();
        }
        w.end();
        w.end();
    }
    w.end();
    w.key("estimateDecks").obj();
    for &est in DeckEstimation::ALL {
        w.key(est.name()).arr();
        for cr in 0..=416 {
            w.num(sim::estimate_decks(est, cr));
        }
        w.end();
    }
    w.end();
    w.end();
    w
}

// ---------------- strategy.json ----------------

fn strategy_cases() -> Vec<(&'static str, Rules, CountSystem, StrategyConfig)> {
    use IndexMode::{BasicOnly, FullIndices, I18Fab4, Illustrious18};
    let hi_lo = CountSystem::default;
    let ko = || preset("KO (Knock-Out)");
    vec![
        ("6D S17 DAS LS, Hi-Lo full -10..10", r(6, false, true, LATE, 4, false), hi_lo(), sc(FullIndices, -10, 10)),
        ("6D H17 LS defaults, Hi-Lo I18F4", Rules::default(), hi_lo(), StrategyConfig::default()),
        ("6D H17 LS, Hi-Lo I18", Rules::default(), hi_lo(), sc(Illustrious18, -10, 10)),
        ("6D H17 LS, basic", Rules::default(), hi_lo(), sc(BasicOnly, -10, 10)),
        ("2D H17 noSurr, KO I18F4 -40..30", r(2, true, true, NO_SURR, 4, false), ko(), sc(I18Fab4, -40, 30)),
        ("6D S17 LS, KO I18F4 -30..30", r(6, false, true, LATE, 4, false), ko(), sc(I18Fab4, -30, 30)),
        ("1D H17 noDAS SP2, Hi-Lo full", r(1, true, false, NO_SURR, 2, false), hi_lo(), sc(FullIndices, -10, 10)),
        (
            "6D S17 LS, Red Seven full -20..20",
            r(6, false, true, LATE, 4, false),
            preset("Red Seven"),
            sc(FullIndices, -20, 20),
        ),
        (
            "8D S17 DAS, Omega II exact/round full -8..12",
            r(8, false, true, NO_SURR, 4, false),
            omega_exact_round(),
            sc(FullIndices, -8, 12),
        ),
        (
            "6D H17 LS, Halves quarter/truncate I18",
            Rules::default(),
            halves_quarter_truncate(),
            sc(Illustrious18, -10, 10),
        ),
        ("6D H17 LS, Hi-Lo I18F4 + overrides", Rules::default(), hi_lo(), with_overrides()),
        (
            "6D S17 DAS ENHC, Hi-Lo full",
            Rules { hole_card: HoleCardRule::Enhc, ..r(6, false, true, NO_SURR, 4, false) },
            hi_lo(),
            sc(FullIndices, -10, 10),
        ),
        (
            "2D H17 LS SP2 CSM, Hi-Lo full -6..6",
            Rules { penetration: 0.0, ..r(2, true, true, LATE, 2, false) },
            hi_lo(),
            sc(FullIndices, -6, 6),
        ),
        (
            "1D S17 noDAS SP2, custom red tags full -5..9",
            r(1, false, false, NO_SURR, 2, false),
            custom_red(),
            sc(FullIndices, -5, 9),
        ),
        ("5D H17 LS RSA 2:1 pen 83, Hi-Lo RC fractional IRC I18F4", five_deck(), rc_fractional(), sc(I18Fab4, -10, 10)),
        ("6D H17 LS, Hi-Lo full, reversed range 4..-3", Rules::default(), hi_lo(), sc(FullIndices, 4, -3)),
    ]
}

fn strategy_fixture() -> Json {
    const CPB: usize = CELLS_PER_BIN;
    let mut w = Json::new();
    w.obj();
    w.key("cases").arr();
    for (label, r, cs, config) in strategy_cases() {
        let pt = strategy::build(&r, &cs, &config);
        w.obj();
        w.key("label").str(label);
        w.key("rules");
        rules_json(&mut w, &r);
        w.key("count");
        count_json(&mut w, &cs);
        w.key("config");
        config_json(&mut w, &config);
        w.key("key").str(&pt.key);
        w.key("flagsHash").str(&fnv_bytes(&pt.flags));
        w.key("flagsBins").arr();
        for b in 0..NUM_BINS {
            let same = b > 0 && pt.flags[b * CPB..(b + 1) * CPB] == pt.flags[(b - 1) * CPB..b * CPB];
            if !same {
                w.arr().int(b as i64).str(&hex(&pt.flags[b * CPB..(b + 1) * CPB])).end();
            }
        }
        w.end();
        let insure: String = pt.insure.iter().map(|&x| if x { '1' } else { '0' }).collect();
        w.key("insure").str(&insure);
        w.key("binTcEq").nums(&pt.bin_tc_eq);
        w.key("basicEV").num(pt.basic_result.player_ev);
        w.key("basicFlags").str(&hex(&pt.basic()[..CPB]));
        w.key("perBinMin").int(pt.per_bin_min as i64);
        match &pt.per_bin {
            None => {
                w.key("perBin").null();
            }
            Some(per_bin) => {
                w.key("perBin").arr();
                for res in per_bin {
                    ca_json(&mut w, res, false);
                }
                w.end();
            }
        }
        w.key("fixedCells").str(&hex(&sim::fixed_cells(&pt)[..CPB]));
        let (lo, hi) = (config.index_min.min(config.index_max), config.index_min.max(config.index_max));
        w.key("describeRange").arr().int(lo as i64).int(hi as i64).end();
        w.key("indices").arr();
        for info in strategy::describe_indices(&pt, lo, hi) {
            w.obj().key("kind").str(info.kind.name()).key("total").int(info.total as i64);
            w.key("upcard").int(info.upcard as i64).key("hand").str(&info.hand).key("play").str(&info.play);
            w.key("condition").str(&info.condition).key("threshold").num(info.threshold);
            w.key("atOrAbove").bool(info.at_or_above).key("irregular").bool(info.irregular);
            w.key("text").str(&info.text()).end();
        }
        w.end();
        let codes: Vec<&str> = (0..CPB).map(|cell| strategy::action_code(pt.basic()[cell], cell >= 440)).collect();
        w.key("actionCodes").str(&codes.join(","));
        w.end();
    }
    w.end();
    w.end();
    w
}

// ---------------- sim.json ----------------

struct SimCase {
    label: &'static str,
    rules: Rules,
    count: CountSystem,
    config: StrategyConfig,
    spots: i32,
    rounds: i64,
    seed: u64,
}

#[allow(clippy::too_many_arguments)]
fn sim_case(
    label: &'static str,
    rules: Rules,
    count: CountSystem,
    config: StrategyConfig,
    spots: i32,
    rounds: i64,
    seed: u64,
) -> SimCase {
    SimCase { label, rules, count, config, spots, rounds, seed }
}

fn sim_cases() -> Vec<SimCase> {
    use IndexMode::{BasicOnly, FullIndices, I18Fab4};
    let hi_lo = CountSystem::default;
    let i18f4 = StrategyConfig::default;
    vec![
        sim_case("6D H17 LS, Hi-Lo I18F4, 1 spot", Rules::default(), hi_lo(), i18f4(), 1, 1_000_000, 99),
        sim_case("6D H17 LS, Hi-Lo I18F4, 3 spots", Rules::default(), hi_lo(), i18f4(), 3, 600_001, 1234),
        sim_case("6D H17 LS, Hi-Lo full, 7 spots", Rules::default(), hi_lo(), sc(FullIndices, -10, 10), 7, 400_000, 5),
        sim_case(
            "2D H17 noSurr, KO I18F4, 2 spots",
            r(2, true, true, NO_SURR, 4, false),
            preset("KO (Knock-Out)"),
            sc(I18Fab4, -40, 30),
            2,
            1_000_000,
            7,
        ),
        sim_case(
            "1D H17 noDAS SP2, Hi-Lo full, 1 spot",
            r(1, true, false, NO_SURR, 2, false),
            hi_lo(),
            sc(FullIndices, -10, 10),
            1,
            1_000_000,
            8,
        ),
        sim_case(
            "1D S17 SP2, Hi-Lo I18F4, 7 spots (reshuffles)",
            r(1, false, true, NO_SURR, 2, false),
            hi_lo(),
            i18f4(),
            7,
            300_000,
            9,
        ),
        sim_case(
            "6D S17 DAS ENHC LS CSM, basic, 1 spot",
            Rules { hole_card: HoleCardRule::Enhc, penetration: 0.0, ..r(6, false, true, LATE, 4, false) },
            hi_lo(),
            sc(BasicOnly, -10, 10),
            1,
            1_000_000,
            10,
        ),
        sim_case(
            "4D S17 DAS RSA HSA OBO ES no burn, Hi-Lo full, 2 spots",
            Rules {
                hit_split_aces: true,
                hole_card: HoleCardRule::EnhcObo,
                burn_card: false,
                ..r(4, false, true, EARLY, 4, true)
            },
            hi_lo(),
            sc(FullIndices, -10, 10),
            2,
            1_000_000,
            11,
        ),
        sim_case(
            "8D H17 ES SP3 7:5 no insurance, Red Seven I18F4, 3 spots",
            Rules { blackjack_pays: 1.4, insurance: false, ..r(8, true, false, EARLY, 3, false) },
            preset("Red Seven"),
            sc(I18Fab4, -20, 20),
            3,
            500_000,
            12,
        ),
        sim_case(
            "6D S17 LS, Halves quarter/truncate full, 3 spots",
            r(6, false, true, LATE, 4, false),
            halves_quarter_truncate(),
            sc(FullIndices, -10, 10),
            3,
            500_000,
            13,
        ),
        sim_case(
            "6D H17 LS, Hi-Lo I18F4 + overrides, 2 spots",
            Rules::default(),
            hi_lo(),
            with_overrides(),
            2,
            500_000,
            14,
        ),
        sim_case(
            "8D S17 DAS, Omega II exact/round full, 5 spots",
            r(8, false, true, NO_SURR, 4, false),
            omega_exact_round(),
            sc(FullIndices, -8, 12),
            5,
            300_000,
            15,
        ),
        sim_case(
            "2D H17 LS SP2 BJ 1.25 CSM, basic, 4 spots",
            Rules { penetration: 0.0, blackjack_pays: 1.25, ..r(2, true, true, LATE, 2, false) },
            hi_lo(),
            sc(BasicOnly, -10, 10),
            4,
            500_000,
            16,
        ),
        sim_case(
            "5D H17 LS RSA 2:1 pen 83 no burn, Hi-Lo RC fractional IRC I18F4, 2 spots",
            five_deck(),
            rc_fractional(),
            sc(I18Fab4, -10, 10),
            2,
            500_000,
            17,
        ),
        sim_case(
            "6D S17 no splits ES OBO, custom red tags full, 1 spot",
            Rules { hole_card: HoleCardRule::EnhcObo, ..r(6, false, true, EARLY, 1, false) },
            custom_red(),
            sc(FullIndices, -5, 9),
            1,
            500_000,
            18,
        ),
        sim_case(
            "6D H17 LS, Hi-Lo I18F4, 1 spot, seed near 2^64",
            Rules::default(),
            hi_lo(),
            i18f4(),
            1,
            300_000,
            0xFFFF_FFFF_FFFF_FFF0,
        ),
    ]
}

/// A seeded single-thread run: its tallies depend only on the seed.
fn run_sim(r: &Rules, cs: &CountSystem, pt: &PlayTable, spots: i32, rounds: i64, seed: u64) -> SimData {
    simulate(&SimPlan::new(r, cs, pt, spots), rounds, 1, seed)
}

fn sim_fixture() -> Json {
    let mut w = Json::new();
    w.obj();
    w.key("cases").arr();
    for c in sim_cases() {
        let pt = strategy::build(&c.rules, &c.count, &c.config);
        let d = run_sim(&c.rules, &c.count, &pt, c.spots, c.rounds, c.seed);
        w.obj();
        w.key("label").str(c.label);
        w.key("rules");
        rules_json(&mut w, &c.rules);
        w.key("count");
        count_json(&mut w, &c.count);
        w.key("config");
        config_json(&mut w, &c.config);
        w.key("spots").int(c.spots as i64).key("rounds").int(c.rounds).key("seed").str(&c.seed.to_string());
        w.key("flagsHash").str(&fnv_bytes(&pt.flags));
        w.key("data");
        sim_data_json(&mut w, &d);
        w.end();
    }
    w.end();
    w.end();
    w
}

fn sim_data_json(w: &mut Json, d: &SimData) {
    w.obj();
    w.key("binMin").int(d.bin_min as i64).key("numBins").int(d.num_bins as i64).key("spots").int(d.spots as i64);
    w.key("rounds").int(d.rounds).key("shoes").int(d.shoes).key("emergency").int(d.emergency);
    w.key("n").ints(d.n.iter().copied()).key("sa").ints(d.sa.iter().copied()).key("qa").ints(d.qa.iter().copied());
    w.key("sb").ints(d.sb.iter().copied()).key("qb").ints(d.qb.iter().copied()).key("ab").ints(d.ab.iter().copied());
    w.end();
}

// ---------------- analysis.json ----------------

/// A ramp from `min` with these units per row (and hands, else 1).
fn ramp(min: i32, units: &[f64], hands: Option<&[i32]>) -> BetRamp {
    let mut r = BetRamp::create(min, min + units.len() as i32 - 1, 1.0);
    r.units.copy_from_slice(units);
    if let Some(h) = hands {
        r.hands.copy_from_slice(h);
    }
    r
}

/// Ramps to evaluate, with unit, rounds per hour and bankroll.
fn ramps(rc: bool) -> Vec<(BetRamp, f64, f64, f64)> {
    let mut two = BetRamp::create(0, 0, 1.0);
    two.hands[0] = 2;
    let mut list = vec![
        (BetRamp::create(0, 0, 1.0), 1.0, 100.0, 1000.0),
        (BetRamp::create(0, 0, 0.0), 25.0, 100.0, 10000.0),
        (two, 1.0, 100.0, 1000.0),
    ];
    if !rc {
        list.push((ramp(-1, &[1.0, 1.0, 2.0, 4.0, 6.0, 8.0, 10.0, 12.0], None), 25.0, 100.0, 25000.0));
        list.push((ramp(-2, &[0.0, 0.0, 1.0, 2.0, 4.0, 6.0, 8.0, 8.0], None), 10.0, 120.0, 8000.0));
        list.push((
            ramp(-1, &[1.0, 1.0, 1.0, 2.0, 3.0, 4.0, 4.0, 4.0], Some(&[1, 1, 1, 1, 2, 2, 2, 3])),
            50.0,
            90.0,
            40000.0,
        ));
        let mut lin = BetRamp::create(-3, 8, 1.0);
        for v in -3..=8 {
            lin.units[(v + 3) as usize] = f64::max(0.5, 0.75 * (v + 1) as f64);
        }
        list.push((lin, 5.0, 200.0, 2000.0));
    } else {
        list.push((ramp(-6, &[1.0, 1.0, 1.0, 1.0, 1.0, 2.0, 4.0, 6.0, 8.0, 8.0, 8.0], None), 25.0, 100.0, 10000.0));
    }
    list
}

#[allow(clippy::too_many_arguments)]
fn opt(
    min_units: f64,
    max_units: f64,
    increment: f64,
    max_hands: i32,
    allow_wong_out: bool,
    kelly: bool,
    kelly_fraction: f64,
    bankroll_units: f64,
) -> OptimizerSettings {
    OptimizerSettings {
        min_units,
        max_units,
        increment,
        max_hands,
        allow_wong_out,
        kelly,
        kelly_fraction,
        bankroll_units,
    }
}

fn optimizations(rc: bool) -> (BetRamp, Vec<OptimizerSettings>) {
    let template = if rc { BetRamp::create(-8, 6, 1.0) } else { BetRamp::create(-3, 8, 1.0) };
    let mut list = vec![
        opt(1.0, 12.0, 1.0, 1, false, false, 1.0, 1000.0),
        opt(1.0, 12.0, 1.0, 2, false, false, 1.0, 1000.0),
        opt(1.0, 12.0, 1.0, 1, true, false, 1.0, 1000.0),
        opt(1.0, 100.0, 1.0, 1, false, true, 0.5, 1000.0),
    ];
    if !rc {
        list.push(opt(2.0, 20.0, 0.5, 3, false, false, 1.0, 1000.0));
        list.push(opt(1.0, 50.0, 0.25, 1, true, true, 1.0, 400.0));
        list.push(opt(1.0, 8.0, 0.0, 1, false, false, 1.0, 1000.0));
    }
    (template, list)
}

fn analysis_fixture() -> Json {
    let mut w = Json::new();
    w.obj();
    w.key("sims").arr();
    let hi_lo = CountSystem::default();
    let pt_h = strategy::build(&Rules::default(), &hi_lo, &StrategyConfig::default());
    let d = run_sim(&Rules::default(), &hi_lo, &pt_h, 3, 2_000_000, 31);
    analysis_case(&mut w, "6D H17 LS, Hi-Lo I18F4, 3 spots", &d, false);
    let d = run_sim(&Rules::default(), &hi_lo, &pt_h, 1, 2_000_000, 32);
    analysis_case(&mut w, "6D H17 LS, Hi-Lo I18F4, 1 spot", &d, false);
    let ko = preset("KO (Knock-Out)");
    let r2 = r(2, true, true, NO_SURR, 4, false);
    let pt_k = strategy::build(&r2, &ko, &sc(IndexMode::I18Fab4, -40, 30));
    let d = run_sim(&r2, &ko, &pt_k, 2, 1_000_000, 33);
    analysis_case(&mut w, "2D H17, KO I18F4, 2 spots", &d, true);
    w.end();

    w.key("phi").arr();
    for x in [-8.0, -6.0, -5.5, -5.0, -4.99, -3.0, -1.5, -0.3, 0.0, 0.3, 1.0, 2.5, 4.0, 7.0] {
        w.arr().num(x).num(analysis::phi(x)).end();
    }
    w.end();
    w.key("phiInv").arr();
    for p in [0.0, 1e-12, 1e-6, 0.001, 0.02, 0.02425, 0.1, 0.35, 0.5, 0.77, 0.97575, 0.99, 0.999999, 1.0 - 1e-12, 1.0] {
        w.arr().num(p).num(analysis::phi_inv(p)).end();
    }
    w.end();
    w.key("tripRuin").arr();
    let trips: [[f64; 4]; 10] = [
        [1.0, 1300.0, 500.0, 10000.0],
        [0.02, 1.3, 100.0, 1e5],
        [0.05, 30.0, 300.0, 2e4],
        [-0.01, 1.2, 50.0, 1000.0],
        [0.0, 1.0, 10.0, 100.0],
        [0.5, 100.0, 0.0, 10.0],
        [0.5, 100.0, 10.0, 0.0],
        [2.0, 1.0, 400.0, 1e6],
        [0.01, 1.3, 20.0, 50.0],
        [0.001, 1.0, 100.0, 100.0],
    ];
    for t in trips {
        w.arr().num(t[0]).num(t[1]).num(t[2]).num(t[3]).num(analysis::trip_ruin(t[0], t[1], t[2], t[3])).end();
    }
    w.end();
    w.key("probLoss").arr();
    for t in [[0.02, 1.3, 1000.0], [0.1, 30.0, 500.0], [-0.05, 2.0, 100.0], [0.0, 1.0, 0.0]] {
        w.arr().num(t[0]).num(t[1]).num(t[2]).num(analysis::prob_loss(t[0], t[1], t[2])).end();
    }
    w.end();
    w.end();
    w
}

fn analysis_case(w: &mut Json, label: &str, d: &SimData, rc: bool) {
    w.obj();
    w.key("label").str(label);
    w.key("data");
    sim_data_json(w, d);
    w.key("bins").arr();
    for (i, b) in analysis::bins(d).iter().enumerate() {
        if b.n == 0 {
            continue;
        }
        w.obj().key("i").int(i as i64).key("value").int(b.value as i64).key("n").int(b.n);
        w.key("freq").num(b.freq).key("mean").num(b.mean).key("m2").num(b.m2).key("cov").num(b.cov);
        w.key("measuredCov").bool(b.measured_cov).key("meanVar").num(b.mean_var);
        w.key("meanLast").num(b.mean_last).key("m2Last").num(b.m2_last).key("meanVarLast").num(b.mean_var_last).end();
    }
    w.end();
    w.key("evaluations").arr();
    for (ramp, unit, rph, bankroll) in ramps(rc) {
        let res = analysis::evaluate(d, &ramp, unit, rph, bankroll);
        w.obj();
        w.key("ramp");
        ramp_json(w, &ramp);
        w.key("unit").num(unit).key("rph").num(rph).key("bankroll").num(bankroll);
        w.key("result");
        result_json(w, &res);
        w.end();
    }
    w.end();
    w.key("optimizations").arr();
    let (template, settings) = optimizations(rc);
    for os in settings {
        let best = analysis::optimize(d, &template, &os);
        w.obj();
        w.key("template");
        ramp_json(w, &template);
        w.key("settings");
        settings_json(w, &os);
        w.key("result");
        ramp_json(w, &best);
        w.end();
    }
    w.end();
    w.end();
}

fn result_json(w: &mut Json, a: &AnalysisResult) {
    w.obj();
    w.key("valid").bool(a.valid);
    w.key("ev").num(a.ev).key("var").num(a.var).key("sd").num(a.sd).key("m2").num(a.m2);
    w.key("avgBet").num(a.avg_bet).key("playedFrac").num(a.played_frac);
    w.key("avgBetPlayed").num(a.avg_bet_played).key("edgePct").num(a.edge_pct);
    w.key("di").num(a.di).key("score").num(a.score).key("n0").num(a.n0).key("seEV").num(a.se_ev);
    w.key("winPerHour").num(a.win_per_hour).key("sdPerHour").num(a.sd_per_hour).key("winPer100").num(a.win_per_100);
    w.key("sdPer100").num(a.sd_per_100).key("seWinPerHour").num(a.se_win_per_hour);
    w.key("roundsPerHour").num(a.rounds_per_hour).key("unit").num(a.unit).key("bankroll").num(a.bankroll);
    w.key("ror").num(a.ror).key("cePerHour").num(a.ce_per_hour).key("kellyUnit").num(a.kelly_unit);
    w.key("hoursN0").num(a.hours_n0).key("spread").num(a.spread);
    w.key("usedAssumedCov").bool(a.used_assumed_cov);
    w.key("rowFreq").nums(&a.row_freq).key("rowEV").nums(&a.row_ev).key("rowContribution").nums(&a.row_contribution);
    w.key("rowM2").nums(&a.row_m2).key("rowN").ints(a.row_n.iter().copied());
    w.key("bankrollForRor").arr();
    for ror in [0.05, 0.135, 0.01, 0.5] {
        w.num(a.bankroll_for_ror(ror));
    }
    w.end();
    w.key("rorForBankroll").arr();
    for b in [1000.0, 5000.0, 20000.0] {
        w.num(a.ror_for_bankroll(b));
    }
    w.end();
    w.end();
}

// ---------------- JSON of engine types ----------------

fn rules_json(w: &mut Json, r: &Rules) {
    w.obj().key("decks").int(r.decks as i64).key("h17").bool(r.h17).key("holeCard").str(r.hole_card.name());
    w.key("blackjackPays").num(r.blackjack_pays).key("doubleOn").str(r.double_on.name()).key("das").bool(r.das);
    w.key("maxSplitHands").int(r.max_split_hands as i64).key("resplitAces").bool(r.resplit_aces);
    w.key("hitSplitAces").bool(r.hit_split_aces).key("surrender").str(r.surrender.name());
    w.key("insurance").bool(r.insurance).key("penetration").num(r.penetration);
    w.key("burnCard").bool(r.burn_card).end();
}

fn count_json(w: &mut Json, cs: &CountSystem) {
    w.obj().key("name").str(&cs.name).key("tags").nums(&cs.tags);
    match &cs.red_tags {
        None => w.key("redTags").null(),
        Some(red) => w.key("redTags").nums(red),
    };
    w.key("ircBase").num(cs.irc_base).key("ircPerDeck").num(cs.irc_per_deck).key("metric").str(cs.metric.name());
    w.key("deckEst").str(cs.deck_est.name()).key("rounding").str(cs.rounding.name()).end();
}

fn config_json(w: &mut Json, sc: &StrategyConfig) {
    w.obj().key("mode").str(sc.mode.name()).key("indexMin").int(sc.index_min as i64);
    w.key("indexMax").int(sc.index_max as i64);
    w.key("overrides").arr();
    for o in &sc.overrides {
        w.obj().key("enabled").bool(o.enabled).key("kind").str(o.kind.name()).key("total").int(o.total as i64);
        w.key("upcard").int(o.upcard as i64).key("play").str(o.play.name()).key("atOrAbove").bool(o.at_or_above);
        w.key("index").num(o.index).key("key").str(&o.key()).end();
    }
    w.end();
    w.end();
}

fn ramp_json(w: &mut Json, r: &BetRamp) {
    w.obj().key("min").int(r.min as i64).key("max").int(r.max as i64).key("units").nums(&r.units);
    w.key("hands").ints(r.hands.iter().map(|&h| h as i64)).end();
}

fn settings_json(w: &mut Json, o: &OptimizerSettings) {
    w.obj().key("minUnits").num(o.min_units).key("maxUnits").num(o.max_units).key("increment").num(o.increment);
    w.key("maxHands").int(o.max_hands as i64).key("allowWongOut").bool(o.allow_wong_out).key("kelly").bool(o.kelly);
    w.key("kellyFraction").num(o.kelly_fraction).key("bankrollUnits").num(o.bankroll_units).end();
}

fn hex(a: &[u8]) -> String {
    a.iter().map(|b| format!("{b:02x}")).collect()
}

fn fnv_bytes(a: &[u8]) -> String {
    let mut h: u64 = 14695981039346656037;
    for &b in a {
        h ^= b as u64;
        h = h.wrapping_mul(1099511628211);
    }
    format!("{h:016x}")
}

fn fnv_shorts(a: &[i16]) -> String {
    let mut h: u64 = 14695981039346656037;
    for &v in a {
        h ^= v as u16 as u64;
        h = h.wrapping_mul(1099511628211);
    }
    format!("{h:016x}")
}

/// A small JSON writer with the fixtures' layout: a line per key down to the third level and per object in the first
/// two levels of arrays, everything deeper on one line.
struct Json {
    text: String,
    closers: Vec<char>,
    /// Per open object or array: nothing written in it yet.
    empty: Vec<bool>,
    after_key: bool,
}

impl Json {
    fn new() -> Json {
        Json { text: String::with_capacity(1 << 20), closers: Vec::new(), empty: Vec::new(), after_key: false }
    }

    /// Writes the separator before a value; true when the value follows a key.
    fn sep(&mut self) -> bool {
        if self.after_key {
            self.after_key = false;
            return true;
        }
        if let Some(empty) = self.empty.last_mut() {
            if *empty {
                *empty = false;
            } else {
                self.text.push(',');
            }
        }
        false
    }

    fn new_line(&mut self) {
        self.text.push('\n');
        for _ in 0..2 * self.closers.len() {
            self.text.push(' ');
        }
    }

    fn open(&mut self, open: char, close: char) -> &mut Json {
        let keyed = self.sep();
        if !keyed && open == '{' && !self.closers.is_empty() && self.closers.len() <= 2 {
            self.new_line();
        }
        self.text.push(open);
        self.closers.push(close);
        self.empty.push(true);
        self
    }

    fn obj(&mut self) -> &mut Json {
        self.open('{', '}')
    }

    fn arr(&mut self) -> &mut Json {
        self.open('[', ']')
    }

    fn end(&mut self) -> &mut Json {
        self.empty.pop();
        let close = self.closers.pop().expect("an open object or array");
        self.text.push(close);
        self
    }

    fn key(&mut self, k: &str) -> &mut Json {
        self.sep();
        if self.closers.len() <= 3 {
            self.new_line();
        }
        self.quote(k);
        self.text.push(':');
        self.after_key = true;
        self
    }

    fn null(&mut self) -> &mut Json {
        self.sep();
        self.text.push_str("null");
        self
    }

    fn bool(&mut self, v: bool) -> &mut Json {
        self.sep();
        self.text.push_str(if v { "true" } else { "false" });
        self
    }

    fn int(&mut self, v: i64) -> &mut Json {
        self.sep();
        self.text.push_str(&v.to_string());
        self
    }

    fn num(&mut self, v: f64) -> &mut Json {
        self.sep();
        self.text.push_str(&g17(v));
        self
    }

    fn str(&mut self, v: &str) -> &mut Json {
        self.sep();
        self.quote(v);
        self
    }

    fn nums(&mut self, a: &[f64]) -> &mut Json {
        self.arr();
        for &v in a {
            self.num(v);
        }
        self.end()
    }

    fn ints(&mut self, a: impl IntoIterator<Item = i64>) -> &mut Json {
        self.arr();
        for v in a {
            self.int(v);
        }
        self.end()
    }

    fn quote(&mut self, s: &str) {
        self.text.push('"');
        for c in s.chars() {
            if c == '"' || c == '\\' {
                self.text.push('\\');
                self.text.push(c);
            } else if c < ' ' {
                self.text.push_str(&format!("\\u{:04x}", c as u32));
            } else {
                self.text.push(c);
            }
        }
        self.text.push('"');
    }
}

/// A number as .NET Framework's `double.ToString("G17")` writes it: 17 significant digits without trailing zeros,
/// in scientific notation (`1.0000000000000001E-05`) below 1E-04 and from 1E+17. NaN is null, infinities are strings.
fn g17(v: f64) -> String {
    if v.is_nan() {
        return "null".to_string();
    }
    if v.is_infinite() {
        return if v > 0.0 { "\"Infinity\"" } else { "\"-Infinity\"" }.to_string();
    }
    if v == 0.0 {
        return "0".to_string(); // also -0, as .NET Framework writes it
    }
    let s = format!("{:.16e}", v.abs());
    let (mantissa, exp) = s.split_once('e').expect("exponent");
    let exp: i32 = exp.parse().expect("exponent value");
    let mut digits: Vec<u8> = mantissa.bytes().filter(u8::is_ascii_digit).collect();
    while digits.len() > 1 && digits.last() == Some(&b'0') {
        digits.pop();
    }
    // value = 0.d1d2d3... * 10^scale
    let scale = exp + 1;
    let mut out = String::new();
    if v < 0.0 {
        out.push('-');
    }
    if !(-3..=17).contains(&scale) {
        out.push(digits[0] as char);
        if digits.len() > 1 {
            out.push('.');
            out.extend(digits[1..].iter().map(|&d| d as char));
        }
        out.push_str(&format!("E{}{:02}", if exp < 0 { '-' } else { '+' }, exp.abs()));
    } else if scale <= 0 {
        out.push_str("0.");
        for _ in 0..-scale {
            out.push('0');
        }
        out.extend(digits.iter().map(|&d| d as char));
    } else {
        let whole = scale as usize;
        for i in 0..whole {
            out.push(digits.get(i).map_or('0', |&d| d as char));
        }
        if digits.len() > whole {
            out.push('.');
            out.extend(digits[whole..].iter().map(|&d| d as char));
        }
    }
    out
}
