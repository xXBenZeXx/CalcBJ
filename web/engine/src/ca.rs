//! Combinatorial analysis for one shoe composition (C#: Combinatorial.cs).

use crate::dealer::{self, DealerDag};
use crate::num::dmax;
use crate::strategy::{CELLS_PER_BIN, hard_cell, pair_cell, soft_cell};
use crate::types::{Rules, SurrenderRule};

/// Decision flags of a cell.
pub mod flag {
    pub const STAND: u8 = 1;
    pub const DOUBLE: u8 = 2;
    pub const SURRENDER: u8 = 4;
    pub const SPLIT: u8 = 8;
}

#[derive(Clone, Debug)]
pub struct CaResult {
    /// Decision flags per cell (see [`crate::strategy`] for the cell layout).
    pub flags: Vec<u8>,
    pub insure: bool,
    /// Player expectation per initial unit for a round dealt from this composition (negative = house edge).
    pub player_ev: f64,
    /// Insurance EV per unit of original bet when the dealer shows an ace.
    pub insurance_ev: f64,
    /// Average EV of each option per cell: `[cell * 5 + k]`, k = stand, hit, double, surrender, split. NaN where not
    /// applicable.
    pub cell_ev: Vec<f64>,
}

#[derive(Clone, Copy)]
struct Combo {
    u: usize,
    a: usize,
    b: usize,
    w: f64,
    s: f64,
    hh: f64,
    d: f64,
    rr: f64,
    p: f64,
    can_d: bool,
}

/// Combinatorial analysis for one shoe composition. The dealer distribution is exact (with removal) for every
/// player two-card hand and upcard; the player's own draws use the composition left after those three cards.
/// Decisions are total-dependent: each cell takes the action with the best probability-weighted EV.
/// Reuse one engine for many compositions; it keeps its work buffers.
pub struct CaEngine {
    r: Rules,
    dags: [&'static DealerDag; 10],
    mass: Vec<f64>,
    dist: [f64; 7],
    sev: [f64; 22],
    v: [f64; 44],
    h: [f64; 44],
    p: [f64; 11],
    su: [f64; 11],
    sp: [f64; 11],
    acc: Vec<f64>,
    acc_w: Vec<f64>,
    combos: Vec<Combo>,
}

/// Full shoe composition: `[0]` unused, `[1..=9]` = 4 per deck, `[10]` = 16 per deck.
pub fn full_shoe(decks: f64) -> [f64; 11] {
    let mut c = [0.0; 11];
    c[1..=9].fill(4.0 * decks);
    c[10] = 16.0 * decks;
    c
}

/// Off-the-top player expectation for the rules with optimal total-dependent basic strategy.
pub fn house_edge_off_top(rules: &Rules) -> f64 {
    CaEngine::new(rules).evaluate(&full_shoe(rules.decks as f64)).player_ev
}

fn sum(c: &[f64; 11]) -> f64 {
    let mut n = 0.0;
    for &x in &c[1..=10] {
        n += x;
    }
    n
}

impl CaEngine {
    pub fn new(rules: &Rules) -> CaEngine {
        let dags: [&'static DealerDag; 10] = std::array::from_fn(|i| dealer::get(i + 1, rules.h17));
        let max_states = dags.iter().map(|d| d.count).max().unwrap_or(0);
        CaEngine {
            r: rules.clone(),
            dags,
            mass: vec![0.0; max_states],
            dist: [0.0; 7],
            sev: [0.0; 22],
            v: [0.0; 44],
            h: [0.0; 44],
            p: [0.0; 11],
            su: [0.0; 11],
            sp: [0.0; 11],
            acc: vec![0.0; CELLS_PER_BIN * 4],
            acc_w: vec![0.0; CELLS_PER_BIN],
            combos: Vec::with_capacity(600),
        }
    }

    fn fill_stand(&mut self, dbj: f64) {
        // dist[0..=4] = 17..21, dist[5] = bust
        let d = &self.dist;
        let below = d[5] - dbj - (d[0] + d[1] + d[2] + d[3] + d[4]);
        for t in 0..=16 {
            self.sev[t] = below;
        }
        for t in 17..=21usize {
            let mut ev = d[5] - dbj;
            for (k, &pk) in d[..5].iter().enumerate() {
                let dt = 17 + k;
                if t > dt {
                    ev += pk;
                } else if t < dt {
                    ev -= pk;
                }
            }
            self.sev[t] = ev;
        }
    }

    fn fill_dp(&mut self) {
        for h in (2..=21usize).rev() {
            for a in 0..=1usize {
                let best = if a == 1 && h + 10 <= 21 { h + 10 } else { h };
                let st = self.sev[best];
                let mut hit = 0.0;
                for r in 1..=10usize {
                    let pr = self.p[r];
                    if pr == 0.0 {
                        continue;
                    }
                    let nh = h + r;
                    if nh > 21 {
                        hit -= pr;
                    } else {
                        hit += pr * self.v[nh * 2 + if a == 1 || r == 1 { 1 } else { 0 }];
                    }
                }
                self.h[h * 2 + a] = hit;
                self.v[h * 2 + a] = if st > hit { st } else { hit };
            }
        }
    }

    fn dbl(&self, h: usize, a: usize) -> f64 {
        let mut ev = 0.0;
        for r in 1..=10usize {
            let pr = self.p[r];
            if pr == 0.0 {
                continue;
            }
            let nh = h + r;
            if nh > 21 {
                ev -= 2.0 * pr;
            } else {
                let na = if a == 1 || r == 1 { 1 } else { 0 };
                let best = if na == 1 && nh + 10 <= 21 { nh + 10 } else { nh };
                ev += 2.0 * pr * self.sev[best];
            }
        }
        ev
    }

    fn split_hand_value(&self, a: usize, r: usize) -> f64 {
        let h = a + r;
        let ace = if a == 1 || r == 1 { 1 } else { 0 };
        let soft = ace == 1 && h + 10 <= 21;
        let best = if soft { h + 10 } else { h };
        if a == 1 && !self.r.hit_split_aces {
            return self.sev[best];
        }
        let mut v = dmax(self.sev[best], self.h[h * 2 + ace]);
        if self.r.das && a != 1 && self.r.can_double_total(h as i32, soft) {
            v = dmax(v, self.dbl(h, ace));
        }
        v
    }

    fn resplit_dp(pending: i32, hands: i32, max_hands: i32, pp: f64, x: f64, y: f64) -> f64 {
        if pending == 0 {
            return 0.0;
        }
        let mut v = (1.0 - pp) * (x + Self::resplit_dp(pending - 1, hands, max_hands, pp, x, y));
        if hands < max_hands {
            v += pp * Self::resplit_dp(pending + 1, hands + 1, max_hands, pp, x, y);
        } else {
            v += pp * (y + Self::resplit_dp(pending - 1, hands, max_hands, pp, x, y));
        }
        v
    }

    fn split_ev(&self, a: usize) -> f64 {
        let mut max_hands = self.r.max_split_hands.min(4);
        if a == 1 && !self.r.resplit_aces {
            max_hands = 2;
        }
        let pa = self.p[a];
        let mut sum_x = 0.0;
        for r in 1..=10usize {
            if r == a || self.p[r] == 0.0 {
                continue;
            }
            sum_x += self.p[r] * self.split_hand_value(a, r);
        }
        let x = if pa < 1.0 { sum_x / (1.0 - pa) } else { 0.0 };
        let y = self.split_hand_value(a, a);
        Self::resplit_dp(2, 2, max_hands, pa, x, y)
    }

    /// Dealer distribution for the cards in `comp`, conditioned on no dealer blackjack when the dealer peeks;
    /// returns the blackjack probability that still counts against the player (no-hole-card games).
    fn dealer(&mut self, u: usize, use_sp: bool, n: f64, peek_cond: bool) -> f64 {
        let comp = if use_sp { &self.sp } else { &self.su };
        self.dags[u - 1].evaluate(comp, n, &mut self.mass, &mut self.dist);
        if peek_cond {
            let nb = 1.0 - self.dist[6];
            if nb > 1e-12 {
                for k in 0..6 {
                    self.dist[k] /= nb;
                }
            }
            0.0
        } else {
            self.dist[6]
        }
    }

    pub fn evaluate(&mut self, shoe: &[f64; 11]) -> CaResult {
        const CPB: usize = CELLS_PER_BIN;
        let mut flags = vec![0u8; CPB];
        let mut cell_ev = vec![f64::NAN; CPB * 5];
        self.acc.fill(0.0);
        self.acc_w.fill(0.0);
        self.combos.clear();

        let surr_allowed = self.r.surrender != SurrenderRule::None;
        let sr_before_peek = self.r.surrender_before_peek();
        let peek_cond = !self.r.dealer_bj_takes_all();
        let can_split = self.r.max_split_hands >= 2;
        let n = sum(shoe);
        let mut ev_total = 0.0;
        let mut insure = false;
        let mut insurance_ev = 0.0;

        for u in 1..=10usize {
            let pu = shoe[u] / n;
            self.su[1..=10].copy_from_slice(&shoe[1..=10]);
            self.su[u] = dmax(0.0, self.su[u] - 1.0);
            let nu = n - 1.0;

            // Hit/stand defaults for totals no two-card hand reaches (e.g. multi-card 21).
            let dbj0 = self.dealer(u, false, nu, peek_cond);
            self.fill_stand(dbj0);
            for r in 1..=10 {
                self.p[r] = self.su[r] / nu;
            }
            self.fill_dp();
            for t in 4..=21usize {
                flags[hard_cell(t, u)] = if self.sev[t] >= self.h[t * 2] { flag::STAND } else { 0 };
                if t >= 12 {
                    flags[soft_cell(t, u)] = if self.sev[t] >= self.h[(t - 10) * 2 + 1] { flag::STAND } else { 0 };
                }
            }

            if u == 1 {
                let pt = self.su[10] / nu;
                insurance_ev = 1.5 * pt - 0.5;
                insure = self.r.insurance && pt > 1.0 / 3.0;
            }

            if pu <= 0.0 {
                continue;
            }

            for a in 1..=10usize {
                let wa = self.su[a] / nu;
                if wa <= 0.0 {
                    continue;
                }
                for b in a..=10usize {
                    let sb = self.su[b] - if a == b { 1.0 } else { 0.0 };
                    if sb <= 0.0 {
                        continue;
                    }
                    let w = wa * sb / (nu - 1.0) * if a == b { 1.0 } else { 2.0 };
                    self.sp = self.su;
                    self.sp[a] = dmax(0.0, self.sp[a] - 1.0);
                    self.sp[b] = dmax(0.0, self.sp[b] - 1.0);
                    let np = nu - 2.0;
                    let pbj = if u == 1 {
                        self.sp[10] / np
                    } else if u == 10 {
                        self.sp[1] / np
                    } else {
                        0.0
                    };

                    if a == 1 && b == 10 {
                        ev_total += pu * w * (1.0 - pbj) * self.r.blackjack_pays;
                        continue;
                    }

                    let dbj = self.dealer(u, true, np, peek_cond);
                    self.fill_stand(dbj);
                    for r in 1..=10 {
                        self.p[r] = self.sp[r] / np;
                    }
                    self.fill_dp();

                    let h0 = a + b;
                    let ace0 = if a == 1 || b == 1 { 1 } else { 0 };
                    let soft0 = ace0 == 1 && h0 + 10 <= 21;
                    let best0 = if soft0 { h0 + 10 } else { h0 };
                    let mut ev_s = self.sev[best0];
                    let mut ev_h = self.h[h0 * 2 + ace0];
                    let can_d = self.r.can_double_total(h0 as i32, soft0);
                    let mut ev_d = if can_d { self.dbl(h0, ace0) } else { 0.0 };
                    let mut ev_p = if a == b && can_split { self.split_ev(a) } else { 0.0 };
                    if peek_cond && pbj > 0.0 {
                        let q = 1.0 - pbj;
                        ev_s = -pbj + q * ev_s;
                        ev_h = -pbj + q * ev_h;
                        ev_d = -pbj + q * ev_d;
                        ev_p = -pbj + q * ev_p;
                    }
                    let ev_r = if sr_before_peek { -0.5 } else { -pbj - 0.5 * (1.0 - pbj) };

                    let cell = if soft0 { soft_cell(best0, u) } else { hard_cell(h0, u) };
                    self.acc[cell * 4] += w * ev_s;
                    self.acc[cell * 4 + 1] += w * ev_h;
                    if can_d {
                        self.acc[cell * 4 + 2] += w * ev_d;
                    }
                    if surr_allowed {
                        self.acc[cell * 4 + 3] += w * ev_r;
                    }
                    self.acc_w[cell] += w;

                    self.combos.push(Combo {
                        u,
                        a,
                        b,
                        w: w * pu,
                        s: ev_s,
                        hh: ev_h,
                        d: ev_d,
                        rr: ev_r,
                        p: ev_p,
                        can_d,
                    });
                }
            }
        }

        // Pass 1: hard and soft cells.
        for cell in 0..440 {
            let w = self.acc_w[cell];
            if w <= 0.0 {
                continue;
            }
            let t = ((cell % 220) / 10) as i32;
            let soft = cell >= 220;
            let can_d = self.r.can_double_total(if soft { t - 10 } else { t }, soft);
            let s = self.acc[cell * 4] / w;
            let hv = self.acc[cell * 4 + 1] / w;
            let d = if can_d { self.acc[cell * 4 + 2] / w } else { f64::NEG_INFINITY };
            let rv = if surr_allowed { self.acc[cell * 4 + 3] / w } else { f64::NEG_INFINITY };
            let mut f = 0u8;
            if s >= hv {
                f |= flag::STAND;
            }
            let best_hs = dmax(s, hv);
            if d > best_hs {
                f |= flag::DOUBLE;
            }
            if rv > dmax(best_hs, d) {
                f |= flag::SURRENDER;
            }
            flags[cell] = f;
            cell_ev[cell * 5] = s;
            cell_ev[cell * 5 + 1] = hv;
            if can_d {
                cell_ev[cell * 5 + 2] = d;
            }
            if surr_allowed {
                cell_ev[cell * 5 + 3] = rv;
            }
        }

        // Pass 2: pairs (compare splitting with how the total would otherwise be played).
        for c in &self.combos {
            if c.a != c.b {
                continue;
            }
            let h0 = c.a + c.b;
            let soft0 = c.a == 1;
            let tcell = if soft0 { soft_cell(12, c.u) } else { hard_cell(h0, c.u) };
            let tf = flags[tcell];
            let non_split = if (tf & flag::DOUBLE) != 0 && c.can_d {
                c.d
            } else if (tf & flag::STAND) != 0 {
                c.s
            } else {
                c.hh
            };
            let pcell = pair_cell(c.a, c.u);
            let mut f = 0u8;
            if can_split && c.p > non_split {
                f |= flag::SPLIT;
            }
            let best_play = dmax(non_split, if can_split { c.p } else { f64::NEG_INFINITY });
            if surr_allowed && c.rr > best_play {
                f |= flag::SURRENDER;
            }
            flags[pcell] = f;
            cell_ev[pcell * 5 + 4] = if can_split { c.p } else { f64::NAN };
            cell_ev[pcell * 5] = non_split;
            if surr_allowed {
                cell_ev[pcell * 5 + 3] = c.rr;
            }
        }

        // Pass 3: expectation of the resulting strategy.
        for c in &self.combos {
            let h0 = c.a + c.b;
            let soft0 = (c.a == 1 || c.b == 1) && h0 + 10 <= 21;
            let pf = if c.a == c.b { flags[pair_cell(c.a, c.u)] } else { 0 };
            let tf = flags[if soft0 { soft_cell(h0 + 10, c.u) } else { hard_cell(h0, c.u) }];
            let ev = if surr_allowed && (pf & flag::SURRENDER) != 0 {
                c.rr
            } else if (pf & flag::SPLIT) != 0 {
                c.p
            } else if surr_allowed && (tf & flag::SURRENDER) != 0 {
                c.rr
            } else if (tf & flag::DOUBLE) != 0 && c.can_d {
                c.d
            } else if (tf & flag::STAND) != 0 {
                c.s
            } else {
                c.hh
            };
            ev_total += c.w * ev;
        }
        if insure {
            ev_total += (shoe[1] / n) * insurance_ev;
        }

        CaResult { flags, insure, player_ev: ev_total, insurance_ev, cell_ev }
    }
}
