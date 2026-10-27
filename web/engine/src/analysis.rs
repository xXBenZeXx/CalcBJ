//! Bet-ramp evaluation, risk of ruin and bet optimisation (C#: Analysis.cs).

use crate::num::{dmax, dmin};
use crate::sim::SimData;

/// Correlation between two hands in the same round, used when the simulation had a single spot.
pub const ASSUMED_CORRELATION: f64 = 0.37;

/// Single-hand statistics at one count value, in units of the initial bet.
#[derive(Clone, Debug, Default, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize), serde(rename_all = "camelCase"))]
pub struct BinStat {
    pub value: i32,
    pub n: i64,
    pub freq: f64,
    pub mean: f64,
    /// E[x^2] for one hand.
    pub m2: f64,
    /// E[x*y] for two hands played in the same round.
    pub cov: f64,
    pub measured_cov: bool,
    /// Variance of the estimate of `mean`.
    pub mean_var: f64,
}

impl BinStat {
    pub fn var(&self) -> f64 {
        self.m2 - self.mean * self.mean
    }
}

/// Bet ramp over a display range: row 0 covers every count at or below `min`, the last row every count at or above
/// `max`.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize))]
pub struct BetRamp {
    pub min: i32,
    pub max: i32,
    pub units: Vec<f64>,
    pub hands: Vec<i32>,
}

impl BetRamp {
    pub fn create(min: i32, max: i32, units: f64) -> BetRamp {
        let n = (max - min + 1).max(0) as usize;
        BetRamp { min, max, units: vec![units; n], hands: vec![1; n] }
    }

    pub fn rows(&self) -> usize {
        (self.max - self.min + 1).max(0) as usize
    }

    pub fn row(&self, v: i32) -> usize {
        (v.min(self.max).max(self.min) - self.min) as usize
    }

    pub fn units_at(&self, v: i32) -> f64 {
        self.units[self.row(v)]
    }

    pub fn hands_at(&self, v: i32) -> i32 {
        self.hands[self.row(v)]
    }

    pub fn resize(&self, min: i32, max: i32) -> BetRamp {
        let mut r = BetRamp::create(min, max, 1.0);
        for v in min..=max {
            let i = (v - min) as usize;
            r.units[i] = self.units_at(v);
            r.hands[i] = self.hands_at(v);
        }
        r
    }

    pub fn max_units(&self) -> f64 {
        self.units.iter().fold(0.0, |m, &u| dmax(m, u))
    }

    pub fn min_positive_units(&self) -> f64 {
        let m = self.units.iter().filter(|&&u| u > 0.0).fold(f64::MAX, |m, &u| dmin(m, u));
        if m == f64::MAX { 0.0 } else { m }
    }
}

/// Non-finite values (N0 of a break-even ramp) serialise as null.
#[derive(Clone, Debug, Default, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize), serde(rename_all = "camelCase"))]
pub struct AnalysisResult {
    pub valid: bool,
    /// Per observed round, in units.
    pub ev: f64,
    pub var: f64,
    pub sd: f64,
    pub m2: f64,
    /// Initial units wagered per observed round.
    pub avg_bet: f64,
    pub played_frac: f64,
    pub avg_bet_played: f64,
    pub edge_pct: f64,
    pub di: f64,
    pub score: f64,
    pub n0: f64,
    pub se_ev: f64,
    pub win_per_hour: f64,
    pub sd_per_hour: f64,
    pub win_per_100: f64,
    pub sd_per_100: f64,
    pub se_win_per_hour: f64,
    pub rounds_per_hour: f64,
    pub unit: f64,
    pub bankroll: f64,
    pub ror: f64,
    pub ce_per_hour: f64,
    pub kelly_unit: f64,
    pub hours_n0: f64,
    pub spread: f64,
    pub used_assumed_cov: bool,
    pub row_freq: Vec<f64>,
    pub row_ev: Vec<f64>,
    pub row_contribution: Vec<f64>,
    pub row_m2: Vec<f64>,
    pub row_n: Vec<i64>,
}

impl AnalysisResult {
    pub fn bankroll_for_ror(&self, ror: f64) -> f64 {
        if self.ev <= 0.0 || ror <= 0.0 || ror >= 1.0 {
            return f64::INFINITY;
        }
        -ror.ln() * self.var / (2.0 * self.ev) * self.unit
    }

    pub fn ror_for_bankroll(&self, dollars: f64) -> f64 {
        if self.ev <= 0.0 {
            return 1.0;
        }
        let b = dollars / self.unit;
        (-2.0 * self.ev * b / self.var).exp()
    }
}

#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct OptimizerSettings {
    pub min_units: f64,
    pub max_units: f64,
    pub increment: f64,
    pub max_hands: i32,
    pub allow_wong_out: bool,
    pub kelly: bool,
    pub kelly_fraction: f64,
    pub bankroll_units: f64,
}

impl Default for OptimizerSettings {
    fn default() -> Self {
        OptimizerSettings {
            min_units: 1.0,
            max_units: 12.0,
            increment: 1.0,
            max_hands: 1,
            allow_wong_out: false,
            kelly: false,
            kelly_fraction: 1.0,
            bankroll_units: 1000.0,
        }
    }
}

pub fn bins(d: &SimData) -> Vec<BinStat> {
    let total: i64 = d.n[..d.num_bins].iter().sum();
    let two = d.spots >= 2;
    (0..d.num_bins)
        .map(|i| {
            let n = d.n[i];
            let mut b = BinStat {
                value: d.bin_min + i as i32,
                n,
                freq: if total > 0 { n as f64 / total as f64 } else { 0.0 },
                ..BinStat::default()
            };
            if n > 0 {
                let nf = n as f64;
                if two {
                    b.mean = (d.sa[i] as f64 + d.sb[i] as f64) / (40.0 * nf);
                    b.m2 = (d.qa[i] as f64 + d.qb[i] as f64) / (800.0 * nf);
                    b.cov = d.ab[i] as f64 / (400.0 * nf);
                    b.measured_cov = true;
                    let v = dmax(0.0, b.m2 - b.mean * b.mean);
                    let c = dmax(0.0, b.cov - b.mean * b.mean);
                    b.mean_var = (v + c) / (2.0 * nf);
                } else {
                    b.mean = d.sa[i] as f64 / (20.0 * nf);
                    b.m2 = d.qa[i] as f64 / (400.0 * nf);
                    let v = dmax(0.0, b.m2 - b.mean * b.mean);
                    b.cov = b.mean * b.mean + ASSUMED_CORRELATION * v;
                    b.mean_var = v / nf;
                }
            }
            b
        })
        .collect()
}

/// Aggregates bins into ramp rows (tails collapse into the end rows).
pub fn rows(bins: &[BinStat], ramp: &BetRamp) -> Vec<BinStat> {
    let mut r: Vec<BinStat> =
        (0..ramp.rows()).map(|i| BinStat { value: ramp.min + i as i32, ..BinStat::default() }).collect();
    for b in bins {
        if b.n == 0 {
            continue;
        }
        let x = &mut r[ramp.row(b.value)];
        x.n += b.n;
        x.freq += b.freq;
        x.mean += b.freq * b.mean;
        x.m2 += b.freq * b.m2;
        x.cov += b.freq * b.cov;
        x.mean_var += b.freq * b.freq * b.mean_var;
        x.measured_cov = b.measured_cov;
    }
    for x in &mut r {
        if x.freq > 0.0 {
            x.mean /= x.freq;
            x.m2 /= x.freq;
            x.cov /= x.freq;
            x.mean_var /= x.freq * x.freq;
        }
    }
    r
}

pub fn evaluate(d: &SimData, ramp: &BetRamp, unit: f64, rounds_per_hour: f64, bankroll: f64) -> AnalysisResult {
    let mut res = AnalysisResult { unit, rounds_per_hour, bankroll, ..AnalysisResult::default() };
    let rows = rows(&bins(d), ramp);
    let nr = rows.len();
    res.row_freq = vec![0.0; nr];
    res.row_ev = vec![0.0; nr];
    res.row_contribution = vec![0.0; nr];
    res.row_m2 = vec![0.0; nr];
    res.row_n = vec![0; nr];
    let (mut ev, mut m2, mut bet, mut played, mut se_var) = (0.0, 0.0, 0.0, 0.0, 0.0);
    for (i, x) in rows.iter().enumerate() {
        res.row_freq[i] = x.freq;
        res.row_ev[i] = x.mean;
        res.row_n[i] = x.n;
        res.row_m2[i] = x.m2;
        let u = ramp.units[i];
        let h = ramp.hands[i].max(1);
        if u <= 0.0 || x.freq <= 0.0 {
            continue;
        }
        if h > 1 && !x.measured_cov {
            res.used_assumed_cov = true;
        }
        let hf = h as f64;
        let e = hf * u * x.mean;
        let q = u * u * (hf * x.m2 + (h * (h - 1)) as f64 * x.cov);
        ev += x.freq * e;
        m2 += x.freq * q;
        bet += x.freq * hf * u;
        played += x.freq;
        res.row_contribution[i] = x.freq * e;
        se_var += x.freq * x.freq * hf * hf * u * u * x.mean_var;
    }
    if bet <= 0.0 {
        return res;
    }
    res.valid = true;
    res.ev = ev;
    res.m2 = m2;
    res.var = dmax(1e-12, m2 - ev * ev);
    res.sd = res.var.sqrt();
    res.avg_bet = bet;
    res.played_frac = played;
    res.avg_bet_played = bet / played;
    res.edge_pct = ev / bet * 100.0;
    res.di = 1000.0 * ev / res.sd;
    res.score = if ev > 0.0 { 1e6 * ev * ev / res.var } else { 0.0 };
    res.n0 = if ev != 0.0 { res.var / (ev * ev) } else { f64::INFINITY };
    res.hours_n0 = res.n0 / rounds_per_hour;
    res.se_ev = se_var.sqrt();
    res.win_per_hour = ev * rounds_per_hour * unit;
    res.sd_per_hour = (res.var * rounds_per_hour).sqrt() * unit;
    res.win_per_100 = ev * 100.0 * unit;
    res.sd_per_100 = (res.var * 100.0).sqrt() * unit;
    res.se_win_per_hour = res.se_ev * rounds_per_hour * unit;
    let b_u = bankroll / unit;
    res.ror = if ev > 0.0 { (-2.0 * ev * b_u / res.var).exp() } else { 1.0 };
    res.ce_per_hour = (ev - res.var / (2.0 * b_u)) * rounds_per_hour * unit;
    res.kelly_unit = if ev > 0.0 { bankroll * ev / m2 } else { 0.0 };
    let (mx, mn) = (ramp.max_units(), ramp.min_positive_units());
    res.spread = if mn > 0.0 { mx / mn } else { 0.0 };
    res
}

// ---------------- Normal distribution helpers ----------------

pub fn phi(x: f64) -> f64 {
    0.5 * erfc(-x / 2f64.sqrt())
}

fn erfc(x: f64) -> f64 {
    // Numerical Recipes erfc (Chebyshev), fractional error < 1.2e-7
    let z = x.abs();
    let t = 1.0 / (1.0 + 0.5 * z);
    let ans = t
        * (-z * z - 1.26551223
            + t * (1.00002368
                + t * (0.37409196
                    + t * (0.09678418
                        + t * (-0.18628806
                            + t * (0.27886807
                                + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))))
            .exp();
    if x >= 0.0 { ans } else { 2.0 - ans }
}

fn log_phi(x: f64) -> f64 {
    if x > -5.0 {
        return dmax(1e-300, phi(x)).ln();
    }
    // asymptotic tail
    let x2 = x * x;
    -0.5 * x2 - (-x).ln() - 0.5 * (2.0 * std::f64::consts::PI).ln() + (1.0 - 1.0 / x2 + 3.0 / (x2 * x2)).ln()
}

/// Inverse standard normal CDF (Acklam).
#[allow(clippy::excessive_precision)] // the published coefficients, as in the C# version
pub fn phi_inv(p: f64) -> f64 {
    if p <= 0.0 {
        return f64::NEG_INFINITY;
    }
    if p >= 1.0 {
        return f64::INFINITY;
    }
    const A: [f64; 6] = [
        -3.969683028665376e+01,
        2.209460984245205e+02,
        -2.759285104469687e+02,
        1.383577518672690e+02,
        -3.066479806614716e+01,
        2.506628277459239e+00,
    ];
    const B: [f64; 5] = [
        -5.447609879822406e+01,
        1.615858368580409e+02,
        -1.556989798598866e+02,
        6.680131188771972e+01,
        -1.328068155288572e+01,
    ];
    const C: [f64; 6] = [
        -7.784894002430293e-03,
        -3.223964580411365e-01,
        -2.400758277161838e+00,
        -2.549732539343734e+00,
        4.374664141464968e+00,
        2.938163982698783e+00,
    ];
    const D: [f64; 4] = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
    let tail = |q: f64| {
        (((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5])
            / ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1.0)
    };
    if p < 0.02425 {
        return tail((-2.0 * p.ln()).sqrt());
    }
    if p > 1.0 - 0.02425 {
        return -tail((-2.0 * (1.0 - p).ln()).sqrt());
    }
    let q = p - 0.5;
    let r = q * q;
    (((((A[0] * r + A[1]) * r + A[2]) * r + A[3]) * r + A[4]) * r + A[5]) * q
        / (((((B[0] * r + B[1]) * r + B[2]) * r + B[3]) * r + B[4]) * r + 1.0)
}

/// Probability of losing the whole bankroll at some point within n rounds (Brownian approximation).
pub fn trip_ruin(mu: f64, var: f64, bankroll: f64, n: f64) -> f64 {
    if n <= 0.0 {
        return 0.0;
    }
    if bankroll <= 0.0 {
        return 1.0;
    }
    let s = (var * n).sqrt();
    let a = (-bankroll - mu * n) / s;
    let b = (-bankroll + mu * n) / s;
    let expo = -2.0 * mu * bankroll / var;
    let second = dmin(700.0, expo + log_phi(b)).exp();
    dmin(1.0, phi(a) + second)
}

/// Probability of being behind after n rounds.
pub fn prob_loss(mu: f64, var: f64, n: f64) -> f64 {
    if n <= 0.0 {
        return 0.5;
    }
    phi(-mu * n / (var * n).sqrt())
}

// ---------------- Bet optimisation ----------------

fn row_gain(x: &BinStat, b: f64, h: i32, lambda: f64) -> f64 {
    if b <= 0.0 {
        return 0.0;
    }
    let hf = h as f64;
    x.freq * (hf * b * x.mean - b * b * (hf * x.m2 + (h * (h - 1)) as f64 * x.cov) / (2.0 * lambda))
}

fn best_bet(x: &BinStat, os: &OptimizerSettings, lambda: f64) -> (f64, i32) {
    let floor_bet = if os.allow_wong_out { 0.0 } else { os.min_units };
    if x.freq <= 0.0 {
        return (floor_bet, 1);
    }
    let (mut bet, mut hands) = (floor_bet, 1);
    let mut best_g = if os.allow_wong_out { 0.0 } else { row_gain(x, os.min_units, 1, lambda) };
    let max_h = os.max_hands.max(1);
    let inc = if os.increment > 0.0 { os.increment } else { 0.01 };
    for h in 1..=max_h {
        let denom = x.m2 + (h - 1) as f64 * x.cov;
        if denom <= 0.0 {
            continue;
        }
        let raw = lambda * x.mean / denom;
        let lo = (raw / inc).floor() * inc;
        let hi = lo + inc;
        for c0 in [os.min_units, os.max_units, lo, hi] {
            let mut c = dmax(os.min_units, dmin(os.max_units, c0));
            c = (c / inc).round_ties_even() * inc;
            if c < os.min_units - 1e-9 {
                c += inc;
            }
            if c > os.max_units + 1e-9 {
                c -= inc;
            }
            let g = row_gain(x, c, h, lambda);
            if g > best_g + 1e-15 {
                best_g = g;
                bet = c;
                hands = h;
            }
        }
    }
    (bet, hands)
}

fn apply(rows: &[BinStat], template: &BetRamp, os: &OptimizerSettings, lambda: f64) -> BetRamp {
    let mut r = template.clone();
    for (i, x) in rows.iter().enumerate() {
        let (b, h) = best_bet(x, os, lambda);
        r.units[i] = b;
        r.hands[i] = h;
    }
    r
}

fn score_of(rows: &[BinStat], r: &BetRamp) -> f64 {
    let (mut ev, mut m2) = (0.0, 0.0);
    for (i, x) in rows.iter().enumerate() {
        let u = r.units[i];
        let h = r.hands[i].max(1);
        if u <= 0.0 {
            continue;
        }
        let hf = h as f64;
        ev += x.freq * hf * u * x.mean;
        m2 += x.freq * u * u * (hf * x.m2 + (h * (h - 1)) as f64 * x.cov);
    }
    let v = m2 - ev * ev;
    if v <= 0.0 || ev <= 0.0 {
        return ev;
    }
    ev / v.sqrt()
}

/// Kelly mode maximises growth (certainty equivalent) for the bankroll; otherwise searches the bet scale that
/// maximises SCORE (EV/SD) inside the min/max bet constraints.
pub fn optimize(d: &SimData, template: &BetRamp, os: &OptimizerSettings) -> BetRamp {
    let rows = rows(&bins(d), template);
    if os.kelly {
        return apply(&rows, template, os, dmax(1e-6, os.bankroll_units * os.kelly_fraction));
    }
    let mut best: Option<BetRamp> = None;
    let mut best_score = f64::NEG_INFINITY;
    let mut best_lam = 1.0;
    for k in 0..=120 {
        let lam = 10f64.powf(0.5 + k as f64 * 0.05);
        let r = apply(&rows, template, os, lam);
        let sc = score_of(&rows, &r);
        if sc > best_score {
            best_score = sc;
            best = Some(r);
            best_lam = lam;
        }
    }
    for k in -20..=20 {
        let lam = best_lam * 10f64.powf(k as f64 * 0.0025);
        let r = apply(&rows, template, os, lam);
        let sc = score_of(&rows, &r);
        if sc > best_score {
            best_score = sc;
            best = Some(r);
        }
    }
    best.unwrap_or_else(|| template.clone())
}
