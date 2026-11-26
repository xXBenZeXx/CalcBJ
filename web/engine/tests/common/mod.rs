//! Loading the fixtures (written by examples/fixtures.rs) and comparing results.
#![allow(dead_code)]

use calcbj_engine::*;
use serde_json::Value;
use std::path::PathBuf;

pub fn fixture(name: &str) -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests").join("fixtures").join(name);
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("{}: {e} (write the fixtures with cargo run --example fixtures)", path.display()));
    serde_json::from_str(&text).expect("fixture JSON")
}

pub fn list(v: &Value) -> &Vec<Value> {
    v.as_array().expect("array")
}

/// A number; null stands for NaN and the strings "Infinity"/"-Infinity" for infinities.
pub fn num(v: &Value) -> f64 {
    match v {
        Value::Null => f64::NAN,
        Value::Number(n) => n.as_f64().expect("number"),
        Value::String(s) if s == "Infinity" => f64::INFINITY,
        Value::String(s) if s == "-Infinity" => f64::NEG_INFINITY,
        _ => panic!("not a number: {v}"),
    }
}

pub fn nums(v: &Value) -> Vec<f64> {
    list(v).iter().map(num).collect()
}

pub fn int(v: &Value) -> i64 {
    v.as_i64().unwrap_or_else(|| panic!("not an integer: {v}"))
}

pub fn ints(v: &Value) -> Vec<i64> {
    list(v).iter().map(int).collect()
}

pub fn text(v: &Value) -> &str {
    v.as_str().unwrap_or_else(|| panic!("not a string: {v}"))
}

pub fn flag(v: &Value) -> bool {
    v.as_bool().unwrap_or_else(|| panic!("not a bool: {v}"))
}

pub fn hex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex")).collect()
}

pub fn shoe11(v: &Value) -> [f64; 11] {
    let x = nums(v);
    std::array::from_fn(|i| x[i])
}

fn arr10(v: &Value) -> [f64; 10] {
    let x = nums(v);
    std::array::from_fn(|i| x[i])
}

pub fn fnv_bytes(a: &[u8]) -> String {
    let mut h: u64 = 14695981039346656037;
    for &b in a {
        h ^= b as u64;
        h = h.wrapping_mul(1099511628211);
    }
    format!("{h:016x}")
}

pub fn fnv_shorts(a: &[i16]) -> String {
    let mut h: u64 = 14695981039346656037;
    for &v in a {
        h ^= v as u16 as u64;
        h = h.wrapping_mul(1099511628211);
    }
    format!("{h:016x}")
}

pub fn rules(v: &Value) -> Rules {
    Rules {
        decks: int(&v["decks"]) as i32,
        h17: flag(&v["h17"]),
        hole_card: HoleCardRule::from_name(text(&v["holeCard"])).expect("hole card rule"),
        blackjack_pays: num(&v["blackjackPays"]),
        double_on: DoubleRule::from_name(text(&v["doubleOn"])).expect("double rule"),
        das: flag(&v["das"]),
        max_split_hands: int(&v["maxSplitHands"]) as i32,
        resplit_aces: flag(&v["resplitAces"]),
        hit_split_aces: flag(&v["hitSplitAces"]),
        surrender: SurrenderRule::from_name(text(&v["surrender"])).expect("surrender rule"),
        insurance: flag(&v["insurance"]),
        penetration: num(&v["penetration"]),
        burn_card: flag(&v["burnCard"]),
    }
}

pub fn count(v: &Value) -> CountSystem {
    CountSystem {
        name: text(&v["name"]).to_string(),
        tags: arr10(&v["tags"]),
        red_tags: if v["redTags"].is_null() { None } else { Some(arr10(&v["redTags"])) },
        irc_base: num(&v["ircBase"]),
        irc_per_deck: num(&v["ircPerDeck"]),
        metric: CountMetric::from_name(text(&v["metric"])).expect("metric"),
        deck_est: DeckEstimation::from_name(text(&v["deckEst"])).expect("deck estimation"),
        rounding: TcRounding::from_name(text(&v["rounding"])).expect("rounding"),
    }
}

pub fn config(v: &Value) -> StrategyConfig {
    StrategyConfig {
        mode: IndexMode::from_name(text(&v["mode"])).expect("index mode"),
        index_min: int(&v["indexMin"]) as i32,
        index_max: int(&v["indexMax"]) as i32,
        overrides: list(&v["overrides"])
            .iter()
            .map(|o| IndexOverride {
                enabled: flag(&o["enabled"]),
                kind: HandKind::from_name(text(&o["kind"])).expect("hand kind"),
                total: int(&o["total"]) as i32,
                upcard: int(&o["upcard"]) as i32,
                play: PlayKind::from_name(text(&o["play"])).expect("play"),
                at_or_above: flag(&o["atOrAbove"]),
                index: num(&o["index"]),
            })
            .collect(),
    }
}

pub fn ramp(v: &Value) -> BetRamp {
    BetRamp {
        min: int(&v["min"]) as i32,
        max: int(&v["max"]) as i32,
        units: nums(&v["units"]),
        hands: ints(&v["hands"]).into_iter().map(|h| h as i32).collect(),
    }
}

pub fn settings(v: &Value) -> OptimizerSettings {
    OptimizerSettings {
        min_units: num(&v["minUnits"]),
        max_units: num(&v["maxUnits"]),
        increment: num(&v["increment"]),
        max_hands: int(&v["maxHands"]) as i32,
        allow_wong_out: flag(&v["allowWongOut"]),
        kelly: flag(&v["kelly"]),
        kelly_fraction: num(&v["kellyFraction"]),
        bankroll_units: num(&v["bankrollUnits"]),
    }
}

pub fn sim_data(v: &Value) -> SimData {
    let mut d = SimData::create(int(&v["spots"]) as i32);
    d.bin_min = int(&v["binMin"]) as i32;
    d.num_bins = int(&v["numBins"]) as usize;
    d.rounds = int(&v["rounds"]);
    d.shoes = int(&v["shoes"]);
    d.emergency = int(&v["emergency"]);
    d.n = ints(&v["n"]);
    d.sa = ints(&v["sa"]);
    d.qa = ints(&v["qa"]);
    d.sb = ints(&v["sb"]);
    d.qb = ints(&v["qb"]);
    d.ab = ints(&v["ab"]);
    d
}

/// Readable name of a strategy cell, for messages.
pub fn cell_name(cell: usize) -> String {
    let (kind, rest) = if cell >= 440 {
        ("pair", cell - 440)
    } else if cell >= 220 {
        ("soft", cell - 220)
    } else {
        ("hard", cell)
    };
    let (t, u) = (rest / 10, rest % 10 + 1);
    let t = if kind == "pair" { t + 1 } else { t };
    format!("{kind} {t} v {}", cards::rank_name(u))
}

/// Collects mismatches so one run reports all of them, and tracks the largest numeric difference.
pub struct Check {
    name: String,
    failures: Vec<String>,
    pub compared: usize,
    pub max_diff: f64,
    pub exact: usize,
}

impl Check {
    pub fn new(name: &str) -> Check {
        Check { name: name.to_string(), failures: Vec::new(), compared: 0, max_diff: 0.0, exact: 0 }
    }

    pub fn fail(&mut self, msg: String) {
        if self.failures.len() < 100 {
            self.failures.push(msg);
        } else if self.failures.len() == 100 {
            self.failures.push("(more mismatches not shown)".to_string());
        }
    }

    /// Equal within `tol`, or both NaN, or the same infinity.
    pub fn num(&mut self, what: &str, got: f64, want: f64, tol: f64) {
        self.compared += 1;
        if got.to_bits() == want.to_bits() || (got.is_nan() && want.is_nan()) {
            self.exact += 1;
            return;
        }
        let diff = (got - want).abs();
        if got.is_finite() && want.is_finite() && diff > self.max_diff {
            self.max_diff = diff;
        }
        if diff.is_nan() || diff > tol {
            self.fail(format!("{what}: got {got:e}, want {want:e} (diff {diff:e})"));
        }
    }

    pub fn nums(&mut self, what: &str, got: &[f64], want: &[f64], tol: f64) {
        if got.len() != want.len() {
            self.fail(format!("{what}: {} values, want {}", got.len(), want.len()));
            return;
        }
        for (i, (&g, &w)) in got.iter().zip(want).enumerate() {
            self.num(&format!("{what}[{i}]"), g, w, tol);
        }
    }

    pub fn eq<T: PartialEq + std::fmt::Debug>(&mut self, what: &str, got: T, want: T) {
        self.compared += 1;
        if got == want {
            self.exact += 1;
        } else {
            self.fail(format!("{what}: got {got:?}, want {want:?}"));
        }
    }

    pub fn finish(self) {
        println!(
            "{}: {} values compared, {} bit-identical, largest difference {:e}",
            self.name, self.compared, self.exact, self.max_diff
        );
        if !self.failures.is_empty() {
            panic!("{}: {} mismatches\n{}", self.name, self.failures.len(), self.failures.join("\n"));
        }
    }
}
