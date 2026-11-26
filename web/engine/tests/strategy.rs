//! Strategy tables (basic, I18, Fab 4, full indices, overrides) and index descriptions against the fixtures
//! (fixtures/strategy.json).

mod common;

use calcbj_engine::strategy::{self, CELLS_PER_BIN, NUM_BINS};
use common::*;

#[test]
fn strategy_tables() {
    let fx = fixture("strategy.json");
    let mut c = Check::new("strategy tables");
    for case in list(&fx["cases"]) {
        let label = text(&case["label"]);
        let (r, cs, sc) = (rules(&case["rules"]), count(&case["count"]), config(&case["config"]));
        let pt = strategy::build(&r, &cs, &sc);
        c.eq(&format!("{label}: key"), pt.key.clone(), text(&case["key"]).to_string());

        // The fixture lists each bin whose flags differ from the bin before it.
        let mut want = vec![0u8; NUM_BINS * CELLS_PER_BIN];
        let mut prev: Vec<u8> = Vec::new();
        let changes = list(&case["flagsBins"]);
        let mut next = 0;
        for b in 0..NUM_BINS {
            if next < changes.len() && int(&changes[next][0]) as usize == b {
                prev = hex(text(&changes[next][1]));
                next += 1;
            }
            want[b * CELLS_PER_BIN..(b + 1) * CELLS_PER_BIN].copy_from_slice(&prev);
        }
        c.eq(&format!("{label}: flags hash"), fnv_bytes(&pt.flags), text(&case["flagsHash"]).to_string());
        c.eq(&format!("{label}: rebuilt fixture hash"), fnv_bytes(&want), text(&case["flagsHash"]).to_string());
        for (i, (&g, &w)) in pt.flags.iter().zip(&want).enumerate() {
            if g != w {
                let bin = i / CELLS_PER_BIN;
                c.eq(&format!("{label}: count {} {}", bin as i32 - 100, cell_name(i % CELLS_PER_BIN)), g, w);
            }
        }
        let insure: String = pt.insure.iter().map(|&x| if x { '1' } else { '0' }).collect();
        c.eq(&format!("{label}: insure"), insure, text(&case["insure"]).to_string());
        c.nums(&format!("{label}: bin TC equivalent"), &pt.bin_tc_eq, &nums(&case["binTcEq"]), 0.0);
        c.num(&format!("{label}: basic EV"), pt.basic_result.player_ev, num(&case["basicEV"]), 1e-14);
        c.eq(&format!("{label}: basic flags"), pt.basic().to_vec(), hex(text(&case["basicFlags"])));
        c.eq(&format!("{label}: per-bin min"), pt.per_bin_min as i64, int(&case["perBinMin"]));
        match (&pt.per_bin, case["perBin"].as_array()) {
            (None, None) => {}
            (Some(got), Some(want)) => {
                c.eq(&format!("{label}: per-bin results"), got.len(), want.len());
                for (i, (g, w)) in got.iter().zip(want).enumerate() {
                    let what = format!("{label}: per-bin {i}");
                    c.num(&format!("{what} EV"), g.player_ev, num(&w["playerEV"]), 1e-14);
                    c.num(&format!("{what} insurance EV"), g.insurance_ev, num(&w["insuranceEV"]), 1e-14);
                    c.eq(&format!("{what} insure"), g.insure, flag(&w["insure"]));
                    c.eq(&format!("{what} flags"), g.flags.clone(), hex(text(&w["flags"])));
                    let sum: f64 = g.cell_ev.iter().filter(|v| !v.is_nan()).sum();
                    c.num(&format!("{what} cellEV sum"), sum, num(&w["cellEVSum"]), 1e-12);
                }
            }
            _ => c.fail(format!("{label}: per-bin results present on one side only")),
        }
        c.eq(&format!("{label}: fixed cells"), calcbj_engine::sim::fixed_cells(&pt), hex(text(&case["fixedCells"])));

        let range = ints(&case["describeRange"]);
        let got = strategy::describe_indices(&pt, range[0] as i32, range[1] as i32);
        let want = list(&case["indices"]);
        c.eq(&format!("{label}: index count"), got.len(), want.len());
        for (g, w) in got.iter().zip(want) {
            let what = format!("{label}: index {}", g.hand);
            c.eq(&format!("{what} kind"), g.kind.name(), text(&w["kind"]));
            c.eq(
                &format!("{what} total/upcard"),
                (g.total as i64, g.upcard as i64),
                (int(&w["total"]), int(&w["upcard"])),
            );
            c.eq(&format!("{what} hand"), g.hand.as_str(), text(&w["hand"]));
            c.eq(&format!("{what} text"), g.text(), text(&w["text"]).to_string());
            c.num(&format!("{what} threshold"), g.threshold, num(&w["threshold"]), 0.0);
            c.eq(&format!("{what} at or above"), g.at_or_above, flag(&w["atOrAbove"]));
            c.eq(&format!("{what} irregular"), g.irregular, flag(&w["irregular"]));
        }
        let codes: Vec<&str> =
            (0..CELLS_PER_BIN).map(|cell| strategy::action_code(pt.basic()[cell], cell >= 440)).collect();
        c.eq(&format!("{label}: chart codes"), codes.join(","), text(&case["actionCodes"]).to_string());
        for (o, w) in sc.overrides.iter().zip(list(&case["config"]["overrides"])) {
            c.eq(&format!("{label}: override key"), o.key(), text(&w["key"]).to_string());
        }
    }
    c.finish();
}
