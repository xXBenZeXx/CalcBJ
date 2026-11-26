//! The composition of the unseen cards at a count (`ca::regress`, which the index generation uses): how much each
//! rank's expected number of unseen cards moves per unit of their tag sum must match random draws of unseen cards,
//! for counts with separate red and black tags too.

use calcbj_engine::CountSystem;
use calcbj_engine::ca::{CountModel, regress};

#[test]
fn composition_at_a_count_matches_random_draws() {
    let presets = CountSystem::presets();
    let colours = CountSystem {
        name: "Hi-Lo with red 7 +1 / black 7 -1, red 9 -1 / black 9 +1".to_string(),
        tags: [-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, -1.0, 0.0, 1.0, -1.0],
        red_tags: Some([-1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, -1.0, -1.0]),
        ..presets[0].clone()
    };
    let mut failures = Vec::new();
    for cs in [&presets[0], &presets[5], &presets[11], &colours] {
        for decks in [1, 6] {
            let worst = regress_check(cs, decks, if decks == 1 { 2_000_000 } else { 400_000 });
            if worst > 4.0 {
                failures.push(format!(
                    "{} {decks}D: the count model's composition differs from random draws by {worst:.1} SE",
                    cs.name
                ));
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

/// Draws random sets of unseen cards from a shoe without a ten-ten-six deal and compares the least-squares slope of
/// each rank's count on the set's tag sum with the slope of the count model. Tens and sixes count the same in red and
/// black in every tested count, so the colours of the population stay even, as the model assumes. Returns the largest
/// difference in standard errors of the sampled slope.
fn regress_check(cs: &CountSystem, decks: i32, samples: usize) -> f64 {
    // Card codes: rank index * 2 + red; codes 18 and 19 are the tens.
    let mut left = [0usize; 20];
    for (code, n) in left.iter_mut().enumerate() {
        *n = decks as usize * if code >= 18 { 8 } else { 2 };
    }
    left[18] -= 1; // upcard ten
    left[19] -= 1; // player's ten
    left[10] -= 1; // player's six
    let mut deck: Vec<usize> = (0..20).flat_map(|code| std::iter::repeat_n(code, left[code])).collect();
    let (m, n) = (deck.len(), deck.len() / 2);
    let mut popl = [0.0; 11];
    let mut tag_sum = 0.0;
    for &code in &deck {
        popl[(code >> 1) + 1] += 1.0;
        tag_sum += cs.tag_of_code(code);
    }

    let mut model = CountModel {
        tags: std::array::from_fn(|r| if r == 0 { 0.0 } else { cs.rank_tag(r) }),
        tag_var: cs.red_tags.map(|red| {
            std::array::from_fn(|r| {
                if r == 0 {
                    return 0.0;
                }
                let h = 0.5 * (red[r - 1] - cs.tags[r - 1]);
                h * h
            })
        }),
        unseen_tags: 0.0,
        unseen: n as f64,
        table_weight: None,
        table_min: 0,
        table_step: 1,
        code_tags: [0; 20],
    };
    let s0 = tag_sum * n as f64 / m as f64;
    let slope = model_slope(&popl, &mut model, s0);
    // The earlier model: the variance of the ranks' average tags only.
    let old_slope = model.tag_var.take().map(|tv| {
        let s = model_slope(&popl, &mut model, s0);
        model.tag_var = Some(tv);
        s
    });

    // Sampled slope of each rank's count on the tag sum, around the population means.
    let (mut sx, mut sxx) = (0.0, 0.0);
    let (mut sy, mut sxy, mut syy) = ([0.0; 11], [0.0; 11], [0.0; 11]);
    let mut cnt = [0usize; 11];
    let mut state: u64 = 0x2545_F491_4F6C_DD1D ^ (decks as u64 * 131 + cs.name.chars().count() as u64);
    for _ in 0..samples {
        cnt.fill(0);
        let mut s = 0.0;
        for k in 0..n {
            state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
            let mut z = state;
            z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
            z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
            z ^= z >> 31;
            let j = k + (((z >> 32) * (m - k) as u64) >> 32) as usize;
            deck.swap(j, k);
            let c = deck[k];
            s += cs.tag_of_code(c);
            cnt[(c >> 1) + 1] += 1;
        }
        let x = s - s0;
        sx += x;
        sxx += x * x;
        for r in 1..=10 {
            let y = cnt[r] as f64 - popl[r] * n as f64 / m as f64;
            sy[r] += y;
            sxy[r] += x * y;
            syy[r] += y * y;
        }
    }
    let samples = samples as f64;
    let cxx = sxx - sx * sx / samples;
    let (mut worst, mut worst_old) = (0.0f64, 0.0f64);
    for r in 1..=10 {
        let cxy = sxy[r] - sx * sy[r] / samples;
        let cyy = syy[r] - sy[r] * sy[r] / samples;
        let b = cxy / cxx;
        let se = ((cyy - b * cxy) / (samples - 2.0) / cxx).max(1e-30).sqrt();
        worst = worst.max((slope[r] - b).abs() / se);
        if let Some(old) = &old_slope {
            worst_old = worst_old.max((old[r] - b).abs() / se);
        }
    }
    println!(
        "{:<58} {decks}D: largest slope difference {worst:4.1} SE{}",
        cs.name,
        if old_slope.is_some() { format!(" (without the red/black spread {worst_old:.1} SE)") } else { String::new() }
    );
    worst
}

/// The model's change in each rank's unseen cards per unit of their tag sum, around `s0`.
fn model_slope(popl: &[f64; 11], model: &mut CountModel, s0: f64) -> [f64; 11] {
    let (mut lo, mut hi) = ([0.0; 11], [0.0; 11]);
    model.unseen_tags = s0 - 0.5;
    regress(popl, model, &mut lo);
    model.unseen_tags = s0 + 0.5;
    regress(popl, model, &mut hi);
    std::array::from_fn(|r| if r == 0 { 0.0 } else { hi[r] - lo[r] })
}
