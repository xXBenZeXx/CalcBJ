//! Combinatorial analysis for one shoe composition (C#: Combinatorial.cs).

use std::cell::RefCell;
use std::collections::HashMap;

use crate::dealer::{self, DealerDag};
use crate::num::{dmax, dmin};
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
    /// Player expectation per initial unit for a round dealt from this composition (negative = house edge). NaN for an
    /// analysis at a count ([`CountModel`]): its deals are weighted by the shoe, not by the count.
    pub player_ev: f64,
    /// Insurance EV per unit of original bet when the dealer shows an ace.
    pub insurance_ev: f64,
    /// Average EV of each option per cell: `[cell * 5 + k]`, k = stand, hit, double, surrender, split. NaN where not
    /// applicable.
    pub cell_ev: Vec<f64>,
}

/// The unseen cards at a known count, for index plays. The player decides after seeing the upcard and their own two
/// cards, so the count already includes them: for each deal, the cards left after that upcard and hand are shifted by
/// the linear regression of the rank counts on the tag sum to the composition expected when the unseen cards' tags add
/// up to `unseen_tags` (C#: CountModel).
pub struct CountModel {
    /// `[1..=10]` average tag of each rank.
    pub tags: [f64; 11],
    /// `[1..=10]` variance of the tags within each rank, where red and black cards count differently; `None` if none do.
    pub tag_var: Option<[f64; 11]>,
    /// Sum of the tags of the unseen cards.
    pub unseen_tags: f64,
    /// Number of unseen cards.
    pub unseen: f64,
}

/// The model's unseen cards out of the population left after a deal: q_r = n p_r (1 + (t_r - m) x) with
/// x = (s/n - m) / V, where p, m and V are the population's rank shares, mean tag and tag variance per card. The
/// variance includes the spread between a rank's red and black cards: the tag sum also depends on their colours, which
/// the rank counts don't show. Returns the cards' sum.
fn regress(popl: &[f64; 11], model: &CountModel, outc: &mut [f64; 11]) -> f64 {
    let (mut m, mut st) = (0.0, 0.0);
    for r in 1..=10 {
        m += popl[r];
        st += popl[r] * model.tags[r];
    }
    let n = dmin(model.unseen, m);
    let mean = st / m;
    let (mut v, mut max_dev) = (0.0, 0.0);
    for r in 1..=10 {
        if popl[r] <= 0.0 {
            continue;
        }
        let d = model.tags[r] - mean;
        v += match &model.tag_var {
            Some(tv) => popl[r] / m * (d * d + tv[r]),
            None => popl[r] / m * d * d,
        };
        max_dev = dmax(max_dev, d.abs());
    }
    let mut x = if v > 1e-12 { (model.unseen_tags / n - mean) / v } else { 0.0 };
    if max_dev > 1e-12 {
        let lim = 0.9 / max_dev;
        if x > lim {
            x = lim;
        } else if x < -lim {
            x = -lim;
        }
    }
    let mut total = 0.0;
    for r in 1..=10 {
        let mut f = 1.0 + (model.tags[r] - mean) * x;
        if f < 0.02 {
            f = 0.02;
        }
        let mut q = n * popl[r] / m * f;
        if q > popl[r] {
            q = popl[r];
        }
        outc[r] = q;
        total += q;
    }
    total
}

/// One game (number of decks and soft-17 rule) needs about 26,000 dealer distributions; the limit holds the six games
/// of a Game page refresh.
const DEALER_CACHE_LIMIT: usize = 1 << 18;

thread_local! {
    /// Raw dealer distributions keyed by game, upcard and the player's cards (C#: dealerCache). Shared by the engines
    /// of a thread: the Game page asks for many rule variants in a row, and most of them deal the same cards.
    static DEALER_CACHE: RefCell<HashMap<u64, [f64; 7]>> = RefCell::new(HashMap::new());
}

/// Adds a card of rank `r` to a hand key (5 bits per rank).
fn with_card(cards: u64, r: usize) -> u64 {
    cards + (1u64 << (5 * (r - 1)))
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
    pop: [f64; 11],
    tmp: [f64; 11],
    acc: Vec<f64>,
    acc_w: Vec<f64>,
    combos: Vec<Combo>,
    // Exact expectation off the top (see `exact_off_top`).
    xc: [f64; 11],
    hand_memo: HashMap<u64, f64>,
    split_memo: HashMap<u64, f64>,
    x_up: usize,
    x_game: u64,
    x_takes_all: bool,
    // The split being worked out: pair rank, resplits so far (pair cards out besides the two dealt), most hands, and
    // per number of resplits the chance of drawing the pair rank and the values of a hand without and with it.
    s_rank: usize,
    s_level: usize,
    s_max_hands: i32,
    split_p: [f64; 3],
    split_x: [f64; 3],
    split_y: [f64; 3],
}

/// Full shoe composition: `[0]` unused, `[1..=9]` = 4 per deck, `[10]` = 16 per deck.
pub fn full_shoe(decks: f64) -> [f64; 11] {
    let mut c = [0.0; 11];
    c[1..=9].fill(4.0 * decks);
    c[10] = 16.0 * decks;
    c
}

/// Off-the-top player expectation for the rules with optimal total-dependent basic strategy, played as the simulator
/// plays it (see [`CaEngine::exact_off_top`]).
pub fn house_edge_off_top(rules: &Rules) -> f64 {
    let mut e = CaEngine::new(rules);
    let res = e.evaluate(&full_shoe(rules.decks as f64));
    e.exact_off_top(&res)
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
            pop: [0.0; 11],
            tmp: [0.0; 11],
            acc: vec![0.0; CELLS_PER_BIN * 4],
            acc_w: vec![0.0; CELLS_PER_BIN],
            combos: Vec::with_capacity(600),
            xc: [0.0; 11],
            hand_memo: HashMap::new(),
            split_memo: HashMap::new(),
            x_up: 1,
            x_game: 0,
            x_takes_all: false,
            s_rank: 1,
            s_level: 0,
            s_max_hands: 2,
            split_p: [0.0; 3],
            split_x: [0.0; 3],
            split_y: [0.0; 3],
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
        self.evaluate_with(shoe, None)
    }

    /// Analysis of a round dealt from `shoe`. With a count model, `shoe` is the population before the deal and the
    /// cards each deal leaves unseen come from the model (the index plays at that count); deals are weighted by the
    /// shoe.
    pub fn evaluate_with(&mut self, shoe: &[f64; 11], model: Option<&CountModel>) -> CaResult {
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
            self.pop[1..=10].copy_from_slice(&shoe[1..=10]);
            self.pop[u] = dmax(0.0, self.pop[u] - 1.0);
            let npop = n - 1.0;
            let nu = match model {
                None => {
                    self.su = self.pop;
                    npop
                }
                Some(m) => regress(&self.pop, m, &mut self.su),
            };

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
                let wa = self.pop[a] / npop;
                if wa <= 0.0 {
                    continue;
                }
                for b in a..=10usize {
                    let sb = self.pop[b] - if a == b { 1.0 } else { 0.0 };
                    if sb <= 0.0 {
                        continue;
                    }
                    let w = wa * sb / (npop - 1.0) * if a == b { 1.0 } else { 2.0 };
                    let np = match model {
                        None => {
                            self.sp = self.pop;
                            self.sp[a] = dmax(0.0, self.sp[a] - 1.0);
                            self.sp[b] = dmax(0.0, self.sp[b] - 1.0);
                            npop - 2.0
                        }
                        Some(m) => {
                            self.tmp = self.pop;
                            self.tmp[a] = dmax(0.0, self.tmp[a] - 1.0);
                            self.tmp[b] = dmax(0.0, self.tmp[b] - 1.0);
                            regress(&self.tmp, m, &mut self.sp)
                        }
                    };
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
            cell_ev[pcell * 5] = c.s;
            cell_ev[pcell * 5 + 1] = c.hh;
            if c.can_d {
                cell_ev[pcell * 5 + 2] = c.d;
            }
            if surr_allowed {
                cell_ev[pcell * 5 + 3] = c.rr;
            }
            cell_ev[pcell * 5 + 4] = if can_split { c.p } else { f64::NAN };
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

        let player_ev = if model.is_some() { f64::NAN } else { ev_total };
        CaResult { flags, insure, player_ev, insurance_ev, cell_ev }
    }

    // ---------------- Exact expectation of a strategy off the top ----------------

    fn raw_dealer(&mut self, cards: u64, n: f64) -> [f64; 7] {
        let key = ((cards * 16 + self.x_up as u64) << 5) + self.x_game;
        if let Some(d) = DEALER_CACHE.with_borrow(|c| c.get(&key).copied()) {
            return d;
        }
        self.dags[self.x_up - 1].evaluate(&self.xc, n, &mut self.mass, &mut self.dist);
        let d = self.dist;
        DEALER_CACHE.with_borrow_mut(|c| {
            if c.len() >= DEALER_CACHE_LIMIT {
                c.clear();
            }
            c.insert(key, d);
        });
        d
    }

    /// The player stands on `total` with `bet` units; peek games leave out the dealer's blackjacks (see
    /// `exact_off_top`).
    fn terminal(&mut self, cards: u64, n: f64, total: usize, bet: f64) -> f64 {
        let d = self.raw_dealer(cards, n);
        let mut ev = d[5];
        for (k, &dk) in d[..5].iter().enumerate() {
            let t = 17 + k;
            if total > t {
                ev += dk;
            } else if total < t {
                ev -= dk;
            }
        }
        ev *= bet;
        if self.x_takes_all {
            ev -= bet * d[6];
        }
        ev
    }

    fn bust(&self, n: f64, bet: f64) -> f64 {
        if self.x_takes_all {
            return -bet;
        }
        let pb = if self.x_up == 1 {
            self.xc[10] / n
        } else if self.x_up == 10 {
            self.xc[1] / n
        } else {
            0.0
        };
        -bet * (1.0 - pb)
    }

    /// Value of a hand of three or more cards, played by the flags as the simulator plays it. In a split hand, `cards`
    /// also holds the cards of the split that are out of the shoe but not in this hand (see `split_exact`).
    fn play(&mut self, flags: &[u8], cards: u64, n: f64, h: usize, ace: bool, split: bool) -> f64 {
        let key = if split {
            ((cards * 16 + self.x_up as u64) * 16 + self.s_rank as u64) * 4 + self.s_level as u64
        } else {
            cards * 16 + self.x_up as u64
        };
        let memo = if split { &self.split_memo } else { &self.hand_memo };
        if let Some(&v) = memo.get(&key) {
            return v;
        }
        let soft = ace && h <= 11;
        let best = if soft { h + 10 } else { h };
        let v = if best == 21 {
            self.terminal(cards, n, 21, 1.0)
        } else {
            let cell = if soft { soft_cell(best, self.x_up) } else { hard_cell(h, self.x_up) };
            if (flags[cell] & flag::STAND) != 0 {
                self.terminal(cards, n, best, 1.0)
            } else {
                self.draw(flags, cards, n, h, ace, 1.0, split)
            }
        };
        if split {
            self.split_memo.insert(key, v);
        } else {
            self.hand_memo.insert(key, v);
        }
        v
    }

    /// Takes one card: a hit (bet 1, then plays on) or a double (bet 2, then stands).
    #[allow(clippy::too_many_arguments)]
    fn draw(&mut self, flags: &[u8], cards: u64, n: f64, h: usize, ace: bool, bet: f64, split: bool) -> f64 {
        let mut ev = 0.0;
        for r in 1..=10usize {
            let cr = self.xc[r];
            if cr <= 0.0 {
                continue;
            }
            let pr = cr / n;
            self.xc[r] = cr - 1.0;
            let h2 = h + r;
            let a2 = ace || r == 1;
            let v = if h2 > 21 {
                self.bust(n - 1.0, bet)
            } else if bet == 2.0 {
                self.terminal(with_card(cards, r), n - 1.0, if a2 && h2 <= 11 { h2 + 10 } else { h2 }, 2.0)
            } else {
                self.play(flags, with_card(cards, r), n - 1.0, h2, a2, split)
            };
            ev += pr * v;
            self.xc[r] = cr;
        }
        ev
    }

    /// A split hand of the pair card `a` and its second card `r` (already out of `xc` and in `cards`), played as the
    /// simulator plays it: split aces get one card unless they may be hit, and doubling needs DAS and isn't allowed on
    /// split aces.
    fn split_hand(&mut self, flags: &[u8], cards: u64, n: f64, a: usize, r: usize) -> f64 {
        let h = a + r;
        let ace = a == 1 || r == 1;
        let soft = ace && h <= 11;
        let best = if soft { h + 10 } else { h };
        if a == 1 && !self.r.hit_split_aces {
            return self.terminal(cards, n, best, 1.0);
        }
        if best == 21 {
            return self.terminal(cards, n, 21, 1.0);
        }
        let f = flags[if soft { soft_cell(best, self.x_up) } else { hard_cell(h, self.x_up) }];
        if (f & flag::DOUBLE) != 0 && self.r.das && a != 1 && self.r.can_double_total(h as i32, soft) {
            return self.draw(flags, cards, n, h, ace, 2.0, true);
        }
        if (f & flag::STAND) != 0 {
            return self.terminal(cards, n, best, 1.0);
        }
        self.draw(flags, cards, n, h, ace, 1.0, true)
    }

    /// Total value of the split hands still to come: `pending` hands waiting for their second card, of `hands` in all.
    fn split_dp(&self, pending: i32, hands: i32) -> f64 {
        if pending == 0 {
            return 0.0;
        }
        let j = (hands - 2) as usize;
        let pp = self.split_p[j];
        let mut v = (1.0 - pp) * (self.split_x[j] + self.split_dp(pending - 1, hands));
        if hands < self.s_max_hands {
            v += pp * self.split_dp(pending + 1, hands + 1);
        } else {
            v += pp * (self.split_y[j] + self.split_dp(pending - 1, hands));
        }
        v
    }

    /// Expectation of splitting a pair of `a`, played as the simulator plays it (`xc` holds the cards left after the
    /// deal). Every split hand is played exactly with its own cards, the pair cards and the pair cards of earlier
    /// resplits out of the shoe. The cards of the other split hands stay in: in shuffle-every-round simulations of 1 to
    /// 8 decks that makes no measurable difference (0.002% or less), where the analysis's fixed draw odds were up to
    /// 0.02% too low with one deck.
    fn split_exact(&mut self, flags: &[u8], a: usize, np: f64, pbj: f64, peek_cond: bool) -> f64 {
        let mut max_hands = self.r.max_split_hands.min(4);
        if a == 1 && !self.r.resplit_aces {
            max_hands = 2;
        }
        self.s_rank = a;
        self.s_max_hands = max_hands;
        let mut cards = with_card(with_card(0, a), a);
        let mut n = np;
        let mut taken = 0.0;
        for j in 0..=(max_hands - 2) as usize {
            if j > 0 {
                // Every resplit puts one more pair card on the table.
                if self.xc[a] < 1.0 {
                    // No pair card left to resplit with: this many resplits can't happen.
                    self.split_p[j] = 0.0;
                    self.split_x[j] = 0.0;
                    self.split_y[j] = 0.0;
                    continue;
                }
                self.xc[a] -= 1.0;
                cards = with_card(cards, a);
                n -= 1.0;
                taken += 1.0;
            }
            self.s_level = j;
            let na = self.xc[a];
            let pp = na / n;
            let mut sum = 0.0;
            for r in 1..=10usize {
                let cr = self.xc[r];
                if r == a || cr <= 0.0 {
                    continue;
                }
                self.xc[r] = cr - 1.0;
                sum += cr / n * self.split_hand(flags, with_card(cards, r), n - 1.0, a, r);
                self.xc[r] = cr;
            }
            self.split_p[j] = pp;
            self.split_x[j] = if pp < 1.0 { sum / (1.0 - pp) } else { 0.0 };
            self.split_y[j] = 0.0;
            if j == (max_hands - 2) as usize && na > 0.0 {
                // At the most hands a further pair card is played as it is.
                self.xc[a] = na - 1.0;
                self.split_y[j] = self.split_hand(flags, with_card(cards, a), n - 1.0, a, a);
                self.xc[a] = na;
            }
        }
        self.xc[a] += taken;
        let mut ev = self.split_dp(2, 2);
        if peek_cond {
            ev -= pbj;
        }
        ev
    }

    /// Off-the-top expectation of the strategy in `basic` (its flags and insurance) with every card the player draws
    /// taken out of the shoe: hits, doubles and split hands follow the flags exactly as the simulator plays them
    /// (splits as in `split_exact`). `evaluate`'s `player_ev` assumes the best play after each hit, with the draw odds
    /// fixed after the first three cards, which is up to 0.04% too high with one deck. In peek games each deal loses
    /// its blackjack probability once and the dealer's blackjacks are left out of every later outcome, which is exact
    /// because the order of the unseen cards doesn't matter.
    pub fn exact_off_top(&mut self, basic: &CaResult) -> f64 {
        let shoe = full_shoe(self.r.decks as f64);
        self.x_game = (self.r.decks * 2 + if self.r.h17 { 1 } else { 0 }) as u64;
        self.hand_memo.clear();
        self.split_memo.clear();
        self.x_takes_all = self.r.dealer_bj_takes_all();
        let surr_allowed = self.r.surrender != SurrenderRule::None;
        let sr_before_peek = self.r.surrender_before_peek();
        let peek_cond = !self.r.dealer_bj_takes_all();
        let flags = &basic.flags;
        let n = sum(&shoe);
        let mut ev_total = 0.0;
        for u in 1..=10usize {
            let pu = shoe[u] / n;
            if pu <= 0.0 {
                continue;
            }
            self.x_up = u;
            self.pop[1..=10].copy_from_slice(&shoe[1..=10]);
            self.pop[u] = dmax(0.0, self.pop[u] - 1.0);
            let npop = n - 1.0;
            for a in 1..=10usize {
                let wa = self.pop[a] / npop;
                if wa <= 0.0 {
                    continue;
                }
                for b in a..=10usize {
                    let sb = self.pop[b] - if a == b { 1.0 } else { 0.0 };
                    if sb <= 0.0 {
                        continue;
                    }
                    let w = wa * sb / (npop - 1.0) * if a == b { 1.0 } else { 2.0 };
                    self.xc = self.pop;
                    self.xc[a] = dmax(0.0, self.xc[a] - 1.0);
                    self.xc[b] = dmax(0.0, self.xc[b] - 1.0);
                    let np = npop - 2.0;
                    let pbj = if u == 1 {
                        self.xc[10] / np
                    } else if u == 10 {
                        self.xc[1] / np
                    } else {
                        0.0
                    };
                    if a == 1 && b == 10 {
                        ev_total += pu * w * (1.0 - pbj) * self.r.blackjack_pays;
                        continue;
                    }
                    let h0 = a + b;
                    let ace0 = a == 1 || b == 1;
                    let soft0 = ace0 && h0 + 10 <= 21;
                    let pf = if a == b { flags[pair_cell(a, u)] } else { 0 };
                    let tf = flags[if soft0 { soft_cell(h0 + 10, u) } else { hard_cell(h0, u) }];
                    let ev_r = if sr_before_peek { -0.5 } else { -pbj - 0.5 * (1.0 - pbj) };
                    let ev = if surr_allowed && (pf & flag::SURRENDER) != 0 {
                        ev_r
                    } else if (pf & flag::SPLIT) != 0 {
                        self.split_exact(flags, a, np, pbj, peek_cond)
                    } else if surr_allowed && (tf & flag::SURRENDER) != 0 {
                        ev_r
                    } else {
                        let cards = with_card(with_card(0, a), b);
                        let mut v = if (tf & flag::DOUBLE) != 0 && self.r.can_double_total(h0 as i32, soft0) {
                            self.draw(flags, cards, np, h0, ace0, 2.0, false)
                        } else if (tf & flag::STAND) != 0 {
                            self.terminal(cards, np, if soft0 { h0 + 10 } else { h0 }, 1.0)
                        } else {
                            self.draw(flags, cards, np, h0, ace0, 1.0, false)
                        };
                        if peek_cond {
                            v -= pbj;
                        }
                        v
                    };
                    ev_total += pu * w * ev;
                }
            }
        }
        if basic.insure {
            ev_total += (shoe[1] / n) * basic.insurance_ev;
        }
        ev_total
    }
}
