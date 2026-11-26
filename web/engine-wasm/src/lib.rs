//! WebAssembly interface of the CalcBJ engine for the web app (web/app).
//!
//! Requests are JSON: JavaScript copies `{"op": "…", …}` into memory from [`alloc`], calls [`call`] and reads the
//! reply, `{"ok": …}` or `{"error": "…"}`, of the returned length at [`out_ptr`]. A simulation worker sends one
//! `simPrepare` request and then calls [`sim_run`], which hands back its tallies as doubles (no JSON on that path).

mod app;

use calcbj_engine::*;
use serde::Deserialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::alloc::Layout;
use std::cell::RefCell;

/// Doubles returned by [`sim_run`]: hands, sum A, squares A, sum B, squares B and products per count bin
/// ([`strategy::NUM_BINS`] each), then rounds, shoes and reshuffle fallbacks.
pub const TALLY_LEN: usize = 6 * strategy::NUM_BINS + 3;

thread_local! {
    static OUT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    static TABLE: RefCell<Option<PlayTable>> = const { RefCell::new(None) };
    static WORKER: RefCell<Option<SimWorker>> = const { RefCell::new(None) };
    static TALLY: RefCell<Vec<f64>> = const { RefCell::new(Vec::new()) };
}

fn layout(len: usize) -> Layout {
    Layout::from_size_align(len.max(1), 1).expect("request layout")
}

/// Reserves `len` bytes for a request.
#[unsafe(no_mangle)]
pub extern "C" fn alloc(len: usize) -> *mut u8 {
    unsafe { std::alloc::alloc(layout(len)) }
}

/// Frees memory from [`alloc`].
///
/// # Safety
/// `ptr` must come from `alloc(len)` and must not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dealloc(ptr: *mut u8, len: usize) {
    unsafe { std::alloc::dealloc(ptr, layout(len)) }
}

/// Handles one JSON request and returns the length of the reply, which [`out_ptr`] points to until the next call.
///
/// # Safety
/// `ptr` must point to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn call(ptr: *const u8, len: usize) -> usize {
    let input = unsafe { std::slice::from_raw_parts(ptr, len) };
    let reply = match serde_json::from_slice::<Value>(input).map_err(|e| e.to_string()).and_then(|req| handle(&req)) {
        Ok(v) => json!({ "ok": v }),
        Err(e) => json!({ "error": e }),
    };
    OUT.with_borrow_mut(|out| {
        *out = serde_json::to_vec(&reply).unwrap_or_else(|_| br#"{"error":"the reply could not be encoded"}"#.to_vec());
        out.len()
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn out_ptr() -> *const u8 {
    OUT.with_borrow(|out| out.as_ptr())
}

/// Plays `rounds` more rounds on the worker set up by `simPrepare`, clears its tallies and returns a pointer to them
/// ([`TALLY_LEN`] doubles, valid until the next call); null when no worker is prepared.
#[unsafe(no_mangle)]
pub extern "C" fn sim_run(rounds: u32) -> *const f64 {
    WORKER.with_borrow_mut(|worker| {
        let Some(w) = worker.as_mut() else {
            return std::ptr::null();
        };
        w.run(rounds as i64);
        TALLY.with_borrow_mut(|t| {
            t.clear();
            for v in [&w.n, &w.sa, &w.qa, &w.sb, &w.qb, &w.ab] {
                t.extend(v.iter().map(|&x| x as f64));
            }
            t.extend([w.rounds as f64, w.shoes as f64, w.emergency as f64]);
            w.reset();
            t.as_ptr()
        })
    })
}

fn arg<T: DeserializeOwned>(req: &Value, name: &str) -> Result<T, String> {
    let v = req.get(name).ok_or_else(|| format!("missing \"{name}\""))?;
    T::deserialize(v).map_err(|e| format!("{name}: {e}"))
}

fn arg_opt<T: DeserializeOwned>(req: &Value, name: &str) -> Result<Option<T>, String> {
    match req.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(v) => T::deserialize(v).map(Some).map_err(|e| format!("{name}: {e}")),
    }
}

fn rules(req: &Value) -> Result<Rules, String> {
    let r: Rules = arg(req, "rules")?;
    if !(1..=8).contains(&r.decks) {
        return Err("the number of decks must be 1 to 8".into());
    }
    // 100% means the cut card sits as deep as the reserve for the round in progress allows, as in the Windows app.
    if !(0.0..=1.0).contains(&r.penetration) {
        return Err("penetration must be 0 to 100%".into());
    }
    if !(0.0..=10.0).contains(&r.blackjack_pays) {
        return Err("the blackjack payout must be between 0 and 10".into());
    }
    // The simulator pays in twentieths of a unit; the app snaps payouts from files to them (sanitizeRules).
    if ((r.blackjack_pays * 20.0).round() - r.blackjack_pays * 20.0).abs() > 1e-9 {
        return Err("the blackjack payout must be a multiple of 0.05".into());
    }
    if !(1..=calcbj_engine::types::MAX_SPLIT_LIMIT).contains(&r.max_split_hands) {
        return Err("split hands must be 1 to 4".into());
    }
    Ok(r)
}

fn count(req: &Value) -> Result<CountSystem, String> {
    let c: CountSystem = arg(req, "count")?;
    let ok = |t: &[f64; 10]| t.iter().all(|v| v.is_finite() && v.abs() <= 20.0);
    if !ok(&c.tags) || !c.red_tags.as_ref().is_none_or(ok) {
        return Err("tags must be between -20 and 20".into());
    }
    if !(c.irc_base.abs() <= 10_000.0 && c.irc_per_deck.abs() <= 1_000.0) {
        return Err("the initial running count is out of range".into());
    }
    Ok(c)
}

fn config(req: &Value) -> Result<StrategyConfig, String> {
    let sc: StrategyConfig = arg(req, "config")?;
    if sc.overrides.iter().any(|o| !o.index.is_finite()) {
        return Err("index values must be numbers".into());
    }
    Ok(sc)
}

fn spots(req: &Value) -> Result<i32, String> {
    let s: i32 = arg(req, "spots")?;
    if !(1..=7).contains(&s) {
        return Err("spots must be 1 to 7".into());
    }
    Ok(s)
}

fn ramp(req: &Value) -> Result<BetRamp, String> {
    let r: BetRamp = arg(req, "ramp")?;
    if r.max < r.min || r.units.len() != r.rows() || r.hands.len() != r.rows() {
        return Err("the bet ramp rows do not match its count range".into());
    }
    if r.units.iter().any(|u| !(u.is_finite() && *u >= 0.0)) {
        return Err("bets must be zero or more".into());
    }
    Ok(r)
}

/// Simulation results; the tallies may arrive as whole-number doubles (JavaScript keeps them in Float64Arrays).
fn sim(req: &Value) -> Result<SimData, String> {
    let mut v = req.get("sim").cloned().ok_or("missing \"sim\"")?;
    let whole = |x: &mut Value| {
        if let Some(f) = x.as_f64()
            && x.is_f64()
            && f.fract() == 0.0
            && f.abs() < 9e15
        {
            *x = json!(f as i64);
        }
    };
    for key in ["n", "sa", "qa", "sb", "qb", "ab"] {
        if let Some(Value::Array(a)) = v.get_mut(key) {
            a.iter_mut().for_each(whole);
        }
    }
    for key in ["rounds", "shoes", "emergency"] {
        if let Some(x) = v.get_mut(key) {
            whole(x);
        }
    }
    let d = SimData::deserialize(&v).map_err(|e| format!("sim: {e}"))?;
    let n = d.num_bins;
    if n > 1000 || [&d.n, &d.sa, &d.qa, &d.sb, &d.qb, &d.ab].iter().any(|v| v.len() != n) {
        return Err("the simulation results are incomplete".into());
    }
    Ok(d)
}

fn to_value<T: serde::Serialize>(v: &T) -> Result<Value, String> {
    serde_json::to_value(v).map_err(|e| e.to_string())
}

/// Runs `f` with the strategy table for these settings, building it only when they changed since the last request.
fn with_table<T>(r: &Rules, cs: &CountSystem, sc: &StrategyConfig, f: impl FnOnce(&PlayTable) -> T) -> T {
    let key = strategy::key_of(r, cs, sc);
    TABLE.with_borrow_mut(|slot| {
        if slot.as_ref().is_none_or(|t| t.key != key) {
            *slot = Some(strategy::build(r, cs, sc));
        }
        f(slot.as_ref().expect("strategy table"))
    })
}

fn handle(req: &Value) -> Result<Value, String> {
    let op = req.get("op").and_then(Value::as_str).ok_or("missing \"op\"")?;
    match op {
        "houseEdge" => Ok(json!(house_edge_off_top(&rules(req)?))),
        "rulesInfo" => {
            let (r, spots) = (rules(req)?, spots(req)?);
            Ok(json!({
                "describe": r.describe(),
                "key": r.key(),
                "totalCards": r.total_cards(),
                "cutCard": r.cut_card(spots),
                "midDepthDecks": strategy::mid_depth_decks(&r),
            }))
        }
        "presets" => to_value(&CountSystem::presets()),
        "countKey" => Ok(json!(count(req)?.key())),
        "countInfo" => {
            let eor: Option<Vec<f64>> = arg_opt(req, "eor")?;
            let eor: Option<[f64; 11]> = match eor {
                Some(e) => Some(e.try_into().map_err(|_| "\"eor\" must have 11 values")?),
                None => None,
            };
            Ok(app::count_info(&rules(req)?, &count(req)?, eor.as_ref()))
        }
        // Ten exact evaluations (about a second): the app asks for them in a worker and passes them to countInfo.
        "effectsOfRemoval" => Ok(json!(effects_of_removal(&rules(req)?))),
        // The settings and the engine's results version: results stored under another version are outdated.
        "simKey" => {
            let key = strategy::key_of(&rules(req)?, &count(req)?, &config(req)?);
            Ok(json!(format!("{key}#spots={}#results={RESULTS_VERSION}", spots(req)?)))
        }
        "strategy" => {
            let (r, cs, sc) = (rules(req)?, count(req)?, config(req)?);
            Ok(with_table(&r, &cs, &sc, |pt| {
                let indices: Vec<Value> = strategy::describe_indices(pt, sc.index_min, sc.index_max)
                    .iter()
                    .map(|i| {
                        let mut v = json!(i);
                        v["text"] = json!(i.text());
                        v
                    })
                    .collect();
                json!({ "key": pt.key, "basicEv": pt.basic_result.player_ev, "indices": indices })
            }))
        }
        "chart" => {
            let (r, cs, sc) = (rules(req)?, count(req)?, config(req)?);
            let at: Option<i32> = arg_opt(req, "at")?;
            Ok(with_table(&r, &cs, &sc, |pt| {
                let codes: Vec<&str> = (0..strategy::CELLS_PER_BIN)
                    .map(|cell| {
                        let f = at.map_or(pt.basic()[cell], |v| pt.flags_at(v, cell));
                        strategy::action_code(f, cell >= 440)
                    })
                    .collect();
                // With basic strategy only there is no analysis per count: every count shows the off-the-top values.
                let basic = at.is_none() || pt.per_bin.is_none();
                let ca = if basic { &pt.basic_result } else { pt.result_at(at.unwrap_or(0)) };
                // After the dealer's peek the values are given no dealer blackjack; otherwise they are from the deal.
                let shown: Vec<f64> = (0..strategy::CELLS_PER_BIN * 5).map(|i| ca.shown_ev(&r, i / 5, i % 5)).collect();
                json!({ "codes": codes, "cellEv": shown, "afterPeek": CaResult::after_peek(&r), "basic": basic })
            }))
        }
        "defaultRamp" => {
            let (r, cs) = (rules(req)?, count(req)?);
            let old: Option<BetRamp> = arg_opt(req, "ramp")?;
            let mut ramp = app::reset_ramp_range(&cs, &r, old.as_ref().filter(|o| o.units.len() == o.rows()));
            let preset = app::apply_preset(&cs, &r, &mut ramp, arg(req, "preset")?);
            Ok(json!({ "ramp": ramp, "preset": preset }))
        }
        "applyPreset" => {
            let (r, cs, mut ramp) = (rules(req)?, count(req)?, ramp(req)?);
            let preset = app::apply_preset(&cs, &r, &mut ramp, arg(req, "preset")?);
            Ok(json!({ "ramp": ramp, "preset": preset }))
        }
        "indexRange" => {
            let (lo, hi) = app::index_range(&count(req)?, &rules(req)?);
            Ok(json!({ "min": lo, "max": hi }))
        }
        "carryRamp" => {
            // The ramp moved from the count and rules it was set for (fromCount, from) to the current ones.
            let (cs, to, ramp) = (count(req)?, rules(req)?, ramp(req)?);
            let from: Rules = arg(req, "from")?;
            let cs_from: CountSystem = arg(req, "fromCount")?;
            let preset: i32 = arg(req, "preset")?;
            let carried = app::ramp_for(&cs_from, &from, &cs, &to, &ramp, preset);
            let custom = !(0..app::CUSTOM_PRESET).contains(&preset) && ramp.rows() >= 1;
            Ok(
                json!({ "ramp": carried, "preset": if custom || (0..app::CUSTOM_PRESET).contains(&preset) { preset } else { 3 } }),
            )
        }
        "fromHiLo" => {
            let values: Vec<f64> = arg(req, "values")?;
            Ok(json!(app::from_hi_lo(&count(req)?, &rules(req)?, &values)))
        }
        "rampForRules" => {
            let (cs, ramp) = (count(req)?, ramp(req)?);
            let from: Rules = arg(req, "from")?;
            let to = rules(req)?;
            Ok(json!(app::ramp_for_rules(&cs, &from, &to, &ramp, arg(req, "preset")?)))
        }
        "bins" => to_value(&analysis::bins(&sim(req)?)),
        "evaluate" => {
            let (d, ramp) = (sim(req)?, ramp(req)?);
            let (unit, rph, bankroll): (f64, f64, f64) = (arg(req, "unit")?, arg(req, "rph")?, arg(req, "bankroll")?);
            let a = analysis::evaluate(&d, &ramp, unit, rph, bankroll);
            let rows = analysis::rows(&analysis::bins(&d), &ramp);
            let bankroll_for = [0.05, 0.135, 0.01].map(|ror| a.bankroll_for_ror(ror));
            Ok(json!({ "result": a, "bankrollFor": bankroll_for, "rows": rows }))
        }
        "optimize" => {
            let (d, ramp) = (sim(req)?, ramp(req)?);
            let os: OptimizerSettings = arg(req, "settings")?;
            to_value(&analysis::optimize(&d, &ramp, &os))
        }
        "phi" => {
            let x: Vec<f64> = arg(req, "x")?;
            Ok(json!(x.iter().map(|&v| analysis::phi(v)).collect::<Vec<_>>()))
        }
        "phiInv" => {
            let p: Vec<f64> = arg(req, "p")?;
            Ok(json!(p.iter().map(|&v| analysis::phi_inv(v)).collect::<Vec<_>>()))
        }
        "tripRuin" => {
            let (mu, var, bankroll, n): (f64, f64, f64, f64) =
                (arg(req, "mu")?, arg(req, "var")?, arg(req, "bankroll")?, arg(req, "n")?);
            Ok(json!(analysis::trip_ruin(mu, var, bankroll, n)))
        }
        "probLoss" => {
            let (mu, var, n): (f64, f64, f64) = (arg(req, "mu")?, arg(req, "var")?, arg(req, "n")?);
            Ok(json!(analysis::prob_loss(mu, var, n)))
        }
        "simPrepare" => {
            let (r, cs, sc, spots) = (rules(req)?, count(req)?, config(req)?, spots(req)?);
            let seed: String = arg(req, "seed")?;
            let seed: u64 = seed.parse().map_err(|_| "the seed must be a whole number".to_string())?;
            let index: i32 = arg(req, "worker")?;
            let (plan, basic_ev) =
                with_table(&r, &cs, &sc, |pt| (SimPlan::new(&r, &cs, pt, spots), pt.basic_result.player_ev));
            let mut w = SimWorker::new(&plan, sim::worker_seed(seed, index));
            w.shuffle();
            WORKER.with_borrow_mut(|slot| *slot = Some(w));
            Ok(json!({ "basicEv": basic_ev, "tallyLength": TALLY_LEN }))
        }
        _ => Err(format!("unknown request \"{op}\"")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ok(req: Value) -> Value {
        handle(&req).unwrap_or_else(|e| panic!("{req}: {e}"))
    }

    fn defaults() -> (Value, Value, Value) {
        (json!(Rules::default()), json!(CountSystem::default()), json!(StrategyConfig::default()))
    }

    #[test]
    fn json_round_trip_and_house_edge() {
        let (r, ..) = defaults();
        assert_eq!(r["holeCard"], "USPeek");
        assert_eq!(r["blackjackPays"], 1.5);
        let ev = ok(json!({ "op": "houseEdge", "rules": r })).as_f64().unwrap();
        assert_eq!(ev, house_edge_off_top(&Rules::default()));
        // Missing fields take the defaults.
        let s17 = ok(json!({ "op": "houseEdge", "rules": { "h17": false } })).as_f64().unwrap();
        assert_eq!(s17, house_edge_off_top(&Rules { h17: false, ..Rules::default() }));
    }

    /// The app tells current results from outdated ones by the version at the end of the key.
    #[test]
    fn sim_key_ends_with_the_results_version() {
        let (r, cs, sc) = defaults();
        let key = ok(json!({ "op": "simKey", "rules": r, "count": cs, "config": sc, "spots": 3 }));
        let key = key.as_str().expect("a key");
        assert!(key.ends_with(&format!("#spots=3#results={RESULTS_VERSION}")), "{key}");
        assert_eq!(key.matches("#results=").count(), 1, "{key}");
    }

    #[test]
    fn bad_requests_are_errors() {
        assert!(handle(&json!({ "op": "nope" })).is_err());
        assert!(handle(&json!({ "op": "houseEdge", "rules": { "decks": 0 } })).is_err());
        assert!(handle(&json!({ "op": "houseEdge", "rules": { "holeCard": "Peek" } })).is_err());
        let (r, cs, _) = defaults();
        let ramp = json!({ "min": 0, "max": 2, "units": [1, 2], "hands": [1, 1] });
        assert!(handle(&json!({ "op": "applyPreset", "rules": r, "count": cs, "ramp": ramp, "preset": 1 })).is_err());
    }

    #[test]
    fn default_ramp_for_hi_lo() {
        let (r, cs, _) = defaults();
        let v = ok(json!({ "op": "defaultRamp", "rules": r, "count": cs, "ramp": null, "preset": 3 }));
        assert_eq!(v["preset"], 3);
        assert_eq!(v["ramp"]["min"], -3);
        assert_eq!(v["ramp"]["max"], 8);
        let units: Vec<f64> = serde_json::from_value(v["ramp"]["units"].clone()).unwrap();
        assert_eq!(units, [1.0, 1.0, 1.0, 1.0, 2.0, 4.0, 6.0, 8.0, 10.0, 12.0, 12.0, 12.0]);
        let range = ok(json!({ "op": "indexRange", "rules": r, "count": cs }));
        assert_eq!((range["min"].as_i64(), range["max"].as_i64()), (Some(-10), Some(10)));
    }

    /// Presets map each row by its typical count: Wong Halves (1 Halves TC = 1.05 Hi-Lo TC) bets like Hi-Lo at the same
    /// true count instead of a row late, in every rounding mode.
    #[test]
    fn presets_for_other_counts() {
        let (r, _, _) = defaults();
        let halves = CountSystem::presets().into_iter().find(|c| c.name == "Wong Halves").unwrap();
        for rounding in ["Floor", "Truncate", "Round"] {
            let mut cs = serde_json::to_value(&halves).unwrap();
            cs["rounding"] = json!(rounding);
            let v = ok(json!({ "op": "defaultRamp", "rules": r, "count": cs, "ramp": null, "preset": 3 }));
            let (min, units): (i64, Vec<f64>) =
                (v["ramp"]["min"].as_i64().unwrap(), serde_json::from_value(v["ramp"]["units"].clone()).unwrap());
            let at = |tc: i64| units[(tc - min) as usize];
            assert_eq!(
                [at(0), at(1), at(2), at(3), at(4), at(5), at(6)],
                [1.0, 2.0, 4.0, 6.0, 8.0, 10.0, 12.0],
                "{rounding}"
            );
        }
    }

    /// A custom ramp keeps its bets at the same true counts when the count changes; a preset is applied again.
    #[test]
    fn ramp_carried_to_another_count() {
        let (r, hi_lo, _) = defaults();
        let ko = json!(CountSystem::presets().into_iter().find(|c| c.name.starts_with("KO")).unwrap());
        let custom = json!({
            "min": -3, "max": 8,
            "units": [1.0, 1.0, 1.0, 1.0, 3.0, 5.0, 7.0, 9.0, 11.0, 13.0, 15.0, 15.0],
            "hands": [1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2],
        });
        let carry = |from: &Value, to: &Value, ramp: &Value, preset: i32| {
            ok(
                json!({ "op": "carryRamp", "from": r, "fromCount": from, "rules": r, "count": to, "ramp": ramp, "preset": preset }),
            )
        };
        // The same count and rules: nothing moves.
        let same = carry(&hi_lo, &hi_lo, &custom, app::CUSTOM_PRESET);
        assert_eq!(same["ramp"], custom);
        assert_eq!(same["preset"], app::CUSTOM_PRESET);
        // To KO's running count (6 decks: about -23 to +21 for Hi-Lo -3 to +8) and back.
        let to_ko = carry(&hi_lo, &ko, &custom, app::CUSTOM_PRESET);
        let units: Vec<f64> = serde_json::from_value(to_ko["ramp"]["units"].clone()).unwrap();
        let hands: Vec<i32> = serde_json::from_value(to_ko["ramp"]["hands"].clone()).unwrap();
        assert!(units.len() > 30 && units.windows(2).all(|w| w[0] <= w[1]), "{units:?}");
        assert_eq!((units[0], *units.last().unwrap()), (1.0, 15.0));
        assert_eq!((hands[0], *hands.last().unwrap()), (1, 2));
        // Every bet of the custom ramp is still there.
        for u in [1.0, 3.0, 5.0, 7.0, 9.0, 11.0, 13.0, 15.0] {
            assert!(units.contains(&u), "{u} missing in {units:?}");
        }
        assert_eq!(carry(&ko, &hi_lo, &to_ko["ramp"], app::CUSTOM_PRESET)["ramp"], custom);
        // A preset is applied again over the new count's rows.
        let preset = carry(&hi_lo, &ko, &custom, 3);
        assert_eq!(preset["preset"], 3);
        assert_eq!(
            preset["ramp"],
            ok(json!({ "op": "defaultRamp", "rules": r, "count": ko, "ramp": null, "preset": 3 }))["ramp"]
        );
        // More decks move a running count's rows: the bets follow.
        let eight = json!(Rules { decks: 8, ..Rules::default() });
        let moved = ok(
            json!({ "op": "carryRamp", "from": r, "fromCount": ko, "rules": eight, "count": ko, "ramp": to_ko["ramp"], "preset": app::CUSTOM_PRESET }),
        );
        assert!(moved["ramp"]["min"].as_i64().unwrap() < to_ko["ramp"]["min"].as_i64().unwrap());
        let units8: Vec<f64> = serde_json::from_value(moved["ramp"]["units"].clone()).unwrap();
        assert_eq!((units8[0], *units8.last().unwrap()), (1.0, 15.0));
    }

    /// Published Hi-Lo index numbers stay as they are for counts that read like Hi-Lo and are converted for the others.
    #[test]
    fn published_indices_in_another_count() {
        let (r, hi_lo, _) = defaults();
        let published = [-1.0, 0.0, 1.0, 2.0, 3.0, 4.0, 5.0];
        let own = |name: &str| -> Vec<f64> {
            let cs = if name.is_empty() {
                hi_lo.clone()
            } else {
                json!(CountSystem::presets().into_iter().find(|c| c.name.starts_with(name)).unwrap())
            };
            serde_json::from_value(ok(json!({ "op": "fromHiLo", "rules": r, "count": cs, "values": published })))
                .unwrap()
        };
        assert_eq!(own(""), published);
        // A level-2 count has about twice the numbers.
        assert_eq!(own("Zen"), [-2.0, 0.0, 2.0, 4.0, 5.0, 7.0, 9.0]);
        // KO's running count with 6 decks: Hi-Lo +4 is at its pivot (+4, within a point at mid-shoe), 4 points per true count.
        let ko = own("KO");
        assert!((ko[5] - 4.0).abs() <= 1.0, "{ko:?}");
        assert!(ko.windows(2).all(|w| w[1] - w[0] == 4.0), "{ko:?}");
    }

    /// The chart says what its values are: given no dealer blackjack where the dealer peeks, and basic strategy's when
    /// no index plays are in use (whatever count was asked for).
    #[test]
    fn chart_values_are_labelled() {
        let (r, cs, _) = defaults();
        let basic_only = json!({ "mode": "BasicOnly", "indexMin": -10, "indexMax": 10, "overrides": [] });
        let full = json!({ "mode": "FullIndices", "indexMin": -10, "indexMax": 10, "overrides": [] });
        let chart = ok(json!({ "op": "chart", "rules": r, "count": cs, "config": basic_only, "at": 3 }));
        assert_eq!((chart["basic"].as_bool(), chart["afterPeek"].as_bool()), (Some(true), Some(true)));
        let at3 = ok(json!({ "op": "chart", "rules": r, "count": cs, "config": full, "at": 3 }));
        assert_eq!(at3["basic"], false);
        // 16 v T, surrender: half a unit once the dealer has shown no blackjack.
        let cell = strategy::hard_cell(16, 10);
        let surrender = chart["cellEv"][cell * 5 + 3].as_f64().unwrap();
        assert!((surrender + 0.5).abs() < 1e-12, "{surrender}");
        let enhc = json!(Rules { hole_card: HoleCardRule::Enhc, ..Rules::default() });
        let no_peek = ok(json!({ "op": "chart", "rules": enhc, "count": cs, "config": full, "at": null }));
        assert_eq!(no_peek["afterPeek"], false);
    }

    /// Rules the simulator and the analysis would read differently are refused (the app brings files into range first).
    #[test]
    fn rules_off_the_grid_are_errors() {
        let (r, _, _) = defaults();
        for (field, value) in [
            ("blackjackPays", json!(4.0 / 3.0)),
            ("maxSplitHands", json!(5)),
            ("decks", json!(9)),
            ("penetration", json!(1.2)),
        ] {
            let mut bad = r.clone();
            bad[field] = value;
            assert!(handle(&json!({ "op": "houseEdge", "rules": bad })).is_err(), "{field}");
        }
        let mut six_five = r.clone();
        six_five["blackjackPays"] = json!(1.2);
        assert!(ok(json!({ "op": "houseEdge", "rules": six_five })).as_f64().unwrap() < -0.015);
    }

    #[test]
    fn risk_formulas() {
        let p = ok(json!({ "op": "phiInv", "p": [0.5, 0.9] }));
        assert_eq!(p[0].as_f64(), Some(analysis::phi_inv(0.5)));
        assert!((p[1].as_f64().unwrap() - 1.2816).abs() < 1e-3);
        let x = ok(json!({ "op": "phi", "x": [0.0] }));
        assert!((x[0].as_f64().unwrap() - 0.5).abs() < 1e-6);
        let ruin = ok(json!({ "op": "tripRuin", "mu": 1, "var": 1300, "bankroll": 500, "n": 10000 }));
        assert_eq!(ruin.as_f64(), Some(analysis::trip_ruin(1.0, 1300.0, 500.0, 10000.0)));
        let loss = ok(json!({ "op": "probLoss", "mu": 0.02, "var": 1.3, "n": 1000 }));
        assert_eq!(loss.as_f64(), Some(analysis::prob_loss(0.02, 1.3, 1000.0)));
    }

    #[test]
    fn strategy_chart_and_counts() {
        let (r, cs, sc) = defaults();
        let s = ok(json!({ "op": "strategy", "rules": r, "count": cs, "config": sc }));
        assert!(s["indices"].as_array().unwrap().iter().any(|i| i["hand"] == "16 v T"));
        let chart = ok(json!({ "op": "chart", "rules": r, "count": cs, "config": sc, "at": null }));
        assert_eq!(chart["codes"].as_array().unwrap().len(), strategy::CELLS_PER_BIN);
        // Surrender; hands of three or more cards, which can't, stand.
        assert_eq!(chart["codes"][strategy::hard_cell(16, 10)], "Rs");
        let info = ok(json!({ "op": "countInfo", "rules": r, "count": cs }));
        assert_eq!(info["factor"], 1.0);
        // The betting correlation needs the effects of removal, which come from their own (slow) request.
        assert!(info["bettingCorrelation"].is_null());
        let eor = ok(json!({ "op": "effectsOfRemoval", "rules": r }));
        assert_eq!(eor.as_array().unwrap().len(), 11);
        let info = ok(json!({ "op": "countInfo", "rules": r, "count": cs, "eor": eor }));
        let bc = info["bettingCorrelation"].as_f64().unwrap();
        assert!((0.955..0.975).contains(&bc), "Hi-Lo betting correlation {bc}");
        assert!(handle(&json!({ "op": "countInfo", "rules": r, "count": cs, "eor": [0, 1] })).is_err());
        assert_eq!(ok(json!({ "op": "presets" })).as_array().unwrap().len(), 13);
    }

    #[test]
    fn worker_tallies_and_analysis() {
        let (r, cs, sc) = defaults();
        ok(json!({ "op": "simPrepare", "rules": r, "count": cs, "config": sc, "spots": 2, "seed": "42", "worker": 0 }));
        let p = sim_run(100_000);
        assert!(!p.is_null());
        let t = unsafe { std::slice::from_raw_parts(p, TALLY_LEN) }.to_vec();
        let nb = strategy::NUM_BINS;
        assert_eq!(t[6 * nb], 100_000.0);
        assert_eq!(t[..nb].iter().sum::<f64>(), 100_000.0);

        let col = |k: usize| t[k * nb..(k + 1) * nb].to_vec();
        let sim = json!({
            "binMin": strategy::BIN_MIN, "numBins": nb, "spots": 2, "rounds": 100_000,
            "n": col(0), "sa": col(1), "qa": col(2), "sb": col(3), "qb": col(4), "ab": col(5),
        });
        let ramp = json!({ "min": -1, "max": 4, "units": [1, 1, 2, 4, 6, 8], "hands": [1, 1, 1, 1, 1, 2] });
        let e = ok(json!({ "op": "evaluate", "sim": sim, "ramp": ramp, "unit": 25, "rph": 100, "bankroll": 10000 }));
        assert_eq!(e["result"]["valid"], true);
        assert_eq!(e["rows"].as_array().unwrap().len(), 6);
        let opt = ok(json!({ "op": "optimize", "sim": sim, "ramp": ramp, "settings": { "maxUnits": 8 } }));
        assert_eq!(opt["units"].as_array().unwrap().len(), 6);
    }
}
