//! Combinatorial analysis for one shoe composition (C#: Combinatorial.cs).

use std::cell::RefCell;
use std::collections::{BTreeMap, HashMap};

use crate::dealer::{self, DealerDag};
use crate::num::{dmax, dmin};
use crate::strategy::{CELLS_PER_BIN, hard_cell, pair_cell, soft_cell};
use crate::types::{HoleCardRule, Rules, SurrenderRule};

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
    /// applicable. Counted from the deal: with an ace or ten up it includes the rounds lost to a dealer blackjack.
    pub cell_ev: Vec<f64>,
    /// Per cell, the chance that the dealer has blackjack (0 unless an ace or a ten is up).
    pub cell_bj: Vec<f64>,
}

impl CaResult {
    /// True when the player decides knowing the dealer has no blackjack: the dealer peeks, and surrender isn't early.
    pub fn after_peek(r: &Rules) -> bool {
        r.hole_card == HoleCardRule::UsPeek && r.surrender != SurrenderRule::Early
    }

    /// A cell's option value as the player meets it: in a game where the decision comes after the dealer's peek
    /// (`after_peek`), the value given that the dealer has no blackjack, so late surrender reads -0.5; otherwise from
    /// the deal.
    pub fn shown_ev(&self, r: &Rules, cell: usize, k: usize) -> f64 {
        let v = self.cell_ev[cell * 5 + k];
        if v.is_nan() || !Self::after_peek(r) {
            return v;
        }
        let p = self.cell_bj[cell];
        if p > 0.0 && p < 1.0 { (v + p) / (1.0 - p) } else { v }
    }
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
    /// How likely this count is with each tag sum of the three cards on the table (the upcard and the player's two),
    /// which the count includes: `[(sum - table_min) / table_step]`, sums in the simulator's scaled tag units, largest
    /// entry 1. With one or two decks the hands of a total come up in very different shares at a count than off the top
    /// (T,2 is nine in ten of the 12s at Hi-Lo +1 with one deck). `None`: as off the top.
    pub table_weight: Option<Vec<f64>>,
    pub table_min: i32,
    pub table_step: i32,
    /// `[card code 0..20]` tags in the simulator's scaled units (the two colours of each rank).
    pub code_tags: [i32; 20],
}

/// How likely the model's count is with this deal on the table, relative to the likeliest deal (the two colours of each
/// rank averaged; never quite 0, so that no cell loses all its hands).
fn table_weight(m: &CountModel, tw: &[f64], u: usize, a: usize, b: usize) -> f64 {
    let ct = &m.code_tags;
    let mut sum = 0.0;
    for i in 0..2 {
        for j in 0..2 {
            for k in 0..2 {
                let idx =
                    (ct[(u - 1) * 2 + i] + ct[(a - 1) * 2 + j] + ct[(b - 1) * 2 + k] - m.table_min) / m.table_step;
                if idx >= 0 && (idx as usize) < tw.len() {
                    sum += tw[idx as usize];
                }
            }
        }
    }
    dmax(sum / 8.0, 1e-9)
}

/// What exact card removal changes in the analysis, worked out off the top by [`CaEngine::basic_strategy`] and added at
/// every count (C#: ExactAdjust): per deal (upcard u, first cards a <= b) the change in the hit, double and split
/// values, and per hard or soft cell how much more its hands of three or more cards prefer standing than its two-card
/// hands (NaN where the strategy reaches no such hand).
#[derive(Clone, Debug)]
pub struct ExactAdjust {
    pub hit: Vec<f64>,
    pub dbl: Vec<f64>,
    pub split: Vec<f64>,
    pub stand_shift: Vec<f64>,
    /// Per hard or soft cell: how often a hand of three or more cards plays it for each time a two-card hand does (0
    /// where none does, or where it wasn't worked out).
    pub stand_ratio: Vec<f64>,
    /// Cards left after a deal off the top, where the corrections were measured.
    pub cards: f64,
}

impl Default for ExactAdjust {
    fn default() -> Self {
        Self::new()
    }
}

impl ExactAdjust {
    pub fn new() -> ExactAdjust {
        ExactAdjust {
            hit: vec![0.0; 1331],
            dbl: vec![0.0; 1331],
            split: vec![0.0; 1331],
            stand_shift: vec![0.0; 440],
            stand_ratio: vec![0.0; 440],
            cards: 0.0,
        }
    }

    pub fn deal(u: usize, a: usize, b: usize) -> usize {
        (u * 11 + a) * 11 + b
    }
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

/// How many dealer distributions the memo holds before it is cleared. One game (number of decks and soft-17 rule)
/// needs about 26,000, so this holds the six games of a Game page refresh several times over. It is 229,376, 7/8 of
/// 2^18, because that is what a hash table of 2^18 buckets (16 MiB) holds: one entry more and it moves to 2^19 buckets
/// (32 MiB) while the old table still exists, a 67 MiB peak in the WebAssembly memory, which never shrinks again (too
/// much on a phone). The C# engine keeps 2^18 entries; the limit only decides when the memo is cleared, never a result.
const DEALER_CACHE_LIMIT: usize = (1 << 18) / 8 * 7;

thread_local! {
    /// Raw dealer distributions keyed by game, upcard and the player's cards (C#: dealerCache). Shared by the engines
    /// of a thread: the Game page asks for many rule variants in a row, and most of them deal the same cards.
    static DEALER_CACHE: RefCell<HashMap<u64, [f64; 7]>> = RefCell::new(HashMap::new());
    /// Basic strategies of recently used rules (C#: basicCache): the Game page asks for the same rule variants on every
    /// refresh, and settings such as the penetration don't change them. Oldest first, at most `BASIC_CACHE_LIMIT`.
    static BASIC_CACHE: RefCell<Vec<(String, CaResult, ExactAdjust, f64)>> = const { RefCell::new(Vec::new()) };
}

const BASIC_CACHE_LIMIT: usize = 48;

/// Splits whose approximate value is this far from the play they compete with keep it: the exact split value never moves
/// by more than about 0.04 (1 deck), so the decision can't change.
const SPLIT_MARGIN: f64 = 0.1;

/// A cell whose stand, hit, double and surrender values are this far apart keeps its decision whatever its hands of
/// three or more cards prefer (the light and the full basic strategy agree on 432 rule sets of 1 to 8 decks).
const CLOSE_CALL: f64 = 0.1;

/// [`CaEngine::basic_strategy`] and its exact off-the-top expectation ([`CaEngine::exact_off_top`]) for the rules,
/// remembered per thread for the last 48 rule sets (C#: BasicFor).
pub fn basic_for(rules: &Rules) -> (CaResult, ExactAdjust, f64) {
    basic_for_mode(rules, false)
}

/// `basic_for`; `light` leaves out the corrections for the analyses at a count (see `basic_strategy_mode`).
/// The rules that decide basic strategy and its expectation (not the penetration or the burn card).
fn basic_key(rules: &Rules) -> String {
    format!(
        "{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}",
        rules.decks,
        rules.h17,
        rules.hole_card.name(),
        rules.blackjack_pays,
        rules.double_on.name(),
        rules.das,
        rules.max_split_hands,
        rules.resplit_aces,
        rules.hit_split_aces,
        rules.surrender.name(),
        rules.insurance
    )
}

thread_local! {
    /// Effects of removal of recently used rules (they take ten exact evaluations); oldest first, at most 8.
    static EOR_CACHE: RefCell<Vec<(String, [f64; 11])>> = const { RefCell::new(Vec::new()) };
}

/// The change in the player's expectation when one card of a rank is taken out of the full shoe (`[1..=10]`), for basic
/// strategy played unchanged and worked out exactly (`exact_off_top` on each shoe). These are the effects of removal a
/// count's betting correlation is defined with (C#: EffectsOfRemoval). Remembered per thread for the last few rule sets.
pub fn effects_of_removal(rules: &Rules) -> [f64; 11] {
    let key = basic_key(rules);
    if let Some(e) = EOR_CACHE.with_borrow(|c| c.iter().find(|e| e.0 == key).map(|e| e.1)) {
        return e;
    }
    let (basic, _, off_top) = basic_for_mode(rules, true);
    let mut eng = CaEngine::new(rules);
    let full = full_shoe(rules.decks as f64);
    let mut eor = [0.0; 11];
    for (k, slot) in eor.iter_mut().enumerate().skip(1) {
        let mut shoe = full;
        shoe[k] -= 1.0;
        eng.own_dealer = Some(HashMap::new());
        *slot = eng.exact_on(&shoe, &basic) - off_top;
    }
    EOR_CACHE.with_borrow_mut(|c| {
        if c.len() >= 8 {
            c.remove(0);
        }
        c.push((key, eor));
    });
    eor
}

fn basic_for_mode(rules: &Rules, light: bool) -> (CaResult, ExactAdjust, f64) {
    let base = basic_key(rules);
    let full = format!("{base}|full");
    let key = if light { format!("{base}|light") } else { full.clone() };
    // A full result serves a light request as well (same flags and expectation).
    let found = BASIC_CACHE.with_borrow(|c| {
        c.iter().find(|e| e.0 == key || (light && e.0 == full)).map(|e| (e.1.clone(), e.2.clone(), e.3))
    });
    if let Some(hit) = found {
        return hit;
    }
    let mut eng = CaEngine::new(rules);
    let (res, adj) = eng.basic_strategy_mode(light);
    let off_top = eng.exact_off_top(&res);
    BASIC_CACHE.with_borrow_mut(|c| {
        if c.len() >= BASIC_CACHE_LIMIT {
            c.remove(0);
        }
        c.push((key, res.clone(), adj.clone(), off_top));
    });
    (res, adj, off_top)
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
    cell: usize,
    /// Probability of the deal; `wd`: of the two cards given the upcard (the cell average's weight).
    w: f64,
    wd: f64,
    s: f64,
    hh: f64,
    d: f64,
    rr: f64,
    p: f64,
    /// Cards left after the deal (upcard and both cards out); scales the exact corrections.
    np: f64,
    /// Chance that the dealer has blackjack.
    bj: f64,
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
    acc_p: Vec<f64>,
    combos: Vec<Combo>,
    cur: Vec<Combo>,
    // The rest of the last `evaluate_adj` that `decide` needs.
    base_flags: Vec<u8>,
    bj_total: f64,
    ace_share: f64,
    insurance_ev: f64,
    insure: bool,
    // Exact expectation off the top (see `exact_off_top`).
    xc: [f64; 11],
    own_dealer: Option<HashMap<u64, [f64; 7]>>,
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

/// Off-the-top player expectation for the rules with optimal total-dependent basic strategy
/// ([`CaEngine::basic_strategy`]), played as the simulator plays it (see [`CaEngine::exact_off_top`]).
pub fn house_edge_off_top(rules: &Rules) -> f64 {
    basic_for_mode(rules, true).2
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
            acc_p: vec![0.0; CELLS_PER_BIN],
            combos: Vec::with_capacity(600),
            cur: Vec::with_capacity(600),
            base_flags: vec![0u8; CELLS_PER_BIN],
            bj_total: 0.0,
            ace_share: 0.0,
            insurance_ev: 0.0,
            insure: false,
            xc: [0.0; 11],
            own_dealer: None,
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
        self.evaluate_adj(shoe, None, None)
    }

    /// Analysis of a round dealt from `shoe`. With a count model, `shoe` is the population before the deal and the
    /// cards each deal leaves unseen come from the model (the index plays at that count); deals are weighted by the
    /// shoe.
    pub fn evaluate_with(&mut self, shoe: &[f64; 11], model: Option<&CountModel>) -> CaResult {
        self.evaluate_adj(shoe, model, None)
    }

    /// The player's draw odds from the cards left after the deal with upcard `u` (`sp`, or `su` for the upcard
    /// defaults), `n` of them. In a peek game with an ace or ten up the player draws only after the dealer has shown no
    /// blackjack, so the hole card is one of the cards that can't make one: P(next card is r) = (c_r - P(hole card is
    /// r)) / (n - 1) (C#: DrawOdds).
    fn draw_odds(&mut self, use_sp: bool, n: f64, u: usize, peek_cond: bool) {
        let c = if use_sp { self.sp } else { self.su };
        for (r, pr) in self.p.iter_mut().enumerate().skip(1) {
            *pr = c[r] / n;
        }
        if !peek_cond || (u != 1 && u != 10) || n <= 1.0 {
            return;
        }
        let bj = if u == 1 { 10 } else { 1 };
        let nb = n - c[bj];
        if nb <= 1.0 {
            return;
        }
        for (r, pr) in self.p.iter_mut().enumerate().skip(1) {
            *pr = if r == bj { c[r] / (n - 1.0) } else { c[r] * (nb - 1.0) / (nb * (n - 1.0)) };
        }
    }

    /// `evaluate_with`, plus what exact card removal changes in each deal's values (see `basic_strategy`).
    pub fn evaluate_adj(
        &mut self,
        shoe: &[f64; 11],
        model: Option<&CountModel>,
        adj: Option<&ExactAdjust>,
    ) -> CaResult {
        self.base_flags.fill(0);
        self.combos.clear();

        let sr_before_peek = self.r.surrender_before_peek();
        let peek_cond = !self.r.dealer_bj_takes_all();
        let can_split = self.r.max_split_hands >= 2;
        let n = sum(shoe);
        self.bj_total = 0.0;
        self.ace_share = shoe[1] / n;
        self.insurance_ev = 0.0;
        self.insure = false;

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
            self.draw_odds(false, nu, u, peek_cond);
            self.fill_dp();
            for t in 4..=21usize {
                self.base_flags[hard_cell(t, u)] = if self.sev[t] >= self.h[t * 2] { flag::STAND } else { 0 };
                if t >= 12 {
                    self.base_flags[soft_cell(t, u)] =
                        if self.sev[t] >= self.h[(t - 10) * 2 + 1] { flag::STAND } else { 0 };
                }
            }

            if u == 1 {
                let pt = self.su[10] / nu;
                self.insurance_ev = 1.5 * pt - 0.5;
                self.insure = self.r.insurance && pt > 1.0 / 3.0;
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
                        self.bj_total += pu * w * (1.0 - pbj) * self.r.blackjack_pays;
                        continue;
                    }

                    let dbj = self.dealer(u, true, np, peek_cond);
                    self.fill_stand(dbj);
                    self.draw_odds(true, np, u, peek_cond);
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

                    self.combos.push(Combo {
                        u,
                        a,
                        b,
                        cell: if soft0 { soft_cell(best0, u) } else { hard_cell(h0, u) },
                        w: w * pu,
                        wd: match model.and_then(|m| m.table_weight.as_deref().map(|tw| table_weight(m, tw, u, a, b))) {
                            Some(tw) => w * tw,
                            None => w,
                        },
                        s: ev_s,
                        hh: ev_h,
                        d: ev_d,
                        rr: ev_r,
                        p: ev_p,
                        np,
                        bj: pbj,
                        can_d,
                    });
                }
            }
        }
        self.decide(model.is_none(), adj)
    }

    /// Flags, cell values and expectation from the deals of the last `evaluate_adj` (`combos`), with the exact
    /// corrections of `adj` added to each deal's hit, double and split values. Leaves `combos` as they are.
    fn decide(&mut self, off_top: bool, adj: Option<&ExactAdjust>) -> CaResult {
        const CPB: usize = CELLS_PER_BIN;
        let mut flags = self.base_flags.clone();
        let mut cell_ev = vec![f64::NAN; CPB * 5];
        self.acc.fill(0.0);
        self.acc_w.fill(0.0);
        self.acc_p.fill(0.0);
        let mut cell_bj = vec![0.0; CPB];
        self.cur.clear();
        let surr_allowed = self.r.surrender != SurrenderRule::None;
        let can_split = self.r.max_split_hands >= 2;

        for i in 0..self.combos.len() {
            let mut c = self.combos[i];
            if let Some(adj) = adj {
                // Removing a drawn card matters more the fewer cards are left: the corrections grow with 1 / cards left.
                let k = ExactAdjust::deal(c.u, c.a, c.b);
                let sc = adj.cards / c.np;
                c.hh += sc * adj.hit[k];
                if c.can_d {
                    c.d += sc * adj.dbl[k];
                }
                if c.a == c.b && can_split {
                    c.p += sc * adj.split[k];
                }
            }
            self.cur.push(c);
            let cell = c.cell;
            self.acc[cell * 4] += c.wd * c.s;
            self.acc[cell * 4 + 1] += c.wd * c.hh;
            if c.can_d {
                self.acc[cell * 4 + 2] += c.wd * c.d;
            }
            if surr_allowed {
                self.acc[cell * 4 + 3] += c.wd * c.rr;
            }
            self.acc_w[cell] += c.wd;
            self.acc_p[cell] += c.wd * c.bj;
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
            // The cell's stand-or-hit also plays its hands of three or more cards (`ratio` of them for each two-card
            // hand), which prefer standing by `shift` more than the two-card hands do (see `ExactAdjust`). So standing
            // is worth `s` plus what they gain by it, and where the two-card hands double or surrender, the
            // stand-or-hit is theirs alone. Without an adjustment both are 0 and the two-card hands decide.
            let (mut ratio, mut shift) = (0.0, 0.0);
            if let Some(adj) = adj
                && adj.stand_ratio[cell] > 0.0
                && !adj.stand_shift[cell].is_nan()
            {
                ratio = adj.stand_ratio[cell];
                shift = adj.stand_shift[cell];
            }
            let multi = s - hv + shift;
            let v_s = s + ratio * multi;
            let own = ratio * dmax(multi, 0.0);
            let mut stand = v_s >= hv;
            let best_hs = if stand { v_s } else { hv };
            let mut f = 0u8;
            if d + own > best_hs {
                f |= flag::DOUBLE;
            }
            if rv + own > dmax(best_hs, d + own) {
                f |= flag::SURRENDER;
            }
            if f != 0 && ratio > 0.0 {
                stand = multi >= 0.0;
            } else if f != 0 {
                stand = s >= hv;
            }
            if stand {
                f |= flag::STAND;
            }
            flags[cell] = f;
            cell_bj[cell] = self.acc_p[cell] / w;
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
        for c in &self.cur {
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
            cell_bj[pcell] = c.bj;
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
        let mut ev_total = self.bj_total;
        for c in &self.cur {
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
        if self.insure {
            ev_total += self.ace_share * self.insurance_ev;
        }

        let player_ev = if off_top { ev_total } else { f64::NAN };
        CaResult { flags, insure: self.insure, player_ev, insurance_ev: self.insurance_ev, cell_ev, cell_bj }
    }

    // ---------------- Exact expectation of a strategy off the top ----------------

    fn raw_dealer(&mut self, cards: u64, n: f64) -> [f64; 7] {
        let key = ((cards * 16 + self.x_up as u64) << 5) + self.x_game;
        // A shoe that isn't the full one (`effects_of_removal`) keeps its dealer distributions to itself.
        if let Some(own) = self.own_dealer.as_mut() {
            if let Some(d) = own.get(&key) {
                return *d;
            }
            self.dags[self.x_up - 1].evaluate(&self.xc, n, &mut self.mass, &mut self.dist);
            own.insert(key, self.dist);
            return self.dist;
        }
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

    /// Sets up the exact evaluation of a strategy for these rules: game key, fresh memos (C#: BeginExact).
    fn begin_exact(&mut self) {
        self.x_game = (self.r.decks * 2 + if self.r.h17 { 1 } else { 0 }) as u64;
        self.hand_memo.clear();
        self.split_memo.clear();
        self.x_takes_all = self.r.dealer_bj_takes_all();
    }

    /// Chance that the dealer's hole card gives a blackjack, from the cards in `xc`.
    fn deal_bj(&self, u: usize, np: f64) -> f64 {
        if u == 1 {
            self.xc[10] / np
        } else if u == 10 {
            self.xc[1] / np
        } else {
            0.0
        }
    }

    /// Puts the cards left after the deal (upcard `u`, cards `a` and `b`) in `xc`; returns their number.
    fn set_deal(&mut self, shoe: &[f64; 11], n: f64, u: usize, a: usize, b: usize) -> f64 {
        self.x_up = u;
        self.xc = *shoe;
        self.xc[u] = dmax(0.0, self.xc[u] - 1.0);
        self.xc[a] = dmax(0.0, self.xc[a] - 1.0);
        self.xc[b] = dmax(0.0, self.xc[b] - 1.0);
        n - 3.0
    }

    /// Basic strategy off the top with exact card removal (C#: BasicStrategy). `evaluate` keeps the player's draw odds
    /// and the dealer's distribution as they are after the first three cards, which gets close plays wrong with one or
    /// two decks (e.g. 11 v A with 2 decks S17, A,2 v 4 with 1 deck, or splitting 2,2 v 3 with 1 deck). Here every hit
    /// (followed by the strategy's own play, as the simulator plays it), double and split of the two-card hands takes
    /// the drawn cards out of the shoe, as `exact_off_top` does. In a cell whose two-card hands all double or surrender,
    /// the stand/hit fallback comes from the hands of three or more cards that actually use it (16 v T with surrender:
    /// stand). Also returns the differences to `evaluate`'s values, which `strategy::build` carries over to the
    /// analyses at each count. See [`basic_for`] for the remembered results.
    pub fn basic_strategy(&mut self) -> (CaResult, ExactAdjust) {
        self.basic_strategy_mode(false)
    }

    /// `light`: only what the decisions off the top need (for house edges), not the corrections for the analyses at a
    /// count (C#: BasicStrategy(adj, light)). Exact split values only for pairs whose split is within `SPLIT_MARGIN` of
    /// the alternative, and fallbacks only for the cells whose two-card hands double or surrender. The flags and the
    /// expectation are the same (checked on 216 rule sets of 1 to 8 decks).
    fn basic_strategy_mode(&mut self, light: bool) -> (CaResult, ExactAdjust) {
        let shoe = full_shoe(self.r.decks as f64);
        let n = sum(&shoe);
        let first = self.evaluate(&shoe);
        let deals = self.combos.clone();
        let mut adj = ExactAdjust::new();
        adj.cards = n - 3.0;
        self.begin_exact();
        let peek_cond = !self.r.dealer_bj_takes_all();

        // Doubles with every drawn card out of the shoe (they don't depend on the strategy).
        for c in &deals {
            if !c.can_d {
                continue;
            }
            let np = self.set_deal(&shoe, n, c.u, c.a, c.b);
            let cut = if peek_cond { self.deal_bj(c.u, np) } else { 0.0 };
            let cards = with_card(with_card(0, c.a), c.b);
            adj.dbl[ExactAdjust::deal(c.u, c.a, c.b)] =
                (self.draw(&[], cards, np, c.a + c.b, c.a == 1 || c.b == 1, 2.0, false) - cut) - c.d;
        }
        // Hits, then the strategy's own play, every card out of the shoe. The flags depend on these values in turn, so
        // repeat until they settle (one or two rounds in practice).
        let mut res = first;
        for _ in 0..6 {
            self.begin_exact();
            let flags = res.flags.clone();
            for c in &deals {
                let np = self.set_deal(&shoe, n, c.u, c.a, c.b);
                let cut = if peek_cond { self.deal_bj(c.u, np) } else { 0.0 };
                let cards = with_card(with_card(0, c.a), c.b);
                adj.hit[ExactAdjust::deal(c.u, c.a, c.b)] =
                    (self.draw(&flags, cards, np, c.a + c.b, c.a == 1 || c.b == 1, 1.0, false) - cut) - c.hh;
            }
            let next = self.decide(true, Some(&adj));
            let same = next.flags == res.flags;
            res = next;
            if same {
                break;
            }
        }

        // Splits, played by those hard and soft flags.
        if self.r.max_split_hands >= 2 {
            self.begin_exact();
            let flags = res.flags.clone();
            for c in &deals {
                if c.a != c.b {
                    continue;
                }
                let k = ExactAdjust::deal(c.u, c.a, c.b);
                if light {
                    // The play the split competes with (`decide`'s pass 2), with the exact hit and double values.
                    let tf = flags[if c.a == 1 { soft_cell(12, c.u) } else { hard_cell(2 * c.a, c.u) }];
                    let other = if (tf & flag::DOUBLE) != 0 && c.can_d {
                        c.d + adj.dbl[k]
                    } else if (tf & flag::STAND) != 0 {
                        c.s
                    } else {
                        c.hh + adj.hit[k]
                    };
                    if (c.p - other).abs() >= SPLIT_MARGIN {
                        continue;
                    }
                }
                let np = self.set_deal(&shoe, n, c.u, c.a, c.b);
                let pbj = self.deal_bj(c.u, np);
                adj.split[k] = self.split_exact(&flags, c.a, np, pbj, peek_cond) - c.p;
            }
            res = self.decide(true, Some(&adj));
        }

        // Stand/hit fallbacks of the cells whose two-card hands double or surrender. (A second round, with the hands these
        // fallbacks send to higher totals, changed no flag in 216 rule sets of 1 to 8 decks.)
        self.begin_exact();
        let flags = res.flags.clone();
        self.stand_shift(&shoe, n, &deals, &flags, &res.cell_ev, &mut adj, light);
        res = self.decide(true, Some(&adj));
        (res, adj)
    }

    /// For every hard and soft cell: how much more the hands of three or more cards that play it prefer standing than
    /// its two-card hands do (C#: StandShift). That is the mean of stand minus hit-and-play-on over the multi-card hands
    /// the strategy (`flags`) reaches by hitting, every card out of the shoe and each hand weighted by how often it is
    /// reached, minus the cell's two-card stand-minus-hit (`cell_ev`). Split hands are left out. Hands are visited by
    /// number of cards and in key order, so the sums are the same as in C#.
    #[allow(clippy::too_many_arguments)]
    fn stand_shift(
        &mut self,
        shoe: &[f64; 11],
        n_all: f64,
        deals: &[Combo],
        flags: &[u8],
        cell_ev: &[f64],
        adj: &mut ExactAdjust,
        light: bool,
    ) {
        let mut gain = [0.0f64; 440];
        let mut reach = [0.0f64; 440];
        let mut two = [0.0f64; 440];
        for c in deals {
            two[c.cell] += c.w;
        }
        // light: only the cells where the hands of three or more cards can change a decision, which are those whose
        // two-card hands double or surrender and those with a close call.
        let mut skip = [false; 440];
        if light {
            for cell in 0..440 {
                if (flags[cell] & (flag::DOUBLE | flag::SURRENDER)) != 0 {
                    continue;
                }
                let (s2, h2, d2, r2) =
                    (cell_ev[cell * 5], cell_ev[cell * 5 + 1], cell_ev[cell * 5 + 2], cell_ev[cell * 5 + 3]);
                if s2.is_nan() || h2.is_nan() {
                    continue;
                }
                let best = dmax(s2, h2);
                let near = |x: f64| best - x < CLOSE_CALL; // false where the option doesn't exist (NaN)
                skip[cell] = (s2 - h2).abs() >= CLOSE_CALL && !near(d2) && !near(r2);
            }
        }
        let surr_allowed = self.r.surrender != SurrenderRule::None;
        for u in 1..=10usize {
            let mut level: BTreeMap<u64, f64> = BTreeMap::new();
            // Two-card hands that hit: their third card starts the multi-card hands.
            for c in deals {
                if c.u != u {
                    continue;
                }
                let h0 = c.a + c.b;
                let soft0 = (c.a == 1 || c.b == 1) && h0 + 10 <= 21;
                let pf = if c.a == c.b { flags[pair_cell(c.a, u)] } else { 0 };
                let tf = flags[if soft0 { soft_cell(h0 + 10, u) } else { hard_cell(h0, u) }];
                if (surr_allowed && (pf & flag::SURRENDER) != 0)
                    || (pf & flag::SPLIT) != 0
                    || (surr_allowed && (tf & flag::SURRENDER) != 0)
                    || ((tf & flag::DOUBLE) != 0 && c.can_d)
                    || (tf & flag::STAND) != 0
                {
                    continue;
                }
                let np = self.set_deal(shoe, n_all, u, c.a, c.b);
                let cards = with_card(with_card(0, c.a), c.b);
                for r in 1..=10usize {
                    if self.xc[r] <= 0.0 || h0 + r > 21 {
                        continue;
                    }
                    *level.entry(with_card(cards, r)).or_insert(0.0) += c.w * (self.xc[r] / np);
                }
            }
            while !level.is_empty() {
                let mut next: BTreeMap<u64, f64> = BTreeMap::new();
                for (&cards, &rch) in &level {
                    let (mut h, mut held) = (0usize, 0usize);
                    for (r, &sr) in shoe.iter().enumerate().skip(1) {
                        let m = ((cards >> (5 * (r - 1))) & 31) as usize;
                        self.xc[r] = sr - if r == u { 1.0 } else { 0.0 } - m as f64;
                        h += r * m;
                        held += m;
                    }
                    let ace = (cards & 31) != 0;
                    let n = n_all - 1.0 - held as f64;
                    self.x_up = u;
                    let soft = ace && h <= 11;
                    let best = if soft { h + 10 } else { h };
                    if best == 21 {
                        continue;
                    }
                    let cell = if soft { soft_cell(best, u) } else { hard_cell(h, u) };
                    if !skip[cell] {
                        let st = self.terminal(cards, n, best, 1.0);
                        let hit = self.draw(flags, cards, n, h, ace, 1.0, false);
                        gain[cell] += rch * (st - hit);
                        reach[cell] += rch;
                    }
                    if (flags[cell] & flag::STAND) != 0 {
                        continue;
                    }
                    for r in 1..=10usize {
                        if self.xc[r] <= 0.0 || h + r > 21 {
                            continue;
                        }
                        *next.entry(with_card(cards, r)).or_insert(0.0) += rch * (self.xc[r] / n);
                    }
                }
                level = next;
            }
        }
        for cell in 0..440 {
            adj.stand_shift[cell] = f64::NAN;
            adj.stand_ratio[cell] = 0.0;
            let (s2, h2) = (cell_ev[cell * 5], cell_ev[cell * 5 + 1]);
            if reach[cell] <= 0.0 || two[cell] <= 0.0 || s2.is_nan() || h2.is_nan() {
                continue;
            }
            adj.stand_shift[cell] = gain[cell] / reach[cell] - (s2 - h2);
            adj.stand_ratio[cell] = reach[cell] / two[cell];
        }
    }

    /// Off-the-top expectation of the strategy in `basic` (its flags and insurance) with every card the player draws
    /// taken out of the shoe: hits, doubles and split hands follow the flags exactly as the simulator plays them
    /// (splits as in `split_exact`). `evaluate`'s `player_ev` assumes the best play after each hit, with the draw odds
    /// fixed after the first three cards, which is off by up to about 0.05% with one deck. In peek games each deal loses
    /// its blackjack probability once and the dealer's blackjacks are left out of every later outcome, which is exact
    /// because the order of the unseen cards doesn't matter.
    pub fn exact_off_top(&mut self, basic: &CaResult) -> f64 {
        self.exact_on(&full_shoe(self.r.decks as f64), basic)
    }

    /// `exact_off_top` for a round dealt from `shoe` (the full shoe, or one with a card taken out).
    fn exact_on(&mut self, shoe: &[f64; 11], basic: &CaResult) -> f64 {
        let shoe = *shoe;
        self.begin_exact();
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

#[cfg(test)]
mod tests {
    use super::*;

    /// A long session of games: the dealer memo is cleared on the way but never outgrows a table of 2^18 buckets, and
    /// a game worked out again after a clear gives the same result to the last bit.
    #[test]
    fn dealer_cache_stays_in_one_table() {
        let first = house_edge_off_top(&Rules::default());
        for decks in 1..=8 {
            for h17 in [false, true] {
                house_edge_off_top(&Rules { decks, h17, ..Rules::default() });
                let (len, capacity) = DEALER_CACHE.with_borrow(|c| (c.len(), c.capacity()));
                assert!(len <= DEALER_CACHE_LIMIT, "{decks} decks: {len} entries");
                assert!(capacity <= DEALER_CACHE_LIMIT, "{decks} decks: the table grew to {capacity} entries");
            }
        }
        assert_eq!(house_edge_off_top(&Rules::default()).to_bits(), first.to_bits());
    }
}
