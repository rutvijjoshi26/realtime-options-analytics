using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Pricing;

public enum SolverStatus
{
    Converged,
    /// <summary>Newton diverged or stalled; the bracketed bisection fallback produced the answer.</summary>
    ConvergedByBisection,
    /// <summary>Price is at or below intrinsic - no positive vol reproduces it.</summary>
    BelowIntrinsic,
    /// <summary>Price exceeds the no-arbitrage upper bound for the contract.</summary>
    AboveUpperBound,
    /// <summary>Neither method reached tolerance inside the iteration budget.</summary>
    NoConvergence
}

public readonly record struct ImpliedVolResult(double Vol, SolverStatus Status, int Iterations)
{
    public bool IsUsable => Status is SolverStatus.Converged or SolverStatus.ConvergedByBisection;
}

/// <summary>
/// Inverts Black-Scholes for volatility.
///
/// Newton-Raphson converges in a handful of iterations near the money, but its
/// step is proportional to 1/vega, and vega collapses toward zero for deep
/// in- and out-of-the-money contracts. On a live options chain a meaningful
/// share of quotes sit exactly there, so a pure Newton solver either diverges
/// or walks off into a negative vol on the wings. This solver keeps Newton for
/// the fast path but validates every step against a maintained bracket, and
/// hands off to bisection the moment Newton tries to leave it - bounded
/// iteration count, guaranteed answer whenever one exists.
/// </summary>
public static class ImpliedVolSolver
{
    public const double MinVol = 1e-4;
    public const double MaxVol = 5.0;

    /// <summary>
    /// Convergence is measured on the width of the volatility bracket, not on
    /// the pricing error.
    ///
    /// Terminating on an absolute price tolerance seems natural but is wrong for
    /// exactly the contracts that need the most care: a deep out-of-the-money
    /// option can be worth 2e-7 with a vega of 1e-6, so a price tolerance of
    /// 1e-8 is several percent of the option's value and lets the solver stop
    /// while the implied vol is still off by 8e-4. Bisecting on vol width
    /// instead bounds the answer directly, independently of how insensitive the
    /// price happens to be to volatility.
    /// </summary>
    private const double VolTolerance = 1e-9;

    /// <summary>Guards against a price that is numerically indistinguishable from its arbitrage bound.</summary>
    private const double BoundEpsilon = 1e-12;

    private const int MaxNewtonIterations = 24;
    private const int MaxBisectionIterations = 80;

    public static ImpliedVolResult Solve(
        double targetPrice,
        double s,
        double k,
        double t,
        double r,
        double q,
        OptionRight right)
    {
        if (t <= 0 || s <= 0 || k <= 0)
            return new ImpliedVolResult(double.NaN, SolverStatus.BelowIntrinsic, 0);

        // No-arbitrage bounds. Outside these there is no real volatility that
        // reprices the option, and reporting a clamped number would be worse
        // than reporting nothing - it would silently distort the surface fit.
        double dfR = Math.Exp(-r * t);
        double dfQ = Math.Exp(-q * t);

        double lowerBound = right == OptionRight.Call
            ? Math.Max(s * dfQ - k * dfR, 0.0)
            : Math.Max(k * dfR - s * dfQ, 0.0);
        double upperBound = right == OptionRight.Call ? s * dfQ : k * dfR;

        if (targetPrice <= lowerBound + BoundEpsilon)
            return new ImpliedVolResult(double.NaN, SolverStatus.BelowIntrinsic, 0);
        if (targetPrice >= upperBound - BoundEpsilon)
            return new ImpliedVolResult(double.NaN, SolverStatus.AboveUpperBound, 0);

        // Brenner-Subrahmanyam ATM approximation as the Newton seed. Starting
        // near the answer rather than at a fixed 0.20 guess cuts the average
        // iteration count roughly in half across a full chain.
        double seed = Math.Sqrt(2.0 * Math.PI / t) * targetPrice / s;
        double vol = Math.Clamp(double.IsFinite(seed) ? seed : 0.2, 0.01, 2.0);

        double lo = MinVol;
        double hi = MaxVol;
        int iterations = 0;

        for (int i = 0; i < MaxNewtonIterations; i++)
        {
            iterations++;
            double price = BlackScholes.Price(s, k, t, r, q, vol, right);
            double diff = price - targetPrice;

            if (diff == 0.0)
                return new ImpliedVolResult(vol, SolverStatus.Converged, iterations);

            // Price is monotonic increasing in vol, so every evaluation tightens
            // the bracket regardless of whether the Newton step is accepted.
            if (diff > 0) hi = vol; else lo = vol;

            if (hi - lo < VolTolerance)
                return new ImpliedVolResult(0.5 * (lo + hi), SolverStatus.Converged, iterations);

            double vega = BlackScholes.RawVega(s, k, t, r, q, vol);
            if (vega < 1e-14) break; // Vega collapse: Newton has nothing to work with.

            double next = vol - diff / vega;
            if (!double.IsFinite(next) || next <= lo || next >= hi)
                break; // Step left the bracket; bisection takes over.

            // Converged on step size rather than on pricing error, so accuracy
            // does not degrade on contracts whose price barely moves with vol.
            if (Math.Abs(next - vol) < VolTolerance)
                return new ImpliedVolResult(next, SolverStatus.Converged, iterations);

            vol = next;
        }

        // Bisection fallback on the bracket Newton already narrowed.
        for (int i = 0; i < MaxBisectionIterations; i++)
        {
            iterations++;
            double mid = 0.5 * (lo + hi);

            if (hi - lo < VolTolerance)
                return new ImpliedVolResult(mid, SolverStatus.ConvergedByBisection, iterations);

            double diff = BlackScholes.Price(s, k, t, r, q, mid, right) - targetPrice;
            if (diff == 0.0)
                return new ImpliedVolResult(mid, SolverStatus.ConvergedByBisection, iterations);

            if (diff > 0) hi = mid; else lo = mid;
        }

        return new ImpliedVolResult(double.NaN, SolverStatus.NoConvergence, iterations);
    }
}
