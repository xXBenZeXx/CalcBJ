//! Shoe simulator (C#: Simulator.cs). A worker deals the same cards from the same seed as the C# version, so
//! single-threaded runs give identical tallies.

use crate::ca::flag;
use crate::num::{dmax, round_even_i32};
use crate::strategy::{BIN_MAX, BIN_MIN, CELLS_PER_BIN, NUM_BINS, PlayTable};
use crate::types::{CountMetric, CountSystem, DeckEstimation, HoleCardRule, Rules, SurrenderRule, TcRounding};

/// Rounds a simulation thread takes at a time.
pub const CHUNK: i64 = 250_000;

/// Per-count-bin results of a simulation. Results are stored in twentieths of a unit so they stay exact.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase", default))]
pub struct SimData {
    pub bin_min: i32,
    pub num_bins: usize,
    pub spots: i32,
    pub n: Vec<i64>,
    pub sa: Vec<i64>,
    pub qa: Vec<i64>,
    pub sb: Vec<i64>,
    pub qb: Vec<i64>,
    pub ab: Vec<i64>,
    pub rounds: i64,
    pub shoes: i64,
    pub emergency: i64,
    pub seconds: f64,
    pub threads: i32,
    pub seed: u64,
    pub key: String,
    pub rules_text: String,
    pub count_text: String,
    pub strategy_text: String,
    pub metric: CountMetric,
    pub basic_ev: f64,
    /// When the results were produced, in milliseconds since 1970 (set by the caller).
    pub created: f64,
}

impl Default for SimData {
    fn default() -> Self {
        SimData::create(1)
    }
}

impl SimData {
    pub fn create(spots: i32) -> SimData {
        SimData {
            bin_min: BIN_MIN,
            num_bins: NUM_BINS,
            spots,
            n: vec![0; NUM_BINS],
            sa: vec![0; NUM_BINS],
            qa: vec![0; NUM_BINS],
            sb: vec![0; NUM_BINS],
            qb: vec![0; NUM_BINS],
            ab: vec![0; NUM_BINS],
            rounds: 0,
            shoes: 0,
            emergency: 0,
            seconds: 0.0,
            threads: 0,
            seed: 0,
            key: String::new(),
            rules_text: String::new(),
            count_text: String::new(),
            strategy_text: String::new(),
            metric: CountMetric::TrueCount,
            basic_ev: 0.0,
            created: 0.0,
        }
    }

    /// Combines two independent runs of the same configuration (their counts simply add up); the other fields come
    /// from `b`.
    pub fn merge(a: Option<&SimData>, b: Option<&SimData>) -> Option<SimData> {
        match (a, b) {
            (None, None) => None,
            (None, Some(b)) => Some(b.clone()),
            (Some(a), None) => Some(a.clone()),
            (Some(a), Some(b)) => {
                let mut m = b.clone();
                for i in 0..m.num_bins.min(a.num_bins) {
                    m.n[i] += a.n[i];
                    m.sa[i] += a.sa[i];
                    m.qa[i] += a.qa[i];
                    m.sb[i] += a.sb[i];
                    m.qb[i] += a.qb[i];
                    m.ab[i] += a.ab[i];
                }
                m.rounds += a.rounds;
                m.shoes += a.shoes;
                m.emergency += a.emergency;
                m.seconds += a.seconds;
                Some(m)
            }
        }
    }

    /// Adds a worker's tallies (call [`SimWorker::reset`] afterwards when the worker continues).
    pub fn add_from(&mut self, w: &SimWorker) {
        for i in 0..self.num_bins {
            self.n[i] += w.n[i];
            self.sa[i] += w.sa[i];
            self.qa[i] += w.qa[i];
            self.sb[i] += w.sb[i];
            self.qb[i] += w.qb[i];
            self.ab[i] += w.ab[i];
        }
        self.rounds += w.rounds;
        self.shoes += w.shoes;
        self.emergency += w.emergency;
    }
}

/// Count-bin lookup by (cards remaining, running count). Cards-remaining values that share a deck estimate share a
/// row, so for ¼/½/full-deck estimation or running-count play the table is only a few KB and stays in cache.
#[derive(Clone, Debug, PartialEq)]
pub struct BinLookup {
    pub rc_min: i32,
    pub rc_max: i32,
    pub width: i32,
    pub cr_row: Vec<i32>,
    pub rows: Vec<i16>,
}

/// Everything the simulation workers of one configuration share.
#[derive(Clone, Debug)]
pub struct SimPlan {
    pub rules: Rules,
    pub count: CountSystem,
    pub spots: i32,
    pub bins: BinLookup,
    pub fixed: Vec<u8>,
    pub flags: Vec<u8>,
    pub insure: Vec<bool>,
}

impl SimPlan {
    pub fn new(rules: &Rules, count: &CountSystem, table: &PlayTable, spots: i32) -> SimPlan {
        SimPlan {
            rules: rules.clone(),
            count: count.clone(),
            spots,
            bins: build_bins(rules, count),
            fixed: fixed_cells(table),
            flags: table.flags.clone(),
            insure: table.insure.clone(),
        }
    }
}

/// Seed of simulation thread `idx` (0-based) in a run seeded with `seed`.
pub fn worker_seed(seed: u64, idx: i32) -> u64 {
    seed.wrapping_add((idx as u64).wrapping_add(1).wrapping_mul(0x9E37_79B9_7F4A_7C15))
}

/// Runs `rounds` rounds on `threads` threads like the C# Simulator: every thread deals its own shoe and takes
/// 250 000-round chunks until all are claimed. With one thread the result depends only on the seed.
#[cfg(not(target_arch = "wasm32"))]
pub fn simulate(plan: &SimPlan, rounds: i64, threads: i32, seed: u64) -> SimData {
    use std::sync::Mutex;
    use std::sync::atomic::{AtomicI64, Ordering};

    let start = std::time::Instant::now();
    let n = threads.max(1);
    let acc = Mutex::new(SimData::create(plan.spots));
    let claimed = AtomicI64::new(0);
    std::thread::scope(|scope| {
        for idx in 0..n {
            let (acc, claimed) = (&acc, &claimed);
            scope.spawn(move || {
                let mut w = SimWorker::new(plan, worker_seed(seed, idx));
                w.shuffle();
                loop {
                    let first = claimed.fetch_add(CHUNK, Ordering::Relaxed);
                    if first >= rounds {
                        break;
                    }
                    w.run(CHUNK.min(rounds - first));
                    acc.lock().expect("simulation results").add_from(&w);
                    w.reset();
                }
            });
        }
    });
    let mut d = acc.into_inner().expect("simulation results");
    d.threads = n;
    d.seed = seed;
    d.seconds = start.elapsed().as_secs_f64();
    d
}

pub fn estimate_decks(est: DeckEstimation, cards_remaining: i32) -> f64 {
    let cr = cards_remaining as f64;
    match est {
        DeckEstimation::QuarterDeck => dmax(1.0, (cr / 13.0).round()) / 4.0,
        DeckEstimation::HalfDeck => dmax(1.0, (cr / 26.0).round()) / 2.0,
        DeckEstimation::FullDeck => dmax(1.0, (cr / 52.0).round()),
        DeckEstimation::Exact => cards_remaining.max(6) as f64 / 52.0,
    }
}

/// Count value (TC or RC, in tag units) for a scaled running count and cards remaining.
pub fn metric_value(cs: &CountSystem, scale: i32, rc_scaled: i32, cards_remaining: i32) -> i32 {
    if cs.metric == CountMetric::RunningCount {
        return (rc_scaled as f64 / scale as f64 + 1e-9).floor() as i32;
    }
    let tc = rc_scaled as f64 / (scale as f64 * estimate_decks(cs.deck_est, cards_remaining));
    match cs.rounding {
        TcRounding::Truncate => (tc + if tc >= 0.0 { 1e-9 } else { -1e-9 }) as i32,
        TcRounding::Round => tc.round() as i32,
        TcRounding::Floor => (tc + 1e-9).floor() as i32,
    }
}

pub fn build_bins(r: &Rules, cs: &CountSystem) -> BinLookup {
    let scale = cs.scale();
    let irc = round_even_i32(cs.irc(r.decks) * scale as f64);
    let (mut pos, mut neg) = (0i64, 0i64);
    for code in 0..20 {
        let rank = (code >> 1) + 1;
        let per_deck: i64 = if rank == 10 { 8 } else { 2 };
        let t = round_even_i32(cs.tag_of_code(code) * scale as f64) as i64;
        if t > 0 {
            pos += t * per_deck * r.decks as i64;
        } else {
            neg += t * per_deck * r.decks as i64;
        }
    }
    let rc_min = (irc as i64 + neg).max(-4000) as i32;
    let rc_max = (irc as i64 + pos).min(4000) as i32;
    let width = rc_max - rc_min + 1;
    let n_cards = r.total_cards();
    let mut cr_row = vec![0i32; n_cards as usize + 1];
    let mut rows: Vec<Vec<i16>> = Vec::new();
    for cr in 0..=n_cards {
        let row: Vec<i16> = (rc_min..=rc_max)
            .map(|rc| (metric_value(cs, scale, rc, cr).clamp(BIN_MIN, BIN_MAX) - BIN_MIN) as i16)
            .collect();
        if rows.last() != Some(&row) {
            rows.push(row);
        }
        cr_row[cr as usize] = (rows.len() as i32 - 1) * width;
    }
    BinLookup { rc_min, rc_max, width, cr_row, rows: rows.concat() }
}

/// Per cell: its flags when they are the same at every count (no index play), otherwise 0xFF.
pub fn fixed_cells(t: &PlayTable) -> Vec<u8> {
    (0..CELLS_PER_BIN)
        .map(|c| {
            let v = t.flags[c];
            let same = (1..NUM_BINS).all(|b| t.flags[b * CELLS_PER_BIN + c] == v);
            if same { v } else { 0xFF }
        })
        .collect()
}

const MAXH: usize = 8;
const MAX_SPOTS: usize = 7;
const CPB: usize = CELLS_PER_BIN;
/// Cards are stored packed: ((tag + TAG_BIAS) << 4) | rank, so one load gives both the rank and the count tag.
const TAG_BIAS: i32 = 512;

fn split_mix(x: &mut u64) -> u64 {
    *x = x.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// One simulation thread: its own shoe, random generator and tallies.
pub struct SimWorker {
    n_cards: i32,
    cut: i32,
    spots: usize,
    max_hands: usize,
    rc_min: i32,
    rc_max: i32,
    irc: i32,
    bj20: i32,
    spot_a: usize,
    spot_b: Option<usize>,
    dbl_mode: i32,
    h17: bool,
    das: bool,
    rsa: bool,
    hsa: bool,
    peek: bool,
    takes_all: bool,
    burn: bool,
    es_peek: bool,
    surr_at_turn: bool,
    ins_on: bool,
    late_surr: bool,
    insure: Vec<bool>,
    shoe: Vec<i32>,
    base_shoe: Vec<i32>,
    scratch: Vec<i32>,
    pos: i32,
    rc: i32,
    round_start: i32,
    hole_v: i32,
    hole_hidden: bool,
    must_shuffle: bool,
    fresh_shoe: bool,
    rng: [u64; 4],
    rbuf: u64,
    rhave: bool,
    bin_rows: Vec<i16>,
    cr_row: Vec<i32>,
    fixed: Vec<u8>,
    flags: Vec<u8>,
    p1: [i32; MAX_SPOTS],
    p2: [i32; MAX_SPOTS],
    status: [i32; MAX_SPOTS],
    insured: [bool; MAX_SPOTS],
    result: [i32; MAX_SPOTS],
    n_hands: [usize; MAX_SPOTS],
    h_total: [i32; MAX_SPOTS * MAXH],
    h_bet: [i32; MAX_SPOTS * MAXH],
    hf: [i32; MAXH],
    hs: [i32; MAXH],
    pub n: Vec<i64>,
    pub sa: Vec<i64>,
    pub qa: Vec<i64>,
    pub sb: Vec<i64>,
    pub qb: Vec<i64>,
    pub ab: Vec<i64>,
    pub rounds: i64,
    pub shoes: i64,
    pub emergency: i64,
}

impl SimWorker {
    pub fn new(plan: &SimPlan, seed: u64) -> SimWorker {
        let r = &plan.rules;
        let cs = &plan.count;
        let n_cards = r.total_cards();
        let spots = plan.spots.clamp(1, MAX_SPOTS as i32);
        let peek = r.hole_card == HoleCardRule::UsPeek;
        let es_peek = peek && r.surrender == SurrenderRule::Early;
        let scale = cs.scale();
        let packed: [i32; 20] = std::array::from_fn(|code| {
            ((round_even_i32(cs.tag_of_code(code) * scale as f64) + TAG_BIAS) << 4) | ((code as i32 >> 1) + 1)
        });
        let mut base_shoe = Vec::with_capacity(n_cards as usize);
        for _ in 0..r.decks {
            for rk in 1..=10usize {
                let per = if rk == 10 { 8 } else { 2 };
                for _ in 0..per {
                    base_shoe.push(packed[(rk - 1) * 2]);
                    base_shoe.push(packed[(rk - 1) * 2 + 1]);
                }
            }
        }
        let mut x = seed;
        let rng = [split_mix(&mut x), split_mix(&mut x), split_mix(&mut x), split_mix(&mut x)];
        let spots = spots as usize;
        SimWorker {
            n_cards,
            cut: r.cut_card(spots as i32),
            spots,
            max_hands: r.max_split_hands.clamp(1, MAXH as i32) as usize,
            rc_min: plan.bins.rc_min,
            rc_max: plan.bins.rc_max,
            irc: round_even_i32(cs.irc(r.decks) * scale as f64),
            bj20: round_even_i32(r.blackjack_pays * 20.0),
            spot_a: spots - 1,
            spot_b: if spots >= 2 { Some(spots - 2) } else { None },
            dbl_mode: match r.double_on {
                crate::types::DoubleRule::AnyTwo => 0,
                crate::types::DoubleRule::NineToEleven => 1,
                crate::types::DoubleRule::TenToEleven => 2,
            },
            h17: r.h17,
            das: r.das,
            rsa: r.resplit_aces,
            hsa: r.hit_split_aces,
            peek,
            takes_all: r.hole_card == HoleCardRule::Enhc,
            burn: r.burn_card,
            es_peek,
            surr_at_turn: r.surrender != SurrenderRule::None && !es_peek,
            ins_on: r.insurance,
            late_surr: r.surrender == SurrenderRule::Late,
            insure: plan.insure.clone(),
            shoe: base_shoe.clone(),
            scratch: vec![0; n_cards as usize],
            base_shoe,
            pos: 0,
            rc: 0,
            round_start: 0,
            hole_v: -1,
            hole_hidden: false,
            must_shuffle: false,
            fresh_shoe: false,
            rng,
            rbuf: 0,
            rhave: false,
            bin_rows: plan.bins.rows.clone(),
            cr_row: plan.bins.cr_row.clone(),
            fixed: plan.fixed.clone(),
            flags: plan.flags.clone(),
            p1: [0; MAX_SPOTS],
            p2: [0; MAX_SPOTS],
            status: [0; MAX_SPOTS],
            insured: [false; MAX_SPOTS],
            result: [0; MAX_SPOTS],
            n_hands: [0; MAX_SPOTS],
            h_total: [0; MAX_SPOTS * MAXH],
            h_bet: [0; MAX_SPOTS * MAXH],
            hf: [0; MAXH],
            hs: [0; MAXH],
            n: vec![0; NUM_BINS],
            sa: vec![0; NUM_BINS],
            qa: vec![0; NUM_BINS],
            sb: vec![0; NUM_BINS],
            qb: vec![0; NUM_BINS],
            ab: vec![0; NUM_BINS],
            rounds: 0,
            shoes: 0,
            emergency: 0,
        }
    }

    /// xoshiro256**.
    #[inline]
    fn next(&mut self) -> u64 {
        let s = &mut self.rng;
        let r = s[1].wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s[1] << 17;
        s[2] ^= s[0];
        s[3] ^= s[1];
        s[1] ^= s[2];
        s[0] ^= s[3];
        s[2] ^= t;
        s[3] = s[3].rotate_left(45);
        r
    }

    /// Uniform integer in [0, n), using each 64-bit generator output as two 32-bit draws.
    #[inline]
    fn next_int(&mut self, n: i32) -> i32 {
        let x = if self.rhave {
            self.rhave = false;
            self.rbuf as u32
        } else {
            self.rbuf = self.next();
            self.rhave = true;
            (self.rbuf >> 32) as u32
        };
        ((x as u64 * n as u64) >> 32) as i32
    }

    /// Clears the tallies (the shoe and the generator carry on).
    pub fn reset(&mut self) {
        for v in [&mut self.n, &mut self.sa, &mut self.qa, &mut self.sb, &mut self.qb, &mut self.ab] {
            v.fill(0);
        }
        self.rounds = 0;
        self.shoes = 0;
        self.emergency = 0;
    }

    /// New shoe. Cards are shuffled lazily: each draw takes a uniformly random card from the undealt part of the
    /// array (one Fisher–Yates step), which is equivalent to a full shuffle but only pays for cards actually dealt.
    pub fn shuffle(&mut self) {
        self.pos = 0;
        self.rc = self.irc;
        self.must_shuffle = false;
        self.fresh_shoe = true;
        if self.burn {
            self.draw_hidden();
        }
    }

    /// The shoe ran out mid-round: like a dealer, shuffle the discards (not the cards on the table) and continue.
    /// The count restarts from the cards still in view; the whole shoe is reshuffled after this round.
    fn mid_round_reshuffle(&mut self) {
        if self.round_start <= 0 {
            return;
        }
        let rs = self.round_start as usize;
        let in_play = (self.n_cards - self.round_start) as usize;
        self.scratch[..in_play].copy_from_slice(&self.shoe[rs..rs + in_play]);
        self.shoe.copy_within(0..rs, in_play);
        self.shoe[..in_play].copy_from_slice(&self.scratch[..in_play]);
        let mut r = self.irc;
        for &v in &self.shoe[..in_play] {
            r += (v >> 4) - TAG_BIAS;
        }
        if self.hole_hidden && self.hole_v >= 0 {
            r -= (self.hole_v >> 4) - TAG_BIAS;
        }
        self.rc = r;
        self.pos = in_play as i32;
        self.round_start = 0;
        self.must_shuffle = true;
        self.emergency += 1;
    }

    pub fn run(&mut self, rounds: i64) {
        for _ in 0..rounds {
            self.play_round();
        }
        self.rounds += rounds;
    }

    #[inline]
    fn bin(&self) -> usize {
        let cr = (self.n_cards - self.pos).max(0);
        let r = self.rc.clamp(self.rc_min, self.rc_max);
        self.bin_rows[(self.cr_row[cr as usize] + (r - self.rc_min)) as usize] as usize
    }

    /// Decision flags for a cell at the current count; cells without index plays skip the count lookup.
    #[inline]
    fn fl(&self, cell: usize) -> u8 {
        let f = self.fixed[cell];
        if f != 0xFF { f } else { self.flags[self.bin() * CPB + cell] }
    }

    /// Deals a card face up: returns its rank and adds its tag to the running count.
    #[inline]
    fn draw(&mut self) -> i32 {
        let v = self.draw_hidden();
        self.rc += (v >> 4) - TAG_BIAS;
        v & 15
    }

    /// Deals a card face down (packed value, not counted).
    #[inline]
    fn draw_hidden(&mut self) -> i32 {
        if self.pos >= self.n_cards {
            self.mid_round_reshuffle();
            if self.pos >= self.n_cards {
                self.emergency += 1;
                let i = self.next_int(self.n_cards) as usize;
                return self.base_shoe[i];
            }
        }
        let p = self.pos;
        let j = (p + self.next_int(self.n_cards - p)) as usize;
        self.shoe.swap(j, p as usize);
        self.pos = p + 1;
        self.shoe[p as usize]
    }

    fn can_double(&self, h: i32, soft: bool) -> bool {
        if self.dbl_mode == 0 {
            return true;
        }
        if soft {
            return false;
        }
        if self.dbl_mode == 1 { (9..=11).contains(&h) } else { (10..=11).contains(&h) }
    }

    fn wants_surrender(&self, c1: i32, c2: i32, up: i32) -> bool {
        if c1 == c2 {
            let pf = self.fl((440 + (c1 - 1) * 10 + (up - 1)) as usize);
            if (pf & flag::SURRENDER) != 0 {
                return true;
            }
            if (pf & flag::SPLIT) != 0 && self.max_hands >= 2 {
                return false;
            }
        }
        let h = c1 + c2;
        let soft = (c1 == 1 || c2 == 1) && h <= 11;
        let cell = if soft { 220 + (h + 10) * 10 + (up - 1) } else { h * 10 + (up - 1) };
        (self.fl(cell as usize) & flag::SURRENDER) != 0
    }

    fn play_hand(&mut self, a: i32, b: i32, up: i32, split: bool, bet: &mut i32) -> i32 {
        let mut h = a + b;
        let mut ace = a == 1 || b == 1;
        let mut soft = ace && h <= 11;
        let mut best = if soft { h + 10 } else { h };
        if best == 21 {
            return 21;
        }
        let mut cell = if soft { 220 + best * 10 + (up - 1) } else { h * 10 + (up - 1) };
        let mut f = self.fl(cell as usize);
        // Split aces may be hit (HSA) but not doubled, as in the combinatorial analysis.
        if (f & flag::DOUBLE) != 0 && (!split || (self.das && a != 1)) && self.can_double(h, soft) {
            let c = self.draw();
            h += c;
            if c == 1 {
                ace = true;
            }
            *bet = 2;
            if h > 21 {
                return 99;
            }
            return if ace && h <= 11 { h + 10 } else { h };
        }
        while (f & flag::STAND) == 0 {
            let c = self.draw();
            h += c;
            if c == 1 {
                ace = true;
            }
            if h > 21 {
                return 99;
            }
            soft = ace && h <= 11;
            best = if soft { h + 10 } else { h };
            if best == 21 {
                return 21;
            }
            cell = if soft { 220 + best * 10 + (up - 1) } else { h * 10 + (up - 1) };
            f = self.fl(cell as usize);
        }
        best
    }

    fn play_spot(&mut self, s: usize, up: i32) {
        let b0 = s * MAXH;
        let mut nh = 1;
        self.hf[0] = self.p1[s];
        self.hs[0] = self.p2[s];
        let mut split = false;
        let mut h = 0;
        while h < nh {
            let a = self.hf[h];
            let mut b = self.hs[h];
            if b == 0 {
                b = self.draw();
            }
            while a == b && nh < self.max_hands && (a != 1 || !split || self.rsa) {
                if (self.fl((440 + (a - 1) * 10 + (up - 1)) as usize) & flag::SPLIT) == 0 {
                    break;
                }
                self.hf[nh] = a;
                self.hs[nh] = 0;
                nh += 1;
                split = true;
                b = self.draw();
            }
            let mut bet = 1;
            let total = if split && a == 1 && !self.hsa {
                if b == 1 { 12 } else { 11 + b }
            } else {
                self.play_hand(a, b, up, split, &mut bet)
            };
            self.h_total[b0 + h] = total;
            self.h_bet[b0 + h] = bet;
            h += 1;
        }
        self.n_hands[s] = nh;
    }

    fn record(&mut self, bin: usize) {
        let a = self.result[self.spot_a] as i64;
        self.n[bin] += 1;
        self.sa[bin] += a;
        self.qa[bin] += a * a;
        if let Some(spot_b) = self.spot_b {
            let b = self.result[spot_b] as i64;
            self.sb[bin] += b;
            self.qb[bin] += b * b;
            self.ab[bin] += a * b;
        }
    }

    fn play_round(&mut self) {
        if self.pos >= self.cut || self.must_shuffle {
            self.shuffle();
        }
        // A shoe counts once a round is dealt from it (not the shuffle that starts each worker).
        if self.fresh_shoe {
            self.shoes += 1;
            self.fresh_shoe = false;
        }
        self.round_start = self.pos;
        self.hole_hidden = false;
        self.hole_v = -1;
        let bet_bin = self.bin();
        let spots = self.spots;
        for s in 0..spots {
            self.p1[s] = self.draw();
        }
        let up = self.draw();
        for s in 0..spots {
            self.p2[s] = self.draw();
        }
        let mut hole = 0;
        if self.peek {
            self.hole_v = self.draw_hidden();
            hole = self.hole_v & 15;
            self.hole_hidden = true;
        }

        for s in 0..spots {
            self.insured[s] = false;
            self.n_hands[s] = 0;
            let (c1, c2) = (self.p1[s], self.p2[s]);
            self.status[s] = if (c1 == 1 && c2 == 10) || (c1 == 10 && c2 == 1) { 2 } else { 0 };
        }
        if self.es_peek {
            for s in 0..spots {
                if self.status[s] == 0 && self.wants_surrender(self.p1[s], self.p2[s], up) {
                    self.status[s] = 1;
                }
            }
        }
        if up == 1 && self.ins_on && self.insure[self.bin()] {
            for s in 0..spots {
                if self.status[s] != 1 {
                    self.insured[s] = true;
                }
            }
        }

        let mut dealer_bj = false;
        if self.peek {
            dealer_bj = (up == 1 && hole == 10) || (up == 10 && hole == 1);
            if dealer_bj {
                self.rc += (self.hole_v >> 4) - TAG_BIAS;
                self.hole_hidden = false;
                for s in 0..spots {
                    let mut res = match self.status[s] {
                        1 => -10,
                        2 => 0,
                        _ => -20,
                    };
                    if self.insured[s] {
                        res += 20;
                    }
                    self.result[s] = res;
                }
                self.record(bet_bin);
                return;
            }
        }

        let mut any_live = false;
        for s in 0..spots {
            if self.status[s] != 0 {
                continue;
            }
            if self.surr_at_turn && self.wants_surrender(self.p1[s], self.p2[s], up) {
                self.status[s] = 1;
                continue;
            }
            self.play_spot(s, up);
            if !any_live {
                let b0 = s * MAXH;
                any_live = self.h_total[b0..b0 + self.n_hands[s]].iter().any(|&t| t <= 21);
            }
        }

        if self.peek {
            self.rc += (self.hole_v >> 4) - TAG_BIAS;
            self.hole_hidden = false;
        } else {
            hole = self.draw();
            dealer_bj = (up == 1 && hole == 10) || (up == 10 && hole == 1);
        }
        let mut dh = up + hole;
        let mut dace = up == 1 || hole == 1;
        let mut d_final = 0;
        if !dealer_bj {
            if any_live {
                loop {
                    let best = if dace && dh <= 11 { dh + 10 } else { dh };
                    if best > 17 || (best == 17 && !(self.h17 && dace && dh == 7)) {
                        break;
                    }
                    let c = self.draw();
                    dh += c;
                    if c == 1 {
                        dace = true;
                    }
                }
            }
            let bst = if dace && dh <= 11 { dh + 10 } else { dh };
            d_final = if bst > 21 { 0 } else { bst };
        }

        for s in 0..spots {
            let b0 = s * MAXH;
            let hands = b0..b0 + self.n_hands[s];
            let mut res = match self.status[s] {
                1 => {
                    if dealer_bj && self.late_surr {
                        -20
                    } else {
                        -10
                    }
                }
                2 => {
                    if dealer_bj {
                        0
                    } else {
                        self.bj20
                    }
                }
                _ if dealer_bj => {
                    if self.takes_all {
                        -20 * self.h_bet[hands].iter().sum::<i32>()
                    } else {
                        -20
                    }
                }
                _ => {
                    let mut res = 0;
                    for h in hands {
                        let t = self.h_total[h];
                        let bet = self.h_bet[h] * 20;
                        if t > 21 {
                            res -= bet;
                        } else if d_final == 0 || t > d_final {
                            res += bet;
                        } else if t < d_final {
                            res -= bet;
                        }
                    }
                    res
                }
            };
            if self.insured[s] {
                res += if dealer_bj { 20 } else { -10 };
            }
            self.result[s] = res;
        }
        self.record(bet_bin);
    }
}
