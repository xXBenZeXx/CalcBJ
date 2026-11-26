//! Rules texts, cut card and combinatorial analysis against the fixtures (fixtures/ca.json).

mod common;

use calcbj_engine::*;
use common::*;
use serde_json::Value;

/// The analysis is plain arithmetic, so results should be identical to the fixtures; this only allows for a last-bit
/// difference.
const TOL: f64 = 1e-14;

fn compare_ca(c: &mut Check, label: &str, res: &CaResult, want: &Value) {
    c.num(&format!("{label}: playerEV"), res.player_ev, num(&want["playerEV"]), TOL);
    c.num(&format!("{label}: insuranceEV"), res.insurance_ev, num(&want["insuranceEV"]), TOL);
    c.eq(&format!("{label}: insure"), res.insure, flag(&want["insure"]));
    let flags = hex(text(&want["flags"]));
    c.eq(&format!("{label}: flag count"), res.flags.len(), flags.len());
    for (i, (&g, &w)) in res.flags.iter().zip(&flags).enumerate() {
        c.eq(&format!("{label}: flags of {}", cell_name(i)), g, w);
    }
    if let Some(cell_ev) = want.get("cellEV") {
        c.nums(&format!("{label}: cellEV"), &res.cell_ev, &nums(cell_ev), TOL);
    } else {
        let (mut sum, mut nan) = (0.0, 0i64);
        for &v in &res.cell_ev {
            if v.is_nan() {
                nan += 1;
            } else {
                sum += v;
            }
        }
        c.num(&format!("{label}: cellEV sum"), sum, num(&want["cellEVSum"]), 1e-12);
        c.eq(&format!("{label}: cellEV NaN count"), nan, int(&want["cellEVNaN"]));
    }
    c.num(&format!("{label}: cellBJ sum"), res.cell_bj.iter().sum(), num(&want["cellBJSum"]), 1e-12);
}

#[test]
fn rules_texts_and_cut_card() {
    let fx = fixture("ca.json");
    let mut c = Check::new("rules");
    for case in list(&fx["cases"]) {
        let label = text(&case["label"]);
        let r = rules(&case["rules"]);
        c.eq(&format!("{label}: key"), r.key(), text(&case["key"]).to_string());
        c.eq(&format!("{label}: describe"), r.describe(), text(&case["describe"]).to_string());
        c.eq(&format!("{label}: payout"), Rules::payout_text(r.blackjack_pays), text(&case["payoutText"]).to_string());
        c.eq(&format!("{label}: total cards"), r.total_cards() as i64, int(&case["totalCards"]));
        let cuts: Vec<i64> = (1..=7).map(|s| r.cut_card(s) as i64).collect();
        c.eq(&format!("{label}: cut card for 1..7 spots"), cuts, ints(&case["cutCard"]));
        c.num(&format!("{label}: mid depth"), strategy::mid_depth_decks(&r), num(&case["midDepthDecks"]), 0.0);
        let mut dbl = String::new();
        for soft in [false, true] {
            for t in 2..=21 {
                dbl.push(if r.can_double_total(t, soft) { '1' } else { '0' });
            }
        }
        c.eq(&format!("{label}: can double"), dbl, text(&case["canDouble"]).to_string());
    }
    c.finish();
}

#[test]
fn combinatorial_analysis() {
    let fx = fixture("ca.json");
    let mut c = Check::new("combinatorial analysis");
    for case in list(&fx["cases"]) {
        let label = text(&case["label"]);
        let r = rules(&case["rules"]);
        let mut engine = CaEngine::new(&r);
        let res = engine.evaluate(&full_shoe(r.decks as f64));
        compare_ca(&mut c, &format!("{label}, off the top"), &res, &case["offTop"]);
        c.num(&format!("{label}: house edge"), house_edge_off_top(&r), num(&case["houseEdge"]), TOL);
        let (basic, adj, _) = basic_for(&r);
        compare_ca(&mut c, &format!("{label}, basic strategy"), &basic, &case["basic"]);
        c.nums(&format!("{label}: stand shifts"), &adj.stand_shift, &nums(&case["basicShifts"]), 1e-12);
        c.nums(&format!("{label}: multi-card ratios"), &adj.stand_ratio, &nums(&case["basicRatios"]), 1e-12);
        if let Some(eor) = case.get("eor") {
            c.nums(&format!("{label}: effects of removal"), &effects_of_removal(&r), &nums(eor), TOL);
        }
        for s in list(&case["shoes"]) {
            let res = engine.evaluate(&shoe11(&s["shoe"]));
            compare_ca(&mut c, &format!("{label}, {}", text(&s["label"])), &res, &s["result"]);
        }
    }
    c.finish();
}

#[test]
fn dealer_probabilities() {
    let fx = fixture("ca.json");
    let mut c = Check::new("dealer");
    for d in list(&fx["dealer"]) {
        let (up, h17) = (int(&d["up"]) as usize, flag(&d["h17"]));
        let label = format!("up {up} {}", if h17 { "H17" } else { "S17" });
        c.eq(&format!("{label}: states"), dealer_states(up, h17) as i64, int(&d["count"]));
        for (shoe, dist) in [("shoe6", "dist6"), ("shoe1", "dist1")] {
            let got = dealer_outcomes(up, h17, &shoe11(&d[shoe]));
            c.nums(&format!("{label} {dist}"), &got, &nums(&d[dist]), TOL);
        }
    }
    c.finish();
}

/// Published figures for well-known games (total-dependent basic strategy, off the top).
#[test]
fn known_house_edges() {
    let game = |decks, h17, das, surrender, max_split| Rules {
        decks,
        h17,
        das,
        surrender,
        max_split_hands: max_split,
        ..Rules::default()
    };
    let cases = [
        (game(6, false, true, SurrenderRule::None, 4), -0.0040),
        (game(6, true, true, SurrenderRule::None, 4), -0.0062),
        (game(8, false, true, SurrenderRule::None, 4), -0.0043),
    ];
    for (r, want) in cases {
        let ev = house_edge_off_top(&r);
        assert!((ev - want).abs() < 0.0003, "{}: {:.4}% vs published {:.2}%", r.describe(), ev * 100.0, want * 100.0);
    }
}
