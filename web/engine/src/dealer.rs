//! Exact dealer outcome probabilities with card removal (C#: DealerDag.cs).

use std::collections::HashMap;
use std::sync::OnceLock;

/// Outcome codes: 0..=4 = final 17..21.
pub(crate) const BUST: i8 = 5;
pub(crate) const BLACKJACK: i8 = 6;

/// All multisets of cards the dealer can draw behind a given upcard, in breadth-first order.
/// Because the stopping rule and the draw probabilities depend only on which cards are out
/// (not their order), evaluating this DAG gives the exact dealer distribution with card removal.
pub(crate) struct DealerDag {
    pub count: usize,
    /// count * 10: number of each rank drawn (index rank - 1).
    m: Vec<u8>,
    /// Cards drawn (hole card included).
    k: Vec<i32>,
    /// -1 = draws again; 0..=4 = 17..21; 5 = bust; 6 = blackjack.
    outcome: Vec<i8>,
    /// count * 10.
    child: Vec<i32>,
}

static CACHE: [OnceLock<Vec<DealerDag>>; 2] = [OnceLock::new(), OnceLock::new()];

/// The DAG for upcard `up` (1..=10), built once per process.
pub(crate) fn get(up: usize, h17: bool) -> &'static DealerDag {
    let all = CACHE[h17 as usize].get_or_init(|| (1..=10).map(|u| DealerDag::build(u, h17)).collect());
    &all[up - 1]
}

impl DealerDag {
    fn build(up: usize, h17: bool) -> DealerDag {
        let mut index: HashMap<u64, usize> = HashMap::new();
        let mut keys: Vec<u64> = vec![0];
        let mut counts: Vec<[u8; 10]> = vec![[0; 10]];
        index.insert(0, 0);
        let mut outcome: Vec<i8> = Vec::new();
        let mut child: Vec<i32> = Vec::new();
        let up_i = up as i32;

        let mut i = 0;
        while i < keys.len() {
            let m = counts[i];
            let (mut k, mut h) = (0i32, up_i);
            for (r, &c) in m.iter().enumerate() {
                k += c as i32;
                h += c as i32 * (r as i32 + 1);
            }
            let ace = up == 1 || m[0] > 0;
            let mut o: i8 = -1;
            if k >= 1 {
                if k == 1 && ((up == 1 && m[9] == 1) || (up == 10 && m[0] == 1)) {
                    o = BLACKJACK;
                } else if h > 21 {
                    o = BUST;
                } else {
                    let best = if ace && h + 10 <= 21 { h + 10 } else { h };
                    let hit_soft17 = h17 && best == 17 && ace && h + 10 == 17;
                    if best >= 17 && !hit_soft17 {
                        o = (best - 17) as i8;
                    }
                }
            }
            outcome.push(o);
            for r in 0..10 {
                if o >= 0 {
                    child.push(-1);
                    continue;
                }
                let ck = keys[i] + (1u64 << (4 * r));
                let ci = match index.get(&ck) {
                    Some(&c) => c,
                    None => {
                        let c = keys.len();
                        index.insert(ck, c);
                        keys.push(ck);
                        let mut cm = m;
                        cm[r] += 1;
                        counts.push(cm);
                        c
                    }
                };
                child.push(ci as i32);
            }
            i += 1;
        }

        let count = keys.len();
        let mut mm = Vec::with_capacity(count * 10);
        let mut kk = Vec::with_capacity(count);
        for c in &counts {
            mm.extend_from_slice(c);
            kk.push(c.iter().map(|&x| x as i32).sum());
        }
        DealerDag { count, m: mm, k: kk, outcome, child }
    }

    /// Dealer final-total distribution. `comp[1..=10]` = cards of each rank still available to the dealer, `n` = their
    /// sum. `dist[0..=4]` = P(17..21), `dist[5]` = P(bust), `dist[6]` = P(blackjack). `mass` needs `count` entries.
    pub fn evaluate(&self, comp: &[f64; 11], n: f64, mass: &mut [f64], dist: &mut [f64; 7]) {
        let mass = &mut mass[..self.count];
        mass.fill(0.0);
        *dist = [0.0; 7];
        mass[0] = 1.0;
        for s in 0..self.count {
            let ms = mass[s];
            if ms == 0.0 {
                continue;
            }
            let o = self.outcome[s];
            if o >= 0 {
                dist[o as usize] += ms;
                continue;
            }
            let denom = n - self.k[s] as f64;
            if denom <= 1e-12 {
                continue;
            }
            let f = ms / denom;
            let b = s * 10;
            let mut lack = 0.0;
            for r in 0..10 {
                let avail = comp[r + 1] - self.m[b + r] as f64;
                if avail > 1e-12 {
                    mass[self.child[b + r] as usize] += f * avail;
                } else if avail < 0.0 {
                    lack -= avail;
                }
            }
            if lack > 0.0 {
                // A composition with fractions of cards (the count model's) can hold less of a rank than the dealer has
                // drawn. What it lacks isn't among the cards left: share this state's mass over denom + lack cards, so
                // the outcomes still add up to 1. Never happens with whole cards.
                let g = ms / (denom + lack) - f;
                for r in 0..10 {
                    let avail = comp[r + 1] - self.m[b + r] as f64;
                    if avail > 1e-12 {
                        mass[self.child[b + r] as usize] += g * avail;
                    }
                }
            }
        }
    }
}

/// Dealer outcome probabilities behind upcard `up` for the cards still available to the dealer (`comp[1..=10]`):
/// P(17), P(18), P(19), P(20), P(21), P(bust), P(blackjack).
pub fn dealer_outcomes(up: usize, h17: bool, comp: &[f64; 11]) -> [f64; 7] {
    let dag = get(up, h17);
    let mut n = 0.0;
    for &c in &comp[1..=10] {
        n += c;
    }
    let mut mass = vec![0.0; dag.count];
    let mut dist = [0.0; 7];
    dag.evaluate(comp, n, &mut mass, &mut dist);
    dist
}

/// Number of distinct dealer draw states behind an upcard.
pub fn dealer_states(up: usize, h17: bool) -> usize {
    get(up, h17).count
}
