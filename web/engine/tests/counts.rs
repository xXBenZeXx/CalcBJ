//! Count systems, true-count conversion and the simulator's count lookup against the fixtures (fixtures/counts.json).

mod common;

use calcbj_engine::*;
use common::*;

#[test]
fn count_systems() {
    let fx = fixture("counts.json");
    let rules_list: Vec<Rules> = list(&fx["rules"]).iter().map(rules).collect();
    let mut c = Check::new("count systems");
    for s in list(&fx["systems"]) {
        let cs = count(&s["count"]);
        let name = cs.name.clone();
        c.eq(&format!("{name}: scale"), cs.scale() as i64, int(&s["scale"]));
        c.num(&format!("{name}: imbalance"), cs.imbalance_per_deck(), num(&s["imbalance"]), 0.0);
        c.eq(&format!("{name}: balanced"), cs.is_balanced(), flag(&s["balanced"]));
        c.num(&format!("{name}: level"), cs.level(), num(&s["level"]), 0.0);
        let ircs: Vec<f64> = [1, 2, 5, 6, 8].iter().map(|&d| cs.irc(d)).collect();
        c.nums(&format!("{name}: IRC"), &ircs, &nums(&s["irc"]), 0.0);
        c.eq(&format!("{name}: tag text"), cs.tag_text(), text(&s["tagText"]).to_string());
        c.eq(&format!("{name}: key"), cs.key(), text(&s["key"]).to_string());
        let tags: Vec<f64> = (0..20).map(|code| cs.tag_of_code(code)).collect();
        c.nums(&format!("{name}: tag of code"), &tags, &nums(&s["tagOfCode"]), 0.0);
        let rank_tags: Vec<f64> = (1..=10).map(|r| cs.rank_tag(r)).collect();
        c.nums(&format!("{name}: rank tag"), &rank_tags, &nums(&s["rankTag"]), 0.0);
        let reps: Vec<f64> = [-12, -1, 0, 1, 7].iter().map(|&v| strategy::representative(&cs, v)).collect();
        c.nums(&format!("{name}: representative"), &reps, &nums(&s["representative"]), 0.0);
        for (r, want) in rules_list.iter().zip(list(&s["tcEquivalent"])) {
            let got: Vec<f64> =
                [-10.0, -3.5, 0.0, 2.5, 9.0].iter().map(|&mv| strategy::tc_equivalent(&cs, r, mv)).collect();
            c.nums(&format!("{name}: TC equivalent, {} decks", r.decks), &got, &nums(want), 0.0);
        }
        for sh in list(&s["shifted"]) {
            let (t, d) = (num(&sh["tcEq"]), num(&sh["decks"]));
            let got = strategy::shifted_shoe(&cs, t, d);
            c.nums(&format!("{name}: shifted shoe at {t} with {d} decks"), &got, &nums(&sh["shoe"]), 0.0);
        }
        let scale = cs.scale();
        for mv in list(&s["metricValue"]) {
            let x = ints(mv);
            let got = sim::metric_value(&cs, scale, x[0] as i32, x[1] as i32);
            c.eq(&format!("{name}: count value for rc {} with {} cards left", x[0], x[1]), got as i64, x[2]);
        }
        for (r, want) in rules_list.iter().zip(list(&s["binLookups"])) {
            let b = sim::build_bins(r, &cs);
            let label = format!("{name}: bin lookup, {} decks", r.decks);
            c.eq(
                &format!("{label}: rc range"),
                (b.rc_min as i64, b.rc_max as i64, b.width as i64),
                (int(&want["rcMin"]), int(&want["rcMax"]), int(&want["width"])),
            );
            c.eq(&format!("{label}: rows"), (b.rows.len() / b.width as usize) as i64, int(&want["rows"]));
            c.eq(
                &format!("{label}: row of each cards-left value"),
                b.cr_row.iter().map(|&x| x as i64).collect::<Vec<_>>(),
                ints(&want["crRow"]),
            );
            c.eq(&format!("{label}: rows hash"), fnv_shorts(&b.rows), text(&want["rowsHash"]).to_string());
        }
    }
    for est in DeckEstimation::ALL {
        let got: Vec<f64> = (0..=416).map(|cr| sim::estimate_decks(*est, cr)).collect();
        c.nums(&format!("estimate decks {}", est.name()), &got, &nums(&fx["estimateDecks"][est.name()]), 0.0);
    }
    c.finish();
}

/// The fixture's systems start with the presets, in order (as CalcBJ's first version had them), so a changed preset
/// shows up.
#[test]
fn presets_match_the_fixture() {
    let fx = fixture("counts.json");
    let mut c = Check::new("presets");
    for (p, s) in CountSystem::presets().iter().zip(list(&fx["systems"])) {
        let want = count(&s["count"]);
        c.eq(&format!("{}: name", p.name), p.name.clone(), want.name.clone());
        c.eq(&format!("{}: definition", p.name), p.key(), want.key());
    }
    c.finish();
}

/// A true count only means something when the tags sum to zero over a deck (Canfield Expert once counted the 2 as +1).
#[test]
fn true_count_presets_are_balanced() {
    for p in CountSystem::presets() {
        if matches!(p.metric, CountMetric::TrueCount) {
            assert!(p.is_balanced(), "{} is used as a true count but has {} per deck", p.name, p.imbalance_per_deck());
        }
    }
}
