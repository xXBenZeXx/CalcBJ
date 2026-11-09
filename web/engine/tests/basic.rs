//! Basic strategy with exact card removal (`CaEngine::basic_strategy`): plays that the analysis's fixed draw odds got
//! wrong in one- and two-deck games, checked against an independent exact calculation and the published charts, and
//! 16 v T with surrender, whose fallback for hands of three or more cards is stand. The C# engine has the same checks
//! (tools/EngineTest.cs, mode "ca").

use calcbj_engine::strategy::{self, action_code, hard_cell, pair_cell, soft_cell};
use calcbj_engine::{CountSystem, IndexMode, Rules, StrategyConfig, SurrenderRule, house_edge_off_top};

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
