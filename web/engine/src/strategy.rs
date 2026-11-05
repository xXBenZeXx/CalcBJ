//! Strategy tables for every count, index generation and index descriptions (C#: Strategy.cs).
//!
//! Cell layout per count bin: hard totals [0..=21] x 10 upcards, soft totals [0..=21] x 10, pairs [A..=T] x 10.

use crate::ca::{CaEngine, CaResult, CountModel, flag, full_shoe};
use crate::num::dmax;
use crate::sim::metric_value;
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

    let mut engine = CaEngine::new(rules);
    let mut basic_result = engine.evaluate(&full_shoe(rules.decks as f64));
    basic_result.player_ev = engine.exact_off_top(&basic_result);

    // The C# version takes min/max of the range and clamps each end once; clamping both ends here as well keeps a
    // range that lies entirely outside the bins usable instead of failing.
    let lo = sc.index_min.min(sc.index_max).clamp(BIN_MIN, BIN_MAX);
    let hi = sc.index_min.max(sc.index_max).clamp(BIN_MIN, BIN_MAX);
    let per_bin: Option<Vec<CaResult>> = if sc.mode != IndexMode::BasicOnly {
        let d_mid = mid_depth_decks(rules);
        let mut tags = [0.0; 11];
        for (r, t) in tags.iter_mut().enumerate().skip(1) {
            *t = cs.rank_tag(r);
        }
        // Red and black cards of a rank sit half a tag difference either side of the rank's average tag.
        let tag_var = cs.red_tags.map(|red| {
            let mut tv = [0.0; 11];
            for (r, v) in tv.iter_mut().enumerate().skip(1) {
                let h = 0.5 * (red[r - 1] - cs.tags[r - 1]);
                *v = h * h;
            }
            tv
        });
        let total_tags = cs.imbalance_per_deck() * rules.decks as f64;
        let irc = cs.irc(rules.decks);
        let (unseen, unseen_mean) = bin_moments(rules, cs);
        // The count when the player decides includes the upcard and their cards (see CountModel).
        let models: Vec<CountModel> = (lo..=hi)
            .map(|v| {
                let b = (v - BIN_MIN) as usize;
                if !unseen[b].is_nan() {
                    CountModel { tags, tag_var, unseen: unseen[b], unseen_tags: unseen_mean[b] * unseen[b] }
                } else {
                    // A count this game never reaches: extend from the bin's representative value at mid depth.
                    let rep = representative(cs, v);
                    let rc = if cs.metric == CountMetric::TrueCount { rep * d_mid } else { rep };
                    CountModel { tags, tag_var, unseen: 52.0 * d_mid, unseen_tags: total_tags - (rc - irc) }
                }
            })
            .collect();
        Some(evaluate_all(rules, &full_shoe(rules.decks as f64), &models, engine))
    } else {
        None
    };

    for b in 0..NUM_BINS {
        let v = BIN_MIN + b as i32;
        let vc = v.clamp(lo, hi);
        let src = per_bin.as_ref().map(|p| &p[(vc - lo) as usize]);
        if src.is_some() {
            bin_tc_eq[b] = tc_equivalent(cs, rules, representative(cs, vc));
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
        key: key_of(rules, cs, sc),
        per_bin,
        per_bin_min: if sc.mode != IndexMode::BasicOnly { lo } else { 0 },
    };
    for o in &sc.overrides {
        apply_override(&mut pt, o, rules);
    }
    pt
}

/// Runs the analysis for every count model, on all cores where threads exist (the results do not depend on it).
#[cfg(not(target_arch = "wasm32"))]
fn evaluate_all(rules: &Rules, full: &[f64; 11], models: &[CountModel], engine: CaEngine) -> Vec<CaResult> {
    let threads = std::thread::available_parallelism().map_or(1, |n| n.get()).min(models.len()).max(1);
    if threads == 1 {
        let mut engine = engine;
        return models.iter().map(|m| engine.evaluate_with(full, Some(m))).collect();
    }
    let per = models.len().div_ceil(threads);
    std::thread::scope(|scope| {
        let parts: Vec<_> = models
            .chunks(per)
            .map(|part| {
                scope.spawn(move || {
                    let mut engine = CaEngine::new(rules);
                    part.iter().map(|m| engine.evaluate_with(full, Some(m))).collect::<Vec<_>>()
                })
            })
            .collect();
        parts.into_iter().flat_map(|p| p.join().expect("analysis thread")).collect()
    })
}

#[cfg(target_arch = "wasm32")]
fn evaluate_all(_rules: &Rules, full: &[f64; 11], models: &[CountModel], mut engine: CaEngine) -> Vec<CaResult> {
    models.iter().map(|m| engine.evaluate_with(full, Some(m))).collect()
}

/// Where the decisions of each count bin fall in the shoe: the expected number of unseen cards and the expected
/// average tag of the unseen cards, for decisions spread evenly over the dealt part of the shoe, with the running
/// count binned exactly as the simulator does. Running counts move in whole cards and true counts cluster towards
/// zero, so a bin's midpoint is a poor stand-in for the counts in it, most of all with one or two decks. Bins that
/// never occur get NaN (C#: BinMoments).
pub fn bin_moments(rules: &Rules, cs: &CountSystem) -> (Vec<f64>, Vec<f64>) {
    let n_total = rules.total_cards();
    let scale = cs.scale();
    let irc = (cs.irc(rules.decks) * scale as f64).round_ties_even() as i32;
    let (mut mu, mut m2) = (0.0, 0.0);
    for code in 0..20 {
        let rank = (code >> 1) + 1;
        let share = (if rank == 10 { 8.0 } else { 2.0 }) / 52.0;
        let t = (cs.tag_of_code(code) * scale as f64).round_ties_even();
        mu += share * t;
        m2 += share * t * t;
    }
    let var1 = m2 - mu * mu;
    let total = mu * n_total as f64;
    let mut w = vec![0.0; NUM_BINS];
    let mut wn = vec![0.0; NUM_BINS];
    let mut wm = vec![0.0; NUM_BINS];
    let last = 3.max((n_total - 2).min((n_total as f64 * rules.penetration).round_ties_even() as i32));
    for k in 3..=last {
        // Seen cards' scaled tag sum: k cards drawn without replacement from the shoe.
        let n = n_total - k;
        let mean = k as f64 * mu;
        let sd = dmax(0.0, k as f64 * var1 * n as f64 / (n_total as f64 - 1.0)).sqrt();
        let s0 = (mean - 6.0 * sd).floor() as i32;
        let s1 = (mean + 6.0 * sd).ceil() as i32;
        let weight = |s: i32| {
            if sd > 0.0 {
                (-0.5 * (s as f64 - mean) * (s as f64 - mean) / (sd * sd)).exp()
            } else if s == s0 {
                1.0
            } else {
                0.0
            }
        };
        let mut z = 0.0;
        for s in s0..=s1 {
            z += weight(s);
        }
        if z <= 0.0 {
            continue;
        }
        for s in s0..=s1 {
            let pr = weight(s) / z;
            if pr <= 0.0 {
                continue;
            }
            // The hole card is dealt but unseen: the simulator's count uses the cards left after it.
            let v = metric_value(cs, scale, irc + s, n - 1).clamp(BIN_MIN, BIN_MAX);
            let b = (v - BIN_MIN) as usize;
            w[b] += pr;
            wn[b] += pr * n as f64;
            wm[b] += pr * (total - s as f64) / scale as f64 / n as f64;
        }
    }
    let unseen = (0..NUM_BINS).map(|b| if w[b] > 1e-12 { wn[b] / w[b] } else { f64::NAN }).collect();
    let unseen_mean = (0..NUM_BINS).map(|b| if w[b] > 1e-12 { wm[b] / w[b] } else { f64::NAN }).collect();
    (unseen, unseen_mean)
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
    } else if min_d == lo && max_d == hi && count == hi - lo + 1 {
        (true, lo, "always".to_string())
    } else if min_d == lo && max_d == hi {
        // Deviates at both ends of the range: name the two ends, e.g. "at -4 or less, and at +10 or more".
        let deviates = |v: i32| ((pt.flags_at(v, cell) & mask) != 0) != basic;
        let mut low_end = lo;
        while low_end < hi && deviates(low_end + 1) {
            low_end += 1;
        }
        let mut high_start = hi;
        while high_start > lo && deviates(high_start - 1) {
            high_start -= 1;
        }
        (false, low_end, format!("at {} or less, and at {} or more", signed(low_end), signed(high_start)))
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
