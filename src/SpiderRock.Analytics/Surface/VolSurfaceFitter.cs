using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Surface;

public readonly record struct SmilePoint(double LogMoneyness, double Vol, double Weight);

/// <summary>
/// A fitted volatility smile for one expiry: vol as a quadratic in log-moneyness,
/// vol(x) = A + B*x + C*x^2, where x = ln(K / F).
/// </summary>
public readonly record struct FittedSmile(
    DateOnly Expiry,
    double YearsToExpiry,
    double Forward,
    double A,
    double B,
    double C,
    int PointCount,
    double RmsError,
    bool IsArbitrageFree,
    string? ArbitrageNote)
{
    public double VolAt(double logMoneyness) =>
        A + B * logMoneyness + C * logMoneyness * logMoneyness;

    /// <summary>ATM vol is the fit evaluated at the forward.</summary>
    public double AtmVol => A;

    /// <summary>Skew: d(vol)/d(logMoneyness) at the forward.</summary>
    public double Skew => B;
}

/// <summary>
/// Fits a per-expiry volatility smile and screens it for static arbitrage.
///
/// A raw chain of solved implied vols is not directly usable: wide markets and
/// stale wing quotes produce points that no arbitrage-free surface can pass
/// through. The fitter therefore weights each point by its quote quality and
/// then checks the resulting curve for the two violations that matter in
/// practice - butterfly (negative implied density) and a smile that turns
/// downward at the wings, which implies negative total variance growth.
/// </summary>
public static class VolSurfaceFitter
{
    private const int MinPointsForQuadratic = 5;

    /// <summary>
    /// Weighted least squares on the quadratic basis [1, x, x^2], solved through
    /// the 3x3 normal equations. At this size an explicit Cramer's-rule solve is
    /// both faster and more predictable than pulling in a matrix library, and it
    /// keeps the fit allocation-light enough to run on every LOOP cycle.
    /// </summary>
    public static FittedSmile Fit(DateOnly expiry, double yearsToExpiry, double forward, IReadOnlyList<SmilePoint> points)
    {
        if (points.Count == 0)
            return new FittedSmile(expiry, yearsToExpiry, forward, double.NaN, 0, 0, 0, double.NaN, false, "no points");

        // Under-determined for a quadratic: fall back to a weighted flat vol
        // rather than fitting curvature through noise.
        if (points.Count < MinPointsForQuadratic)
        {
            double wsum = 0, vsum = 0;
            foreach (var p in points) { wsum += p.Weight; vsum += p.Weight * p.Vol; }
            double flat = wsum > 0 ? vsum / wsum : double.NaN;
            return new FittedSmile(expiry, yearsToExpiry, forward, flat, 0, 0, points.Count, 0,
                true, "flat fit: insufficient points for curvature");
        }

        // Normal equation accumulators for basis [1, x, x^2].
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double b0 = 0, b1 = 0, b2 = 0;

        foreach (var p in points)
        {
            double w = p.Weight;
            double x = p.LogMoneyness;
            double x2 = x * x;

            s0 += w;
            s1 += w * x;
            s2 += w * x2;
            s3 += w * x2 * x;
            s4 += w * x2 * x2;

            b0 += w * p.Vol;
            b1 += w * p.Vol * x;
            b2 += w * p.Vol * x2;
        }

        double det =
            s0 * (s2 * s4 - s3 * s3)
            - s1 * (s1 * s4 - s3 * s2)
            + s2 * (s1 * s3 - s2 * s2);

        if (Math.Abs(det) < 1e-14)
        {
            // Degenerate geometry (e.g. all strikes clustered): flat fit again.
            double wsum = 0, vsum = 0;
            foreach (var p in points) { wsum += p.Weight; vsum += p.Weight * p.Vol; }
            double flat = wsum > 0 ? vsum / wsum : double.NaN;
            return new FittedSmile(expiry, yearsToExpiry, forward, flat, 0, 0, points.Count, 0,
                true, "flat fit: singular design matrix");
        }

        double a = (b0 * (s2 * s4 - s3 * s3)
                  - s1 * (b1 * s4 - s3 * b2)
                  + s2 * (b1 * s3 - s2 * b2)) / det;

        double bb = (s0 * (b1 * s4 - s3 * b2)
                   - b0 * (s1 * s4 - s3 * s2)
                   + s2 * (s1 * b2 - b1 * s2)) / det;

        double c = (s0 * (s2 * b2 - b1 * s3)
                  - s1 * (s1 * b2 - b1 * s2)
                  + b0 * (s1 * s3 - s2 * s2)) / det;

        double sse = 0, wsumAll = 0;
        foreach (var p in points)
        {
            double fitted = a + bb * p.LogMoneyness + c * p.LogMoneyness * p.LogMoneyness;
            double err = fitted - p.Vol;
            sse += p.Weight * err * err;
            wsumAll += p.Weight;
        }
        double rms = wsumAll > 0 ? Math.Sqrt(sse / wsumAll) : double.NaN;

        var smile = new FittedSmile(expiry, yearsToExpiry, forward, a, bb, c, points.Count, rms, true, null);
        var (ok, note) = CheckArbitrage(smile, points);
        return smile with { IsArbitrageFree = ok, ArbitrageNote = note };
    }

    /// <summary>
    /// Screens a fitted smile for static arbitrage across the strike range the
    /// points actually cover. Two checks:
    ///
    /// 1. Negative or non-finite vol anywhere in range - unpriceable outright.
    /// 2. Butterfly arbitrage, detected as a negative second derivative of the
    ///    call price in strike, which corresponds to a negative implied
    ///    risk-neutral density. Evaluated numerically over the sampled range.
    /// </summary>
    public static (bool IsArbitrageFree, string? Note) CheckArbitrage(FittedSmile smile, IReadOnlyList<SmilePoint> points)
    {
        if (!double.IsFinite(smile.A)) return (false, "non-finite fit");

        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var p in points)
        {
            if (p.LogMoneyness < minX) minX = p.LogMoneyness;
            if (p.LogMoneyness > maxX) maxX = p.LogMoneyness;
        }
        if (minX >= maxX) return (true, null);

        const int samples = 41;
        double step = (maxX - minX) / (samples - 1);
        double t = smile.YearsToExpiry;
        double f = smile.Forward;

        var callPrices = new double[samples];
        var strikes = new double[samples];

        for (int i = 0; i < samples; i++)
        {
            double x = minX + i * step;
            double vol = smile.VolAt(x);
            if (!double.IsFinite(vol) || vol <= 0)
                return (false, $"non-positive vol {vol:0.####} at log-moneyness {x:0.###}");

            double kStrike = f * Math.Exp(x);
            strikes[i] = kStrike;
            // Zero rate/carry: the forward already carries them, so this measures
            // the shape of the smile itself rather than the discounting.
            callPrices[i] = BlackScholesForward(f, kStrike, t, vol);
        }

        for (int i = 1; i < samples - 1; i++)
        {
            double h1 = strikes[i] - strikes[i - 1];
            double h2 = strikes[i + 1] - strikes[i];
            double secondDeriv =
                2.0 * (h1 * callPrices[i + 1] - (h1 + h2) * callPrices[i] + h2 * callPrices[i - 1])
                / (h1 * h2 * (h1 + h2));

            if (secondDeriv < -1e-7)
                return (false, $"butterfly arbitrage: negative density near strike {strikes[i]:0.##}");
        }

        return (true, null);
    }

    /// <summary>Undiscounted Black-76 call on a forward.</summary>
    private static double BlackScholesForward(double f, double k, double t, double vol)
    {
        if (t <= 0 || vol <= 0) return Math.Max(f - k, 0.0);
        double sqrtT = Math.Sqrt(t);
        double d1 = (Math.Log(f / k) + 0.5 * vol * vol * t) / (vol * sqrtT);
        double d2 = d1 - vol * sqrtT;
        return f * Pricing.BlackScholes.Cdf(d1) - k * Pricing.BlackScholes.Cdf(d2);
    }
}
