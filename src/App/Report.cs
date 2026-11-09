using System;
using System.Globalization;
using System.Net;
using System.Text;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    /// <summary>Self-contained HTML summary of the current scenario.</summary>
    internal static class Report
    {
        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        static void Row(StringBuilder sb, string k, string v)
        {
            sb.Append("<tr><th>").Append(H(k)).Append("</th><td>").Append(H(v)).Append("</td></tr>\n");
        }

        public static string Build(AppState st)
        {
            var a = st.Result;
            var d = st.Sim;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>CalcBJ report</title><style>");
            sb.Append("body{font-family:Segoe UI,Arial,sans-serif;color:#1c2833;background:#f1f3f6;margin:0;padding:24px}");
            sb.Append(".card{background:#fff;border:1px solid #dce2e8;border-radius:6px;padding:16px 20px;margin:0 0 16px;max-width:980px}");
            sb.Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:15px;margin:0 0 10px}.muted{color:#687886;font-size:12px}");
            sb.Append("table{border-collapse:collapse;width:100%;font-size:13px}th,td{padding:5px 8px;border-bottom:1px solid #eef1f4;text-align:left}");
            sb.Append("th{color:#687886;font-weight:600;width:240px}td.n,th.n{text-align:right}.grid th{width:auto}.pos{color:#1f8f5f}.neg{color:#c0392b}");
            sb.Append(".tiles{display:flex;flex-wrap:wrap;gap:10px}.tile{flex:1 1 180px;border:1px solid #dce2e8;border-radius:6px;padding:10px 12px;background:#fafbfc}");
            sb.Append(".tile b{display:block;font-size:20px;margin-top:2px}.tile span{font-size:11px;color:#687886;text-transform:uppercase}");
            sb.Append("</style></head><body>");
            sb.Append("<div class=\"card\"><h1>CalcBJ report</h1><div class=\"muted\">Generated ").Append(H(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            if (d != null) sb.Append(" · ").Append(H(Theme.Count(d.Rounds))).Append(" simulated rounds");
            if (st.SimStale) sb.Append(" · <b class=\"neg\">settings changed after this simulation</b>");
            sb.Append("</div></div>");

            sb.Append("<div class=\"card\"><h2>Game</h2><table>");
            Row(sb, "Rules", st.Rules.Describe());
            Row(sb, "Spots dealt", st.Spots.ToString(CultureInfo.InvariantCulture));
            Row(sb, "Count", st.CountText() + " — " + st.Count.TagText());
            Row(sb, "Playing strategy", st.StrategyText());
            Row(sb, "Bet ramp (units)", st.RampText());
            Row(sb, "Unit / bankroll", Theme.Money(st.Unit) + " / " + Theme.Money0(st.Bankroll));
            Row(sb, "Rounds per hour", st.RoundsPerHour.ToString("0", CultureInfo.InvariantCulture));
            if (d != null) Row(sb, "Basic strategy EV off the top", Theme.SignedPct(d.BasicEV, 3));
            sb.Append("</table></div>");

            if (a != null && a.Valid)
            {
                sb.Append("<div class=\"card\"><h2>Results</h2><div class=\"tiles\">");
                Tile(sb, "Win rate / hour", Theme.Money(a.WinPerHour), a.WinPerHour >= 0);
                Tile(sb, "SD / hour", Theme.Money0(a.SdPerHour), true);
                Tile(sb, "Player edge", Theme.SignedPct(a.EdgePct / 100, 3), a.EdgePct >= 0);
                Tile(sb, "SCORE", Theme.F(a.Score, "0.00"), true);
                Tile(sb, "DI", Theme.F(a.DI, "0.00"), true);
                Tile(sb, "N0 (rounds)", a.EV > 0 ? Theme.F(a.N0, "#,0") : "never", true);
                Tile(sb, "Risk of ruin", Theme.Ror(a.ROR), a.ROR < 0.2);
                Tile(sb, "Bankroll for 5% ROR", Theme.Money0(a.BankrollForRor(0.05)), true);
                Tile(sb, "Kelly unit", Theme.Money(a.KellyUnit), true);
                Tile(sb, "Certainty equivalent / h", Theme.Money(a.CEPerHour), a.CEPerHour >= 0);
                sb.Append("</div><p class=\"muted\">Win rate ± ").Append(H(Theme.Money(a.SeWinPerHour))).Append(" per hour (1 standard error of the simulation). Average bet when playing ")
                  .Append(H(Theme.Money(a.AvgBetPlayed * st.Unit))).Append(", spread 1–").Append(H(Theme.F(a.Spread, "0.#"))).Append(", plays ").Append(H(Theme.Pct(a.PlayedFrac, 1))).Append(" of rounds.</p></div>");

                sb.Append("<div class=\"card\"><h2>By count</h2><table class=\"grid\"><tr><th>Count</th><th class=\"n\">Frequency</th><th class=\"n\">EV / hand</th><th class=\"n\">Units</th><th class=\"n\">Hands</th><th class=\"n\">Win / 100 rounds</th></tr>");
                var r = st.Ramp;
                for (int i = 0; i < r.Rows; i++)
                {
                    if (a.RowFreq[i] <= 0) continue;
                    sb.Append("<tr><td>").Append(H(Theme.CountLabel(r.Min + i, r.Min, r.Max))).Append("</td><td class=\"n\">").Append(H(Theme.Pct(a.RowFreq[i], 2)))
                      .Append("</td><td class=\"n ").Append(a.RowEV[i] >= 0 ? "pos" : "neg").Append("\">").Append(H(Theme.SignedPct(a.RowEV[i], 2)))
                      .Append("</td><td class=\"n\">").Append(H(r.Units[i].ToString("0.##", CultureInfo.InvariantCulture)))
                      .Append("</td><td class=\"n\">").Append(r.Hands[i])
                      .Append("</td><td class=\"n\">").Append(H(Theme.Money(a.RowContribution[i] * 100 * st.Unit))).Append("</td></tr>\n");
                }
                sb.Append("</table></div>");
            }

            var pt = st.Table;
            var list = StrategyBuilder.DescribeIndices(pt, st.Strategy.IndexMin, st.Strategy.IndexMax);
            sb.Append("<div class=\"card\"><h2>Index plays</h2><table class=\"grid\"><tr><th>Hand</th><th>Play</th><th>When</th></tr>");
            int shown = 0;
            foreach (var i in list)
            {
                if (i.Condition == "always") continue;
                sb.Append("<tr><td>").Append(H(i.Hand)).Append("</td><td>").Append(H(i.Play)).Append("</td><td>").Append(H(i.Condition)).Append("</td></tr>\n");
                shown++;
            }
            if (shown == 0) sb.Append("<tr><td colspan=\"3\">Basic strategy only</td></tr>");
            sb.Append("</table></div>");
            sb.Append("<p class=\"muted\">CalcBJ — results are estimates from simulation and combinatorial analysis.</p></body></html>");
            return sb.ToString();
        }

        static void Tile(StringBuilder sb, string caption, string value, bool good)
        {
            sb.Append("<div class=\"tile\"><span>").Append(H(caption)).Append("</span><b class=\"").Append(good ? "" : "neg").Append("\">").Append(H(value)).Append("</b></div>");
        }
    }
}
