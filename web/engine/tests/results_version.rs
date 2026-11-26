//! The apps keep simulation results per settings and treat those stored under another `RESULTS_VERSION` as outdated
//! (see `src/lib.rs`). This fails when the fixtures' simulation results change, as the reminder to raise the version.

mod common;

use calcbj_engine::RESULTS_VERSION;
use common::*;
use serde_json::{Value, json};

/// The results version the fixtures' simulation results belong to, and their fingerprint.
const FIXTURES_VERSION: u32 = 2;
const FINGERPRINT: &str = "e276864907bc6982";

/// What a simulation's results depend on: every strategy table's decisions (flags and insurance at every count, basic
/// strategy) and the tallies of the seeded simulations.
fn fingerprint() -> String {
    let strategy = fixture("strategy.json");
    let sim = fixture("sim.json");
    let mut parts: Vec<Value> = Vec::new();
    for c in list(&strategy["cases"]) {
        parts.push(json!([c["label"], c["flagsHash"], c["insure"], c["basicFlags"]]));
    }
    for c in list(&sim["cases"]) {
        let d = &c["data"];
        parts.push(json!([
            c["label"],
            c["flagsHash"],
            d["rounds"],
            d["shoes"],
            d["emergency"],
            d["n"],
            d["sa"],
            d["qa"],
            d["sb"],
            d["qb"],
            d["ab"]
        ]));
    }
    fnv_bytes(serde_json::to_string(&parts).expect("JSON").as_bytes())
}

#[test]
fn results_version_follows_the_fixtures() {
    let now = fingerprint();
    assert_eq!(
        now, FINGERPRINT,
        "the simulation results in the fixtures changed (strategy decisions or seeded tallies): raise RESULTS_VERSION \
         in src/lib.rs (now {RESULTS_VERSION}), then set FIXTURES_VERSION here to it and FINGERPRINT to \"{now}\""
    );
    assert_eq!(RESULTS_VERSION, FIXTURES_VERSION, "RESULTS_VERSION isn't the version of the fixtures' results");
}
