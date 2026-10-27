//! Strategy tables for every count, index generation and index descriptions (C#: Strategy.cs).
//!
//! Cell layout per count bin: hard totals [0..=21] x 10 upcards, soft totals [0..=21] x 10, pairs [A..=T] x 10.

use crate::ca::{CaEngine, CaResult, flag, full_shoe};
use crate::num::dmax;
use crate::types::{
    CountMetric, CountSystem, HandKind, IndexMode, IndexOverride, PlayKind, Rules, StrategyConfig, SurrenderRule,
    TcRounding, cards,
};

pub const CELLS_PER_BIN: usize = 540;
pub const BIN_MIN: i32 = -100;
pub const BIN_MAX: i32 = 100;
pub const NUM_BINS: usize = (BIN_MAX - BIN_MIN + 1) as usize;

pub const fn hard_cell(total: usize, up: usize) -> usize {
    total * 10 + (up - 1)
}

pub const fn soft_cell(total: usize, up: usize) -> usize {
    220 + total * 10 + (up - 1)
}

pub const fn pair_cell(rank: usize, up: usize) -> usize {
    440 + (rank - 1) * 10 + (up - 1)
}

/// Decision flags for every count bin.
#[derive(Clone, Debug)]
pub struct PlayTable {
    /// `NUM_BINS * CELLS_PER_BIN` flags.
    pub flags: Vec<u8>,
    pub insure: Vec<bool>,
    pub basic_result: CaResult,
    pub bin_tc_eq: Vec<f64>,
    pub bin_ev: Vec<f64>,
    pub key: String,
    /// CA results for the count values inside the index range (None for basic strategy only).
    pub per_bin: Option<Vec<CaResult>>,
    pub per_bin_min: i32,
}

impl PlayTable {
    /// Basic-strategy flags (off the top).
    pub fn basic(&self) -> &[u8] {
        &self.basic_result.flags
    }

    pub fn result_at(&self, bin_value: i32) -> &CaResult {
        match &self.per_bin {
            Some(per) if !per.is_empty() => {
                let i = (bin_value - self.per_bin_min).clamp(0, per.len() as i32 - 1);
                &per[i as usize]
            }
            _ => &self.basic_result,
        }
    }

    pub fn flags_at(&self, bin_value: i32, cell: usize) -> u8 {
        let b = (bin_value.clamp(BIN_MIN, BIN_MAX) - BIN_MIN) as usize;
        self.flags[b * CELLS_PER_BIN + cell]
    }

    pub fn insure_at(&self, bin_value: i32) -> bool {
        self.insure[(bin_value.clamp(BIN_MIN, BIN_MAX) - BIN_MIN) as usize]
    }
}

/// One decision that differs from basic strategy somewhere in a count range.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize), serde(rename_all = "camelCase"))]
pub struct IndexInfo {
    pub kind: HandKind,
    pub total: i32,
    pub upcard: i32,
    pub hand: String,
    pub play: String,
    pub condition: String,
    pub threshold: f64,
    pub at_or_above: bool,
    pub irregular: bool,
}

impl IndexInfo {
    pub fn text(&self) -> String {
        format!("{} {}", self.play, self.condition)
    }
}

const I18: [(usize, u8); 17] = [
    (hard_cell(16, 10), flag::STAND),
    (hard_cell(15, 10), flag::STAND),
    (pair_cell(10, 5), flag::SPLIT),
    (pair_cell(10, 6), flag::SPLIT),
    (hard_cell(10, 10), flag::DOUBLE),
    (hard_cell(12, 3), flag::STAND),
    (hard_cell(12, 2), flag::STAND),
    (hard_cell(11, 1), flag::DOUBLE),
    (hard_cell(9, 2), flag::DOUBLE),
    (hard_cell(10, 1), flag::DOUBLE),
    (hard_cell(9, 7), flag::DOUBLE),
    (hard_cell(16, 9), flag::STAND),
    (hard_cell(13, 2), flag::STAND),
    (hard_cell(12, 4), flag::STAND),
    (hard_cell(12, 5), flag::STAND),
    (hard_cell(12, 6), flag::STAND),
    (hard_cell(13, 3), flag::STAND),
];

const FAB4: [(usize, u8); 4] = [
    (hard_cell(14, 10), flag::SURRENDER),
    (hard_cell(15, 10), flag::SURRENDER),
    (hard_cell(15, 9), flag::SURRENDER),
    (hard_cell(15, 1), flag::SURRENDER),
];

/// Average decks remaining over the rounds dealt before the cut card.
pub fn mid_depth_decks(r: &Rules) -> f64 {
    r.decks as f64 * (1.0 + (1.0 - r.penetration)) / 2.0
}

/// Typical exact count value inside an integer bin, given how the bin was rounded.
pub fn representative(cs: &CountSystem, v: i32) -> f64 {
    if cs.metric == CountMetric::RunningCount {
        let s = cs.scale();
        return v as f64 + (s - 1) as f64 / (2.0 * s as f64);
    }
    match cs.rounding {
        TcRounding::Floor => v as f64 + 0.5,
        TcRounding::Truncate => {
            if v > 0 {
                v as f64 + 0.5
            } else if v < 0 {
                v as f64 - 0.5
            } else {
                0.0
            }
        }
        TcRounding::Round => v as f64,
    }
}

/// Converts a betting/playing count value to the balanced-equivalent true count at mid depth.
pub fn tc_equivalent(cs: &CountSystem, r: &Rules, metric_value: f64) -> f64 {
    let d_mid = mid_depth_decks(r);
    let rc = if cs.metric == CountMetric::TrueCount { metric_value * d_mid } else { metric_value };
    (rc - cs.irc(r.decks) - cs.imbalance_per_deck() * (r.decks as f64 - d_mid)) / d_mid
}

/// Expected rank composition of the unseen cards when the (balanced-equivalent) true count equals `tc_eq`,
/// from the linear regression of composition on the count: q_r = p_r (1 - T (t_r - m) / K).
pub fn shifted_shoe(cs: &CountSystem, tc_eq: f64, decks_remaining: f64) -> [f64; 11] {
    let m = cs.imbalance_per_deck() / 52.0;
    let mut k = 0.0;
    for code in 0..20 {
        let rank = (code >> 1) + 1;
        let per_deck = if rank == 10 { 8.0 } else { 2.0 };
        let d = cs.tag_of_code(code) - m;
        k += per_deck * d * d;
    }
    let mut comp = [0.0; 11];
    let mut max_dev = 0.0;
    for r in 1..=10 {
        max_dev = dmax(max_dev, (cs.rank_tag(r) - m).abs());
    }
    let mut t = tc_eq;
    if k > 1e-12 && max_dev > 1e-12 {
        let lim = 0.9 * k / max_dev;
        if t > lim {
            t = lim;
        } else if t < -lim {
            t = -lim;
        }
    }
    for (r, slot) in comp.iter_mut().enumerate().skip(1) {
        let pr = (if r == 10 { 16.0 } else { 4.0 }) / 52.0;
        let mut f = if k > 1e-12 { 1.0 - t * (cs.rank_tag(r) - m) / k } else { 1.0 };
        if f < 0.02 {
            f = 0.02;
        }
        *slot = pr * 52.0 * decks_remaining * f;
    }
    comp
}

pub fn key_of(r: &Rules, cs: &CountSystem, sc: &StrategyConfig) -> String {
    format!("{}#{}#{}", r.key(), cs.key(), sc.key())
}

pub fn build(rules: &Rules, cs: &CountSystem, sc: &StrategyConfig) -> PlayTable {
    const CPB: usize = CELLS_PER_BIN;
    let mut flags = vec![0u8; NUM_BINS * CPB];
    let mut insure = vec![false; NUM_BINS];
    let mut bin_tc_eq = vec![f64::NAN; NUM_BINS];
    let mut bin_ev = vec![f64::NAN; NUM_BINS];

    let mut engine = CaEngine::new(rules);
    let basic_result = engine.evaluate(&full_shoe(rules.decks as f64));

    // The C# version takes min/max of the range and clamps each end once; clamping both ends here as well keeps a
    // range that lies entirely outside the bins usable instead of failing.
    let lo = sc.index_min.min(sc.index_max).clamp(BIN_MIN, BIN_MAX);
    let hi = sc.index_min.max(sc.index_max).clamp(BIN_MIN, BIN_MAX);
    let per_bin: Option<Vec<CaResult>> = if sc.mode != IndexMode::BasicOnly {
        let d_mid = mid_depth_decks(rules);
        let shoes: Vec<[f64; 11]> =
            (lo..=hi).map(|v| shifted_shoe(cs, tc_equivalent(cs, rules, representative(cs, v)), d_mid)).collect();
        Some(evaluate_all(rules, &shoes, engine))
    } else {
        None
    };

    for b in 0..NUM_BINS {
        let v = BIN_MIN + b as i32;
        let vc = v.clamp(lo, hi);
        let src = per_bin.as_ref().map(|p| &p[(vc - lo) as usize]);
        if let Some(src) = src {
            bin_tc_eq[b] = tc_equivalent(cs, rules, representative(cs, vc));
            bin_ev[b] = src.player_ev;
        }
        let off = b * CPB;
        let row = &mut flags[off..off + CPB];
        match (sc.mode, src) {
            (IndexMode::FullIndices, Some(src)) => {
                row.copy_from_slice(&src.flags);
                insure[b] = src.insure;
            }
            (IndexMode::Illustrious18 | IndexMode::I18Fab4, Some(src)) => {
                row.copy_from_slice(&basic_result.flags);
                for &(cell, mask) in &I18 {
                    merge(row, cell, src.flags[cell], mask);
                }
                if sc.mode == IndexMode::I18Fab4 && rules.surrender != SurrenderRule::None {
                    for &(cell, mask) in &FAB4 {
                        merge(row, cell, src.flags[cell], mask);
                    }
                }
                insure[b] = src.insure;
            }
            _ => {
                row.copy_from_slice(&basic_result.flags);
                insure[b] = false;
            }
        }
    }

    let mut pt = PlayTable {
        flags,
        insure,
        basic_result,
        bin_tc_eq,
        bin_ev,
        key: key_of(rules, cs, sc),
        per_bin,
        per_bin_min: if sc.mode != IndexMode::BasicOnly { lo } else { 0 },
    };
    for o in &sc.overrides {
        apply_override(&mut pt, o, rules);
    }
    pt
}

/// Runs the analysis for every composition, on all cores where threads exist (the results do not depend on it).
#[cfg(not(target_arch = "wasm32"))]
fn evaluate_all(rules: &Rules, shoes: &[[f64; 11]], engine: CaEngine) -> Vec<CaResult> {
    let threads = std::thread::available_parallelism().map_or(1, |n| n.get()).min(shoes.len()).max(1);
    if threads == 1 {
        let mut engine = engine;
        return shoes.iter().map(|s| engine.evaluate(s)).collect();
    }
    let per = shoes.len().div_ceil(threads);
    std::thread::scope(|scope| {
        let parts: Vec<_> = shoes
            .chunks(per)
            .map(|part| {
                scope.spawn(move || {
                    let mut engine = CaEngine::new(rules);
                    part.iter().map(|s| engine.evaluate(s)).collect::<Vec<_>>()
                })
            })
            .collect();
        parts.into_iter().flat_map(|p| p.join().expect("analysis thread")).collect()
    })
}

#[cfg(target_arch = "wasm32")]
fn evaluate_all(_rules: &Rules, shoes: &[[f64; 11]], mut engine: CaEngine) -> Vec<CaResult> {
    shoes.iter().map(|s| engine.evaluate(s)).collect()
}

fn merge(flags: &mut [u8], idx: usize, src: u8, mask: u8) {
    flags[idx] = (flags[idx] & !mask) | (src & mask);
}

fn apply_override(pt: &mut PlayTable, o: &IndexOverride, rules: &Rules) {
    if !o.enabled {
        return;
    }
    let positive =
        matches!(o.play, PlayKind::Stand | PlayKind::Double | PlayKind::Split | PlayKind::Surrender | PlayKind::Insure);
    for b in 0..NUM_BINS {
        let v = (BIN_MIN + b as i32) as f64;
        let cond = if o.at_or_above { v >= o.index - 1e-9 } else { v <= o.index + 1e-9 };
        let val = if positive { cond } else { !cond };
        if o.kind == HandKind::Insurance || o.play == PlayKind::Insure || o.play == PlayKind::NoInsure {
            pt.insure[b] = val && rules.insurance;
            continue;
        }
        if o.upcard < 1 || o.upcard > 10 {
            return;
        }
        let up = o.upcard as usize;
        let cell = match o.kind {
            HandKind::Pair => {
                if o.total < 1 || o.total > 10 {
                    return;
                }
                pair_cell(o.total as usize, up)
            }
            HandKind::Soft => {
                if o.total < 12 || o.total > 21 {
                    return;
                }
                soft_cell(o.total as usize, up)
            }
            _ => {
                if o.total < 4 || o.total > 21 {
                    return;
                }
                hard_cell(o.total as usize, up)
            }
        };
        let mask = match o.play {
            PlayKind::Stand | PlayKind::Hit => flag::STAND,
            PlayKind::Double | PlayKind::NoDouble => flag::DOUBLE,
            PlayKind::Split | PlayKind::NoSplit => flag::SPLIT,
            _ => flag::SURRENDER,
        };
        let idx = b * CELLS_PER_BIN + cell;
        if val {
            pt.flags[idx] |= mask;
        } else {
            pt.flags[idx] &= !mask;
        }
    }
}

/// Chart code for a cell's flags: H, S, D, Ds, P, R, Rs, Rp; pairs without split return "".
pub fn action_code(f: u8, pair_cell: bool) -> &'static str {
    if pair_cell {
        if (f & flag::SURRENDER) != 0 {
            return if (f & flag::SPLIT) != 0 { "Rp" } else { "R" };
        }
        return if (f & flag::SPLIT) != 0 { "P" } else { "" };
    }
    if (f & flag::SURRENDER) != 0 {
        return if (f & flag::STAND) != 0 { "Rs" } else { "R" };
    }
    if (f & flag::DOUBLE) != 0 {
        return if (f & flag::STAND) != 0 { "Ds" } else { "D" };
    }
    if (f & flag::STAND) != 0 { "S" } else { "H" }
}

fn play_name(mask: u8, on: bool) -> &'static str {
    match mask {
        flag::STAND => {
            if on {
                "Stand"
            } else {
                "Hit"
            }
        }
        flag::DOUBLE => {
            if on {
                "Double"
            } else {
                "No double"
            }
        }
        flag::SPLIT => {
            if on {
                "Split"
            } else {
                "No split"
            }
        }
        _ => {
            if on {
                "Surrender"
            } else {
                "No surrender"
            }
        }
    }
}

pub fn hand_name(kind: HandKind, total: i32) -> String {
    match kind {
        HandKind::Soft => format!("A,{}", if total - 11 == 1 { "A".to_string() } else { (total - 11).to_string() }),
        HandKind::Pair => {
            let r = cards::rank_name(total.max(0) as usize);
            format!("{r},{r}")
        }
        HandKind::Insurance => "Insurance".to_string(),
        HandKind::Hard => total.to_string(),
    }
}

fn signed(v: i32) -> String {
    if v > 0 { format!("+{v}") } else { v.to_string() }
}

#[allow(clippy::too_many_arguments)]
fn describe(
    list: &mut Vec<IndexInfo>,
    pt: &PlayTable,
    kind: HandKind,
    total: usize,
    up: usize,
    mask: u8,
    lo: i32,
    hi: i32,
) {
    let cell = match kind {
        HandKind::Soft => soft_cell(total, up),
        HandKind::Pair => pair_cell(total, up),
        _ => hard_cell(total, up),
    };
    let basic = (pt.basic()[cell] & mask) != 0;
    let (mut min_d, mut max_d, mut count) = (i32::MAX, i32::MIN, 0);
    for v in lo..=hi {
        let f = (pt.flags_at(v, cell) & mask) != 0;
        if f != basic {
            count += 1;
            min_d = min_d.min(v);
            max_d = max_d.max(v);
        }
    }
    if count == 0 {
        return;
    }
    let (at_or_above, threshold, condition) = if max_d == hi && (min_d != lo || min_d > 0) {
        (true, min_d, format!("at {} or more", signed(min_d)))
    } else if min_d == lo && max_d != hi {
        (false, max_d, format!("at {} or less", signed(max_d)))
    } else if min_d == lo && max_d == hi {
        (true, lo, "always".to_string())
    } else {
        (true, min_d, format!("from {} to {}", signed(min_d), signed(max_d)))
    };
    list.push(IndexInfo {
        kind,
        total: total as i32,
        upcard: up as i32,
        hand: format!("{} v {}", hand_name(kind, total as i32), cards::rank_name(up)),
        play: play_name(mask, !basic).to_string(),
        condition,
        threshold: threshold as f64,
        at_or_above,
        irregular: count != max_d - min_d + 1 || (min_d != lo && max_d != hi),
    });
}

/// Lists every decision that differs from basic strategy somewhere in [lo, hi].
pub fn describe_indices(pt: &PlayTable, lo: i32, hi: i32) -> Vec<IndexInfo> {
    let mut list = Vec::new();
    let mut min_d = i32::MAX;
    let mut count = 0;
    for v in lo..=hi {
        if pt.insure_at(v) {
            count += 1;
            min_d = min_d.min(v);
        }
    }
    if count > 0 {
        list.push(IndexInfo {
            kind: HandKind::Insurance,
            total: 0,
            upcard: 1,
            hand: "Insurance".to_string(),
            play: "Insure".to_string(),
            condition: format!("at {} or more", signed(min_d)),
            threshold: min_d as f64,
            at_or_above: true,
            irregular: false,
        });
    }
    for t in (4..=20).rev() {
        for &u in &cards::CHART_UP_ORDER {
            for mask in [flag::STAND, flag::DOUBLE, flag::SURRENDER] {
                describe(&mut list, pt, HandKind::Hard, t, u, mask, lo, hi);
            }
        }
    }
    for t in (13..=20).rev() {
        for &u in &cards::CHART_UP_ORDER {
            for mask in [flag::STAND, flag::DOUBLE, flag::SURRENDER] {
                describe(&mut list, pt, HandKind::Soft, t, u, mask, lo, hi);
            }
        }
    }
    for r in 1..=10 {
        for &u in &cards::CHART_UP_ORDER {
            for mask in [flag::SPLIT, flag::SURRENDER] {
                describe(&mut list, pt, HandKind::Pair, r, u, mask, lo, hi);
            }
        }
    }
    list
}
