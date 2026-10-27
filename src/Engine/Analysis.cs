using System;
using System.Collections.Generic;

namespace CalcBJ.Engine
{
    /// <summary>Single-hand statistics at one count value, in units of the initial bet.</summary>
    public sealed class BinStat
    {
        public int Value;
        public long N;
        public double Freq;
        public double Mean;
        /// <summary>E[x^2] for one hand.</summary>
        public double M2;
        /// <summary>E[x*y] for two hands played in the same round.</summary>
        public double Cov;
        public bool MeasuredCov;
        /// <summary>Variance of the estimate of Mean.</summary>
        public double MeanVar;
        public double Var { get { return M2 - Mean * Mean; } }
    }

    /// <summary>Bet ramp over a display range: row 0 covers every count at or below Min, the last row every count at or above Max.</summary>
    public sealed class BetRamp
    {
        public int Min;
        public int Max;
        public double[] Units;
        public int[] Hands;

        public static BetRamp Create(int min, int max, double units)
        {
            var r = new BetRamp();
            r.Min = min; r.Max = max;
            r.Units = new double[max - min + 1];
            r.Hands = new int[max - min + 1];
            for (int i = 0; i < r.Units.Length; i++) { r.Units[i] = units; r.Hands[i] = 1; }
            return r;
        }

        public int Rows { get { return Max - Min + 1; } }
        public int Row(int v) { return Math.Max(Min, Math.Min(Max, v)) - Min; }
        public double UnitsAt(int v) { return Units[Row(v)]; }
        public int HandsAt(int v) { return Hands[Row(v)]; }

        public BetRamp Clone()
        {
            var r = (BetRamp)MemberwiseClone();
            r.Units = (double[])Units.Clone();
            r.Hands = (int[])Hands.Clone();
            return r;
        }

        public BetRamp Resize(int min, int max)
        {
            var r = Create(min, max, 1);
            for (int v = min; v <= max; v++) { r.Units[v - min] = UnitsAt(v); r.Hands[v - min] = HandsAt(v); }
            return r;
        }

        public double MaxUnits()
        {
            double m = 0;
            foreach (double u in Units) m = Math.Max(m, u);
            return m;
        }

        public double MinPositiveUnits()
        {
            double m = double.MaxValue;
            foreach (double u in Units) if (u > 0) m = Math.Min(m, u);
            return m == double.MaxValue ? 0 : m;
        }
    }

    public sealed class AnalysisResult
    {
        public bool Valid;
        public double EV, Var, SD, M2;            // per observed round, units
        public double AvgBet;                     // initial units wagered per observed round
        public double PlayedFrac;
        public double AvgBetPlayed;
        public double EdgePct;
        public double DI, Score, N0;
        public double SeEV;
        public double WinPerHour, SdPerHour, WinPer100, SdPer100, SeWinPerHour;
        public double RoundsPerHour, Unit, Bankroll;
        public double ROR;
        public double CEPerHour;
        public double KellyUnit;
        public double HoursN0;
        public double Spread;
        public bool UsedAssumedCov;
        public double[] RowFreq, RowEV, RowContribution, RowM2;
        public long[] RowN;

        public double BankrollForRor(double ror)
        {
            if (EV <= 0 || ror <= 0 || ror >= 1) return double.PositiveInfinity;
            return -Math.Log(ror) * Var / (2 * EV) * Unit;
        }

        public double RorForBankroll(double dollars)
        {
            if (EV <= 0) return 1;
            double b = dollars / Unit;
            return Math.Exp(-2 * EV * b / Var);
        }
    }

    public sealed class OptimizerSettings
    {
        public double MinUnits = 1;
        public double MaxUnits = 12;
        public double Increment = 1;
        public int MaxHands = 1;
        public bool AllowWongOut;
        public bool Kelly;
        public double KellyFraction = 1;
        public double BankrollUnits = 1000;
    }

    public static class Analysis
    {
        /// <summary>Correlation between two hands in the same round, used when the simulation had a single spot.</summary>
        public const double AssumedCorrelation = 0.37;

        public static BinStat[] Bins(SimData d)
        {
            var res = new BinStat[d.NumBins];
            long total = 0;
            for (int i = 0; i < d.NumBins; i++) total += d.N[i];
            bool two = d.Spots >= 2;
            for (int i = 0; i < d.NumBins; i++)
            {
                var b = new BinStat();
                b.Value = d.BinMin + i;
                long n = d.N[i];
                b.N = n;
                b.Freq = total > 0 ? (double)n / total : 0;
                if (n > 0)
                {
                    if (two)
                    {
                        b.Mean = (d.SA[i] + (double)d.SB[i]) / (40.0 * n);
                        b.M2 = (d.QA[i] + (double)d.QB[i]) / (800.0 * n);
                        b.Cov = d.AB[i] / (400.0 * n);
                        b.MeasuredCov = true;
                        double v = Math.Max(0, b.M2 - b.Mean * b.Mean);
                        double c = Math.Max(0, b.Cov - b.Mean * b.Mean);
                        b.MeanVar = (v + c) / (2.0 * n);
                    }
                    else
                    {
                        b.Mean = d.SA[i] / (20.0 * n);
                        b.M2 = d.QA[i] / (400.0 * n);
                        double v = Math.Max(0, b.M2 - b.Mean * b.Mean);
                        b.Cov = b.Mean * b.Mean + AssumedCorrelation * v;
                        b.MeanVar = v / n;
                    }
                }
                res[i] = b;
            }
            return res;
        }

        /// <summary>Aggregates bins into ramp rows (tails collapse into the end rows).</summary>
        public static BinStat[] Rows(BinStat[] bins, BetRamp ramp)
        {
            int rows = ramp.Rows;
            var r = new BinStat[rows];
            var w = new double[rows];
            for (int i = 0; i < rows; i++) { r[i] = new BinStat(); r[i].Value = ramp.Min + i; }
            foreach (var b in bins)
            {
                if (b.N == 0) continue;
                int i = ramp.Row(b.Value);
                var x = r[i];
                x.N += b.N; x.Freq += b.Freq;
                x.Mean += b.Freq * b.Mean; x.M2 += b.Freq * b.M2; x.Cov += b.Freq * b.Cov;
                x.MeanVar += b.Freq * b.Freq * b.MeanVar;
                x.MeasuredCov = b.MeasuredCov;
            }
            for (int i = 0; i < rows; i++)
            {
                var x = r[i];
                if (x.Freq > 0)
                {
                    x.Mean /= x.Freq; x.M2 /= x.Freq; x.Cov /= x.Freq; x.MeanVar /= x.Freq * x.Freq;
                }
            }
            return r;
        }

        public static AnalysisResult Evaluate(SimData d, BetRamp ramp, double unit, double roundsPerHour, double bankroll)
        {
            var res = new AnalysisResult();
            res.Unit = unit; res.RoundsPerHour = roundsPerHour; res.Bankroll = bankroll;
            if (d == null || ramp == null) return res;
            var bins = Bins(d);
            var rows = Rows(bins, ramp);
            int nr = rows.Length;
            res.RowFreq = new double[nr]; res.RowEV = new double[nr]; res.RowContribution = new double[nr];
            res.RowM2 = new double[nr]; res.RowN = new long[nr];
            double ev = 0, m2 = 0, bet = 0, played = 0, seVar = 0;
            for (int i = 0; i < nr; i++)
            {
                var x = rows[i];
                res.RowFreq[i] = x.Freq; res.RowEV[i] = x.Mean; res.RowN[i] = x.N;
                res.RowM2[i] = x.M2;
                double u = ramp.Units[i];
                int h = Math.Max(1, ramp.Hands[i]);
                if (u <= 0 || x.Freq <= 0) continue;
                if (h > 1 && !x.MeasuredCov) res.UsedAssumedCov = true;
                double e = h * u * x.Mean;
                double q = u * u * (h * x.M2 + h * (h - 1) * x.Cov);
                ev += x.Freq * e;
                m2 += x.Freq * q;
                bet += x.Freq * h * u;
                played += x.Freq;
                res.RowContribution[i] = x.Freq * e;
                seVar += x.Freq * x.Freq * h * h * u * u * x.MeanVar;
            }
            if (bet <= 0) return res;
            res.Valid = true;
            res.EV = ev; res.M2 = m2; res.Var = Math.Max(1e-12, m2 - ev * ev); res.SD = Math.Sqrt(res.Var);
            res.AvgBet = bet; res.PlayedFrac = played; res.AvgBetPlayed = bet / played;
            res.EdgePct = ev / bet * 100;
            res.DI = 1000 * ev / res.SD;
            res.Score = ev > 0 ? 1e6 * ev * ev / res.Var : 0;
            res.N0 = ev != 0 ? res.Var / (ev * ev) : double.PositiveInfinity;
            res.HoursN0 = res.N0 / roundsPerHour;
            res.SeEV = Math.Sqrt(seVar);
            res.WinPerHour = ev * roundsPerHour * unit;
            res.SdPerHour = Math.Sqrt(res.Var * roundsPerHour) * unit;
            res.WinPer100 = ev * 100 * unit;
            res.SdPer100 = Math.Sqrt(res.Var * 100) * unit;
            res.SeWinPerHour = res.SeEV * roundsPerHour * unit;
            double bU = bankroll / unit;
            res.ROR = ev > 0 ? Math.Exp(-2 * ev * bU / res.Var) : 1.0;
            res.CEPerHour = (ev - res.Var / (2 * bU)) * roundsPerHour * unit;
            res.KellyUnit = ev > 0 ? bankroll * ev / m2 : 0;
            double mx = ramp.MaxUnits(), mn = ramp.MinPositiveUnits();
            res.Spread = mn > 0 ? mx / mn : 0;
            return res;
        }

        // ---------------- Normal distribution helpers ----------------

        public static double Phi(double x)
        {
            return 0.5 * Erfc(-x / Math.Sqrt(2));
        }

        static double Erfc(double x)
        {
            // Numerical Recipes erfc (Chebyshev), fractional error < 1.2e-7
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);
            double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? ans : 2.0 - ans;
        }

        static double LogPhi(double x)
        {
            if (x > -5) return Math.Log(Math.Max(1e-300, Phi(x)));
            // asymptotic tail
            double x2 = x * x;
            return -0.5 * x2 - Math.Log(-x) - 0.5 * Math.Log(2 * Math.PI) + Math.Log(1 - 1 / x2 + 3 / (x2 * x2));
        }

        /// <summary>Inverse standard normal CDF (Acklam).</summary>
        public static double PhiInv(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            double q, r;
            if (p < 0.02425)
            {
                q = Math.Sqrt(-2 * Math.Log(p));
                return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            if (p > 1 - 0.02425)
            {
                q = Math.Sqrt(-2 * Math.Log(1 - p));
                return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            q = p - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }

        /// <summary>Probability of losing the whole bankroll at some point within n rounds (Brownian approximation).</summary>
        public static double TripRuin(double mu, double var, double bankroll, double n)
        {
            if (n <= 0) return 0;
            if (bankroll <= 0) return 1;
            double s = Math.Sqrt(var * n);
            double a = (-bankroll - mu * n) / s;
            double b = (-bankroll + mu * n) / s;
            double expo = -2 * mu * bankroll / var;
            double second = Math.Exp(Math.Min(700, expo + LogPhi(b)));
            return Math.Min(1, Phi(a) + second);
        }

        /// <summary>Probability of being behind after n rounds.</summary>
        public static double ProbLoss(double mu, double var, double n)
        {
            if (n <= 0) return 0.5;
            return Phi(-mu * n / Math.Sqrt(var * n));
        }

        // ---------------- Bet optimisation ----------------

        static double RowGain(BinStat x, double b, int h, double lambda)
        {
            if (b <= 0) return 0;
            return x.Freq * (h * b * x.Mean - b * b * (h * x.M2 + h * (h - 1) * x.Cov) / (2 * lambda));
        }

        static void BestBet(BinStat x, OptimizerSettings os, double lambda, out double bet, out int hands)
        {
            bet = os.AllowWongOut ? 0 : os.MinUnits; hands = 1;
            double bestG = os.AllowWongOut ? 0 : RowGain(x, os.MinUnits, 1, lambda);
            if (x.Freq <= 0) { bet = os.AllowWongOut ? 0 : os.MinUnits; return; }
            int maxH = Math.Max(1, os.MaxHands);
            double inc = os.Increment > 0 ? os.Increment : 0.01;
            for (int h = 1; h <= maxH; h++)
            {
                double denom = x.M2 + (h - 1) * x.Cov;
                if (denom <= 0) continue;
                double raw = lambda * x.Mean / denom;
                double lo = Math.Floor(raw / inc) * inc, hi = lo + inc;
                double[] cand = { os.MinUnits, os.MaxUnits, lo, hi };
                foreach (double c0 in cand)
                {
                    double c = Math.Max(os.MinUnits, Math.Min(os.MaxUnits, c0));
                    c = Math.Round(c / inc) * inc;
                    if (c < os.MinUnits - 1e-9) c += inc;
                    if (c > os.MaxUnits + 1e-9) c -= inc;
                    double g = RowGain(x, c, h, lambda);
                    if (g > bestG + 1e-15) { bestG = g; bet = c; hands = h; }
                }
            }
        }

        static BetRamp Apply(BinStat[] rows, BetRamp template, OptimizerSettings os, double lambda)
        {
            var r = template.Clone();
            for (int i = 0; i < rows.Length; i++)
            {
                double b; int h;
                BestBet(rows[i], os, lambda, out b, out h);
                r.Units[i] = b; r.Hands[i] = h;
            }
            return r;
        }

        /// <summary>
        /// Kelly mode maximises growth (certainty equivalent) for the bankroll; otherwise searches the bet scale that
        /// maximises SCORE (EV/SD) inside the min/max bet constraints.
        /// </summary>
        public static BetRamp Optimize(SimData d, BetRamp template, OptimizerSettings os)
        {
            var rows = Rows(Bins(d), template);
            if (os.Kelly) return Apply(rows, template, os, Math.Max(1e-6, os.BankrollUnits * os.KellyFraction));
            BetRamp best = null; double bestScore = double.NegativeInfinity, bestLam = 1;
            for (int k = 0; k <= 120; k++)
            {
                double lam = Math.Pow(10, 0.5 + k * 0.05);
                var r = Apply(rows, template, os, lam);
                double sc = ScoreOf(rows, r);
                if (sc > bestScore) { bestScore = sc; best = r; bestLam = lam; }
            }
            for (int k = -20; k <= 20; k++)
            {
                double lam = bestLam * Math.Pow(10, k * 0.0025);
                var r = Apply(rows, template, os, lam);
                double sc = ScoreOf(rows, r);
                if (sc > bestScore) { bestScore = sc; best = r; }
            }
            return best ?? template.Clone();
        }

        static double ScoreOf(BinStat[] rows, BetRamp r)
        {
            double ev = 0, m2 = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                double u = r.Units[i]; int h = Math.Max(1, r.Hands[i]);
                if (u <= 0) continue;
                var x = rows[i];
                ev += x.Freq * h * u * x.Mean;
                m2 += x.Freq * u * u * (h * x.M2 + h * (h - 1) * x.Cov);
            }
            double v = m2 - ev * ev;
            if (v <= 0 || ev <= 0) return ev;
            return ev / Math.Sqrt(v);
        }
    }
}
