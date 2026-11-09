//! The C# app's helpers around the engine (AppState.cs, CountPage.cs): count scale, default ramp and index ranges,
//! ramp presets, and the betting and insurance correlations shown on the count page.

use calcbj_engine::strategy::{BIN_MAX, BIN_MIN, mid_depth_decks, representative, tc_equivalent};
use calcbj_engine::*;
use serde_json::{Value, json};

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

/// The Hi-Lo true count whose preset bet a count row gets: the row's typical exact count (`representative`), converted
/// to Hi-Lo and rounded the way the count rounds its own true count (C#: CountScale.PresetHiLo). Mapping the row's lower
/// edge instead made counts whose factor isn't 1 (Wong Halves, Silver Fox, Zen ...) bet a row late. Hi-Lo rows are
/// unchanged.
pub fn preset_hi_lo(cs: &CountSystem, r: &Rules, metric_value: i32) -> f64 {
    let hl = tc_equivalent(cs, r, representative(cs, metric_value)) / factor(cs);
    if cs.metric == CountMetric::TrueCount {
        match cs.rounding {
            TcRounding::Round => return hl.round(),
            TcRounding::Truncate => return ((hl + if hl >= 0.0 { 1e-9 } else { -1e-9 }) as i32) as f64,
            TcRounding::Floor => {}
        }
    }
    hl // preset_units floors it
}

/// Count value (in the count's own metric) that corresponds to a Hi-Lo true count.
pub fn metric_for(cs: &CountSystem, r: &Rules, hi_lo: f64) -> f64 {
    let d_mid = mid_depth_decks(r);
    let rc = hi_lo * factor(cs) * d_mid + cs.irc(r.decks) + cs.imbalance_per_deck() * (r.decks as f64 - d_mid);
    if cs.metric == CountMetric::TrueCount { rc / d_mid } else { rc }
}

/// Most ramp rows: running counts of level-2 systems span many (Uston SS with 8 decks needs about 110 to reach the
/// equivalent of Hi-Lo +8, where the presets reach their top bet).
pub const MAX_RAMP_ROWS: i32 = 121;

/// Ramp rows covering about Hi-Lo -3..+8 in this count, keeping the bets of `old` where the rows overlap.
pub fn reset_ramp_range(cs: &CountSystem, r: &Rules, old: Option<&BetRamp>) -> BetRamp {
    let mut lo = metric_for(cs, r, -3.0).floor() as i32;
    let mut hi = metric_for(cs, r, 8.0).ceil() as i32;
    if hi - lo > MAX_RAMP_ROWS - 1 {
        lo = metric_for(cs, r, -2.0).floor() as i32;
        hi = lo + MAX_RAMP_ROWS - 1;
    }
    // Inside the count bins, and at least two rows: an extreme starting count can put the whole range beyond them.
    let lo = lo.clamp(BIN_MIN, BIN_MAX - 1);
    let hi = hi.clamp(lo + 1, BIN_MAX);
    match old {
        Some(o) if o.rows() >= 1 && o.units.len() == o.rows() && o.hands.len() == o.rows() => o.resize(lo, hi),
        _ => BetRamp::create(lo, hi, 1.0),
    }
}

/// Index range covering about Hi-Lo -10..+10 in this count.
pub fn index_range(cs: &CountSystem, r: &Rules) -> (i32, i32) {
    let lo = metric_for(cs, r, -10.0).floor() as i32;
    let hi = metric_for(cs, r, 10.0).ceil() as i32;
    (lo.clamp(BIN_MIN, BIN_MAX), hi.clamp(BIN_MIN, BIN_MAX))
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
        ramp.units[i] = preset_units(preset, preset_hi_lo(cs, r, v));
        ramp.hands[i] = 1;
    }
    preset
}

/// The ramp for the same count under other rules (another number of decks): a preset is applied again; a custom
/// ramp is carried over row by row, each row taking the bet of the old row with the nearest Hi-Lo equivalent
/// (C#: AppState.RampForRules).
pub fn ramp_for_rules(cs: &CountSystem, from: &Rules, to: &Rules, ramp: &BetRamp, preset: i32) -> BetRamp {
    ramp_for(cs, from, cs, to, ramp, preset)
}

/// The ramp after the count or the rules changed so that a count value means another true count (decks, starting
/// count, conversion, another count altogether): a preset is applied again over the new range; a custom ramp is carried
/// over row by row, each new row taking the bet of the old row with the nearest Hi-Lo equivalent (C#: AppState.RampFor).
pub fn ramp_for(
    cs_from: &CountSystem,
    from: &Rules,
    cs: &CountSystem,
    to: &Rules,
    ramp: &BetRamp,
    preset: i32,
) -> BetRamp {
    let mut r = reset_ramp_range(cs, to, None);
    let custom = !(0..CUSTOM_PRESET).contains(&preset) && ramp.rows() >= 1 && ramp.units.len() == ramp.rows();
    for v in r.min..=r.max {
        let i = (v - r.min) as usize;
        if !custom {
            let preset = if (0..CUSTOM_PRESET).contains(&preset) { preset } else { 3 };
            r.units[i] = preset_units(preset, preset_hi_lo(cs, to, v));
            continue;
        }
        let hl = hi_lo_equivalent(cs, to, v);
        let (mut best, mut best_d) = (ramp.min, f64::MAX);
        for w in ramp.min..=ramp.max {
            let d = (hi_lo_equivalent(cs_from, from, w) - hl).abs();
            if d < best_d {
                best_d = d;
                best = w;
            }
        }
        r.units[i] = ramp.units_at(best);
        r.hands[i] = ramp.hands_at(best);
    }
    r
}

fn corr(x: &[f64; 20], y: &[f64; 20], w: &[f64; 20]) -> f64 {
    let (mut sw, mut mx, mut my) = (0.0, 0.0, 0.0);
    for k in 0..20 {
        sw += w[k];
        mx += w[k] * x[k];
        my += w[k] * y[k];
    }
    mx /= sw;
    my /= sw;
    let (mut sxy, mut sxx, mut syy) = (0.0, 0.0, 0.0);
    for k in 0..20 {
        let (dx, dy) = (x[k] - mx, y[k] - my);
        sxy += w[k] * dx * dy;
        sxx += w[k] * dx * dx;
        syy += w[k] * dy * dy;
    }
    if sxx > 0.0 && syy > 0.0 { sxy / (sxx * syy).sqrt() } else { 0.0 }
}

/// What the count page shows about a count.
///
/// `eor_rank` are the effects of removal by rank (`[1..=10]`) of basic strategy played unchanged, worked out exactly:
/// [`effects_of_removal`], which takes about a second, so the app gets them from a worker and passes them in. Without
/// them there is no betting correlation (null).
pub fn count_info(r: &Rules, cs: &CountSystem, eor_rank: Option<&[f64; 11]>) -> Value {
    // Card by card (red and black separately), so counts with red tags such as Red Seven get their real tags.
    let (mut tags, mut eor, mut insurance, mut weight) = ([0.0; 20], [0.0; 20], [0.0; 20], [0.0; 20]);
    for code in 0..20 {
        let k = (code >> 1) + 1;
        tags[code] = cs.tag_of_code(code);
        eor[code] = eor_rank.map_or(0.0, |e| e[k]);
        weight[code] = if k == 10 { 2.0 } else { 0.5 };
        insurance[code] = if k == 10 { -9.0 / 4.0 } else { 1.0 };
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
        // A running count moves by its factor times the decks left for each Hi-Lo true count (here: at mid-shoe).
        "runningPerHiLo": factor(cs) * mid_depth_decks(r),
        "bettingCorrelation": eor_rank.map(|_| corr(&tags, &eor, &weight)),
        "insuranceCorrelation": corr(&tags, &insurance, &weight),
    })
}

/// The published Hi-Lo index numbers in this count's own scale (C#: CountPage.LoadPublished): unchanged for a count
/// that reads like Hi-Lo (a balanced true count of the same spread, starting at 0); otherwise each one becomes the
/// nearest value of the count that stands for the same true count.
pub fn from_hi_lo(cs: &CountSystem, r: &Rules, values: &[f64]) -> Vec<f64> {
    let as_published = cs.metric == CountMetric::TrueCount
        && cs.is_balanced()
        && cs.irc_base == 0.0
        && cs.irc_per_deck == 0.0
        && (factor(cs) - 1.0).abs() < 0.005;
    values
        .iter()
        .map(|&v| if as_published { v } else { metric_for(cs, r, v).round().clamp(BIN_MIN as f64, BIN_MAX as f64) })
        .collect()
}
