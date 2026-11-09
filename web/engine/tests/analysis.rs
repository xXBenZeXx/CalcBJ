//! Bet-ramp evaluation, risk formulas and the optimiser against the C# engine (fixtures/analysis.json).

mod common;

use calcbj_engine::analysis;
use common::*;

/// exp, ln and powf may differ from .NET in the last bit; everything else is identical arithmetic.
const TOL: f64 = 1e-12;

fn rel(want: f64) -> f64 {
    TOL * want.abs().max(1.0)
}

#[test]
fn bins_rows_and_evaluation() {
    let fx = fixture("analysis.json");
    let mut c = Check::new("analysis");
    for s in list(&fx["sims"]) {
        let label = text(&s["label"]);
        let d = sim_data(&s["data"]);
        let bins = analysis::bins(&d);
        let mut seen = 0;
        for w in list(&s["bins"]) {
            let b = &bins[int(&w["i"]) as usize];
            let what = format!("{label}: count {}", b.value);
            seen += 1;
            c.eq(&format!("{what} value/n"), (b.value as i64, b.n), (int(&w["value"]), int(&w["n"])));
            c.num(&format!("{what} freq"), b.freq, num(&w["freq"]), 0.0);
            c.num(&format!("{what} mean"), b.mean, num(&w["mean"]), 0.0);
            c.num(&format!("{what} m2"), b.m2, num(&w["m2"]), 0.0);
            c.num(&format!("{what} cov"), b.cov, num(&w["cov"]), 0.0);
            c.eq(&format!("{what} measured cov"), b.measured_cov, flag(&w["measuredCov"]));
            c.num(&format!("{what} mean var"), b.mean_var, num(&w["meanVar"]), 0.0);
            c.num(&format!("{what} last seat mean"), b.mean_last, num(&w["meanLast"]), 0.0);
            c.num(&format!("{what} last seat m2"), b.m2_last, num(&w["m2Last"]), 0.0);
            c.num(&format!("{what} last seat mean var"), b.mean_var_last, num(&w["meanVarLast"]), 0.0);
        }
        c.eq(&format!("{label}: bins with hands"), bins.iter().filter(|b| b.n > 0).count(), seen);

        for e in list(&s["evaluations"]) {
            let r = ramp(&e["ramp"]);
            let (unit, rph, bankroll) = (num(&e["unit"]), num(&e["rph"]), num(&e["bankroll"]));
            let a = analysis::evaluate(&d, &r, unit, rph, bankroll);
            let w = &e["result"];
            let what = format!("{label}: ramp {:?} x{:?} unit {unit}", r.units, r.hands);
            c.eq(&format!("{what} valid"), a.valid, flag(&w["valid"]));
            c.eq(&format!("{what} assumed cov"), a.used_assumed_cov, flag(&w["usedAssumedCov"]));
            for (name, got) in [
                ("ev", a.ev),
                ("var", a.var),
                ("sd", a.sd),
                ("m2", a.m2),
                ("avgBet", a.avg_bet),
                ("playedFrac", a.played_frac),
                ("avgBetPlayed", a.avg_bet_played),
                ("edgePct", a.edge_pct),
                ("di", a.di),
                ("score", a.score),
                ("n0", a.n0),
                ("seEV", a.se_ev),
                ("winPerHour", a.win_per_hour),
                ("sdPerHour", a.sd_per_hour),
                ("winPer100", a.win_per_100),
                ("sdPer100", a.sd_per_100),
                ("seWinPerHour", a.se_win_per_hour),
                ("roundsPerHour", a.rounds_per_hour),
                ("unit", a.unit),
                ("bankroll", a.bankroll),
                ("ror", a.ror),
                ("cePerHour", a.ce_per_hour),
                ("kellyUnit", a.kelly_unit),
                ("hoursN0", a.hours_n0),
                ("spread", a.spread),
            ] {
                let want = num(&w[name]);
                c.num(&format!("{what} {name}"), got, want, rel(want));
            }
            c.nums(&format!("{what} rowFreq"), &a.row_freq, &nums(&w["rowFreq"]), 0.0);
            c.nums(&format!("{what} rowEV"), &a.row_ev, &nums(&w["rowEV"]), 0.0);
            c.nums(&format!("{what} rowContribution"), &a.row_contribution, &nums(&w["rowContribution"]), 0.0);
            c.nums(&format!("{what} rowM2"), &a.row_m2, &nums(&w["rowM2"]), 0.0);
            c.eq(&format!("{what} rowN"), a.row_n.clone(), ints(&w["rowN"]));
            let ror: Vec<f64> = [0.05, 0.135, 0.01, 0.5].iter().map(|&x| a.bankroll_for_ror(x)).collect();
            for (g, want) in ror.iter().zip(nums(&w["bankrollForRor"])) {
                c.num(&format!("{what} bankroll for ROR"), *g, want, rel(want));
            }
            let back: Vec<f64> = [1000.0, 5000.0, 20000.0].iter().map(|&x| a.ror_for_bankroll(x)).collect();
            for (g, want) in back.iter().zip(nums(&w["rorForBankroll"])) {
                c.num(&format!("{what} ROR for bankroll"), *g, want, rel(want));
            }
        }
    }
    c.finish();
}

#[test]
fn optimiser() {
    let fx = fixture("analysis.json");
    let mut c = Check::new("optimiser");
    for s in list(&fx["sims"]) {
        let label = text(&s["label"]);
        let d = sim_data(&s["data"]);
        for o in list(&s["optimizations"]) {
            let template = ramp(&o["template"]);
            let os = settings(&o["settings"]);
            let got = analysis::optimize(&d, &template, &os);
            let want = ramp(&o["result"]);
            let what = format!("{label}: {os:?}");
            c.eq(&format!("{what} units"), got.units, want.units);
            c.eq(&format!("{what} hands"), got.hands, want.hands);
        }
    }
    c.finish();
}

#[test]
fn normal_and_ruin_formulas() {
    let fx = fixture("analysis.json");
    let mut c = Check::new("normal and ruin formulas");
    for p in list(&fx["phi"]) {
        let (x, want) = (num(&p[0]), num(&p[1]));
        c.num(&format!("Phi({x})"), analysis::phi(x), want, rel(want));
    }
    for p in list(&fx["phiInv"]) {
        let (x, want) = (num(&p[0]), num(&p[1]));
        c.num(&format!("PhiInv({x})"), analysis::phi_inv(x), want, rel(want));
    }
    for t in list(&fx["tripRuin"]) {
        let x = nums(t);
        c.num(&format!("TripRuin{:?}", &x[..4]), analysis::trip_ruin(x[0], x[1], x[2], x[3]), x[4], rel(x[4]));
    }
    for t in list(&fx["probLoss"]) {
        let x = nums(t);
        c.num(&format!("ProbLoss{:?}", &x[..3]), analysis::prob_loss(x[0], x[1], x[2]), x[3], rel(x[3]));
    }
    c.finish();
}
