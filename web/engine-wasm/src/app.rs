//! The C# app's helpers around the engine (AppState.cs, CountPage.cs): count scale, default ramp and index ranges,
//! ramp presets, and the betting and insurance correlations shown on the count page.

use calcbj_engine::strategy::{BIN_MAX, BIN_MIN, mid_depth_decks, tc_equivalent};
use calcbj_engine::*;
use serde_json::{Value, json};
use std::cell::RefCell;

/// The "(custom ramp)" entry after the presets.
pub const CUSTOM_PRESET: i32 = 8;

/// Units per Hi-Lo true count: <=0, 1, 2, 3, 4, 5, >=6 (the value for <=0 applies to every lower count).
const PRESET_UNITS: [[i32; 7]; 8] = [
    [1, 1, 1, 1, 1, 1, 1],
    [1, 1, 2, 3, 4, 4, 4],
    [1, 1, 2, 4, 6, 8, 8],
    [1, 2, 4, 6, 8, 10, 12],
    [1, 2, 4, 8, 12, 16, 16],
    [1, 4, 8, 12, 16, 20, 24],
    [1, 2, 4, 6, 8, 10, 12],
    [0, 2, 4, 6, 8, 10, 12],
];

/// How many of this count's true-count units equal one Hi-Lo true count (spread of the TC distribution).
pub fn factor(cs: &CountSystem) -> f64 {
    let m = cs.imbalance_per_deck() / 52.0;
    let mut k = 0.0;
    for code in 0..20 {
        let rank = (code >> 1) + 1;
        let d = cs.tag_of_code(code) - m;
        k += (if rank == 10 { 8.0 } else { 2.0 }) * d * d;
    }
    if k > 0.0 { (k / 40.0).sqrt() } else { 1.0 }
}

pub fn hi_lo_equivalent(cs: &CountSystem, r: &Rules, metric_value: i32) -> f64 {
    tc_equivalent(cs, r, metric_value as f64) / factor(cs)
}

/// Count value (in the count's own metric) that corresponds to a Hi-Lo true count.
pub fn metric_for(cs: &CountSystem, r: &Rules, hi_lo: f64) -> f64 {
    let d_mid = mid_depth_decks(r);
    let rc = hi_lo * factor(cs) * d_mid + cs.irc(r.decks) + cs.imbalance_per_deck() * (r.decks as f64 - d_mid);
    if cs.metric == CountMetric::TrueCount { rc / d_mid } else { rc }
}

/// Ramp rows covering about Hi-Lo -3..+8 in this count, keeping the bets of `old` where the rows overlap.
pub fn reset_ramp_range(cs: &CountSystem, r: &Rules, old: Option<&BetRamp>) -> BetRamp {
    let mut lo = metric_for(cs, r, -3.0).floor() as i32;
    let mut hi = metric_for(cs, r, 8.0).ceil() as i32;
    if hi - lo > 44 {
        lo = metric_for(cs, r, -2.0).floor() as i32;
        hi = lo + 44;
    }
    if hi <= lo {
        hi = lo + 1;
    }
    let (lo, hi) = (lo.max(BIN_MIN), hi.min(BIN_MAX));
    match old {
        Some(o) => o.resize(lo, hi),
        None => BetRamp::create(lo, hi, 1.0),
    }
}

/// Index range covering about Hi-Lo -10..+10 in this count.
pub fn index_range(cs: &CountSystem, r: &Rules) -> (i32, i32) {
    let lo = metric_for(cs, r, -10.0).floor() as i32;
    let hi = metric_for(cs, r, 10.0).ceil() as i32;
    (lo.max(BIN_MIN), hi.min(BIN_MAX))
}

fn preset_units(preset: i32, hi_lo_tc: f64) -> f64 {
    let tc = (hi_lo_tc + 1e-9).floor() as i32;
    let t = &PRESET_UNITS[preset.clamp(0, 7) as usize];
    if preset == 6 && tc <= -1 {
        return 0.0;
    }
    f64::from(t[tc.clamp(0, 6) as usize])
}

/// Fills the ramp from a preset (an unknown or custom preset means the 1–12 spread); returns the preset used.
pub fn apply_preset(cs: &CountSystem, r: &Rules, ramp: &mut BetRamp, preset: i32) -> i32 {
    let preset = if (0..CUSTOM_PRESET).contains(&preset) { preset } else { 3 };
    for v in ramp.min..=ramp.max {
        let i = (v - ramp.min) as usize;
        ramp.units[i] = preset_units(preset, hi_lo_equivalent(cs, r, v));
        ramp.hands[i] = 1;
    }
    preset
}

thread_local! {
    static EOR: RefCell<Option<(String, [f64; 11])>> = const { RefCell::new(None) };
}

/// Change in player expectation from removing one card of each rank (index 1..=10), cached per rule set.
fn effects_of_removal(r: &Rules) -> [f64; 11] {
    let key = r.key();
    EOR.with_borrow_mut(|slot| {
        if let Some((k, e)) = slot.as_ref()
            && *k == key
        {
            return *e;
        }
        let mut engine = CaEngine::new(r);
        let full = full_shoe(r.decks as f64);
        let base = engine.evaluate(&full).player_ev;
        let mut e = [0.0; 11];
        for (k, slot) in e.iter_mut().enumerate().skip(1) {
            let mut c = full;
            c[k] -= 1.0;
            *slot = engine.evaluate(&c).player_ev - base;
        }
        *slot = Some((key, e));
        e
    })
}

fn corr(x: &[f64; 11], y: &[f64; 11], w: &[f64; 11]) -> f64 {
    let (mut sw, mut mx, mut my) = (0.0, 0.0, 0.0);
    for k in 1..=10 {
        sw += w[k];
        mx += w[k] * x[k];
        my += w[k] * y[k];
    }
    mx /= sw;
    my /= sw;
    let (mut sxy, mut sxx, mut syy) = (0.0, 0.0, 0.0);
    for k in 1..=10 {
        let (dx, dy) = (x[k] - mx, y[k] - my);
        sxy += w[k] * dx * dy;
        sxx += w[k] * dx * dx;
        syy += w[k] * dy * dy;
    }
    if sxx > 0.0 && syy > 0.0 { sxy / (sxx * syy).sqrt() } else { 0.0 }
}

pub fn count_info(r: &Rules, cs: &CountSystem) -> Value {
    let eor = effects_of_removal(r);
    let (mut tags, mut insurance, mut weight) = ([0.0; 11], [0.0; 11], [0.0; 11]);
    for k in 1..=10 {
        tags[k] = cs.rank_tag(k);
        weight[k] = if k == 10 { 4.0 } else { 1.0 };
        insurance[k] = if k == 10 { -9.0 / 4.0 } else { 1.0 };
    }
    json!({
        "scale": cs.scale(),
        "imbalance": cs.imbalance_per_deck(),
        "balanced": cs.is_balanced(),
        "level": cs.level(),
        "irc": cs.irc(r.decks),
        "tagText": cs.tag_text(),
        "key": cs.key(),
        "factor": factor(cs),
        "bettingCorrelation": corr(&tags, &eor, &weight),
        "insuranceCorrelation": corr(&tags, &insurance, &weight),
    })
}
