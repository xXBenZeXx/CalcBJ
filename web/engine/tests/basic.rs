//! Basic strategy with exact card removal (`CaEngine::basic_strategy`): plays that the analysis's fixed draw odds got
//! wrong in one- and two-deck games, checked against an independent exact calculation and the published charts;
//! 16 v T with surrender, whose fallback for hands of three or more cards is stand; and that no single change of any
//! decision does better.

use calcbj_engine::strategy::{self, CELLS_PER_BIN, action_code, hard_cell, pair_cell, soft_cell};
use calcbj_engine::{
    CaEngine, CountSystem, DoubleRule, HoleCardRule, IndexMode, Rules, StrategyConfig, SurrenderRule, basic_for, flag,
    house_edge_off_top,
};
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

fn rules(decks: i32, h17: bool, das: bool, surrender: SurrenderRule, max_split_hands: i32) -> Rules {
    Rules { decks, h17, das, surrender, max_split_hands, ..Rules::default() }
}

fn code(r: &Rules, kind: char, total: usize, up: usize) -> &'static str {
    let sc = StrategyConfig { mode: IndexMode::BasicOnly, ..StrategyConfig::default() };
    let pt = strategy::build(r, &CountSystem::default(), &sc);
    let cell = match kind {
        'P' => pair_cell(total, up),
        'S' => soft_cell(total, up),
        _ => hard_cell(total, up),
    };
    action_code(pt.basic()[cell], kind == 'P')
}

#[test]
fn exact_plays_in_one_and_two_decks() {
    use SurrenderRule::{Late, None};
    let cases = [
        (rules(2, false, true, None, 4), 'H', 11, 1, "D"),
        (rules(1, false, true, None, 4), 'S', 13, 4, "D"),
        (rules(1, false, true, None, 4), 'S', 19, 6, "Ds"),
        (rules(1, false, true, Late, 4), 'H', 16, 1, "R"),
        (rules(2, true, true, None, 4), 'S', 18, 2, "Ds"),
        (rules(2, false, true, None, 4), 'P', 6, 7, "P"),
        (rules(2, false, true, None, 4), 'P', 7, 8, "P"),
        (rules(2, true, true, None, 4), 'P', 6, 7, "P"),
        (rules(2, true, true, None, 4), 'P', 7, 8, "P"),
        (rules(1, true, true, None, 4), 'P', 9, 1, "P"),
        (rules(1, true, false, None, 2), 'P', 2, 3, "P"),
        (rules(1, false, false, None, 2), 'P', 2, 3, "P"),
    ];
    for (r, kind, total, up, want) in cases {
        assert_eq!(code(&r, kind, total, up), want, "{} {kind}{total} v {up}", r.describe());
    }
}

#[test]
fn sixteen_v_ten_with_surrender_stands_on_three_cards() {
    for (r, want) in [
        (rules(1, true, true, SurrenderRule::Late, 4), "Rs"),
        (rules(6, true, true, SurrenderRule::Late, 4), "Rs"),
        (rules(6, false, true, SurrenderRule::Early, 4), "Rs"),
        (rules(6, true, true, SurrenderRule::None, 4), "H"),
        (rules(1, true, true, SurrenderRule::None, 4), "H"),
    ] {
        assert_eq!(code(&r, 'H', 16, 10), want, "{}", r.describe());
    }
    // The better fallback turns this one-deck game into a (small) player edge.
    assert!(house_edge_off_top(&rules(1, true, true, SurrenderRule::Late, 4)) > 0.0);
}

/// Basic strategy must be the best there is. Every decision of every chart cell (stand or hit, double, surrender,
/// split) is changed in turn and the changed strategy's exact off-the-top expectation worked out: none may gain more
/// than 0.0001%. A wrong cell fails this even when it costs 0.001%, which the published house edges and the
/// simulations can't see. Two known leftovers stay below the limit (found in 77 of 600 rule sets, nothing else in any
/// of them): with two decks and DAS, doubling soft 17 v 2 gains up to 0.00006%, because the hands made by splitting
/// sixes use that cell too; with three decks and surrender, hitting 16 v 9 gains 0.00007%, because the cell's average
/// still counts the pair of eights, which is split.
#[test]
fn no_single_change_improves_basic_strategy() {
    use SurrenderRule::{Early, Late, None};
    let games = [
        ("6D H17 DAS LS", rules(6, true, true, Late, 4)),
        ("6D S17 DAS noSurr", rules(6, false, true, None, 4)),
        ("2D H17 DAS noSurr", rules(2, true, true, None, 4)),
        ("2D S17 DAS noSurr", rules(2, false, true, None, 4)),
        ("1D S17 noDAS SP2", rules(1, false, false, None, 2)),
        ("1D H17 DAS LS", rules(1, true, true, Late, 4)),
        ("1D S17 DAS LS", rules(1, false, true, Late, 4)),
        ("8D S17 DAS ENHC", Rules { hole_card: HoleCardRule::Enhc, ..rules(8, false, true, None, 4) }),
        (
            "6D S17 DAS ES RSA HSA SP3",
            Rules { resplit_aces: true, hit_split_aces: true, ..rules(6, false, true, Early, 3) },
        ),
        (
            "2D S17 noDAS D10 6:5",
            Rules { double_on: DoubleRule::TenToEleven, blackjack_pays: 1.2, ..rules(2, false, false, None, 4) },
        ),
        ("4D H17 DAS LS OBO", Rules { hole_card: HoleCardRule::EnhcObo, ..rules(4, true, true, Late, 4) }),
        ("3D S17 DAS no splitting", rules(3, false, true, None, 1)),
    ];
    let failures: Vec<String> = games.iter().filter_map(|(label, r)| best_play(label, r).err()).collect();
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

/// Changes each decision of basic strategy in turn (on all CPU threads) and checks that none gains.
fn best_play(label: &str, r: &Rules) -> Result<(), String> {
    const BITS: [u8; 4] = [flag::STAND, flag::DOUBLE, flag::SURRENDER, flag::SPLIT];
    const NAMES: [&str; 4] = ["stand/hit", "double", "surrender", "split"];
    let start = std::time::Instant::now();
    let (basic, _, off_top) = basic_for(r);
    let jobs = CELLS_PER_BIN * 4;
    let gains = Mutex::new(vec![0.0; jobs]);
    let next = AtomicUsize::new(0);
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get());
    std::thread::scope(|scope| {
        for _ in 0..threads {
            scope.spawn(|| {
                let mut eng = CaEngine::new(r);
                let mut alt = basic.clone();
                loop {
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    if i >= jobs {
                        break;
                    }
                    let (cell, bit) = (i / 4, BITS[i % 4]);
                    // The split bit belongs to the pair cells, and only where the rules let the player split.
                    if bit == flag::SPLIT && (cell < pair_cell(1, 1) || r.max_split_hands < 2) {
                        continue;
                    }
                    alt.flags[cell] ^= bit;
                    let gain = eng.exact_off_top(&alt) - off_top;
                    alt.flags[cell] ^= bit;
                    gains.lock().expect("gains")[i] = gain;
                }
            });
        }
    });
    let gains = gains.into_inner().expect("gains");
    let changed = gains.iter().filter(|&&g| g != 0.0).count();
    let mut at = 0;
    for (i, &g) in gains.iter().enumerate() {
        if g > gains[at] {
            at = i;
        }
    }
    let (c, up) = (at / 4, at / 4 % 10 + 1);
    let hand = if c >= 440 {
        format!("pair of {}", (c - 440) / 10 + 1)
    } else if c >= 220 {
        format!("soft {}", (c - 220) / 10)
    } else {
        format!("hard {}", c / 10)
    };
    let place = format!("{hand} v {up}, {}", NAMES[at % 4]);
    println!(
        "{label:<28} player EV {:8.4}%   {changed} changed decisions tried, best gain {:.7}%{}   ({} ms)",
        off_top * 100.0,
        gains[at].max(0.0) * 100.0,
        if gains[at] > 0.0 { format!(" ({place})") } else { String::new() },
        start.elapsed().as_millis()
    );
    if changed <= 300 {
        return Err(format!("{label}: only {changed} changed decisions had an effect"));
    }
    if gains[at] > 1e-6 {
        return Err(format!("{label}: {place} gains {:.6}%", gains[at] * 100.0));
    }
    Ok(())
}
