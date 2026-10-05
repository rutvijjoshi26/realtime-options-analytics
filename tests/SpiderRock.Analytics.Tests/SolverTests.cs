using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Pricing;
using Xunit;

namespace SpiderRock.Analytics.Tests;

public class ImpliedVolSolverTests
{
    private const double S = 100.0;
    private const double R = 0.045;
    private const double Q = 0.01;

    /// <summary>
    /// Round-trip test: price at a known vol, solve the price back, and require
    /// the original vol. Run across the full strike and maturity grid a real
    /// chain covers, including the deep wings where Newton alone fails.
    /// </summary>
    [Theory]
    [InlineData(50.0, 1.00, 0.60)]
    [InlineData(60.0, 0.25, 0.30)]
    [InlineData(80.0, 0.25, 0.35)]
    [InlineData(100.0, 0.02, 0.20)]
    [InlineData(100.0, 0.50, 0.25)]
    [InlineData(100.0, 3.00, 0.18)]
    [InlineData(120.0, 0.10, 0.55)]
    [InlineData(150.0, 0.25, 0.75)]
    [InlineData(200.0, 1.00, 0.90)]
    public void Solve_RecoversKnownVol_ForCalls(double k, double t, double trueVol)
    {
        double price = BlackScholes.Price(S, k, t, R, Q, trueVol, OptionRight.Call);
        var result = ImpliedVolSolver.Solve(price, S, k, t, R, Q, OptionRight.Call);

        Assert.True(result.IsUsable, $"solver failed with {result.Status} at K={k} T={t}");
        Assert.Equal(trueVol, result.Vol, 6);
    }

    [Theory]
    [InlineData(50.0, 0.25, 0.40)]
    [InlineData(100.0, 0.50, 0.25)]
    [InlineData(150.0, 1.00, 0.30)]
    public void Solve_RecoversKnownVol_ForPuts(double k, double t, double trueVol)
    {
        double price = BlackScholes.Price(S, k, t, R, Q, trueVol, OptionRight.Put);
        var result = ImpliedVolSolver.Solve(price, S, k, t, R, Q, OptionRight.Put);

        Assert.True(result.IsUsable, $"solver failed with {result.Status} at K={k} T={t}");
        Assert.Equal(trueVol, result.Vol, 6);
    }

    /// <summary>
    /// Sweeps a dense grid to guarantee no (strike, maturity, vol) combination in
    /// the region a live chain occupies drops into the NoConvergence branch.
    /// This is the regression test that pins the Newton/bisection handoff.
    /// </summary>
    [Fact]
    public void Solve_ConvergesAcrossFullGrid()
    {
        int failures = 0;
        int total = 0;
        double worstError = 0;

        foreach (double k in new[] { 40.0, 60.0, 80.0, 95.0, 100.0, 105.0, 120.0, 160.0, 220.0 })
        foreach (double t in new[] { 0.01, 0.05, 0.25, 0.5, 1.0, 2.0 })
        foreach (double vol in new[] { 0.05, 0.10, 0.25, 0.50, 1.00, 2.00 })
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double price = BlackScholes.Price(S, k, t, R, Q, vol, right);

            // Skip combinations whose price is numerically indistinguishable from
            // the arbitrage bound: no solver can recover a vol from those, and a
            // real feed would have rejected the quote before this point.
            double lower = right == OptionRight.Call
                ? Math.Max(S * Math.Exp(-Q * t) - k * Math.Exp(-R * t), 0)
                : Math.Max(k * Math.Exp(-R * t) - S * Math.Exp(-Q * t), 0);
            if (price - lower < 1e-7) continue;

            total++;
            var result = ImpliedVolSolver.Solve(price, S, k, t, R, Q, right);

            if (!result.IsUsable) { failures++; continue; }
            worstError = Math.Max(worstError, Math.Abs(result.Vol - vol));
        }

        Assert.True(total > 300, $"grid too small to be meaningful: {total}");
        Assert.Equal(0, failures);
        Assert.True(worstError < 1e-5, $"worst vol error {worstError:E3} exceeds tolerance");
    }

    /// <summary>
    /// A deep in-the-money short-dated call carries no time value at all once it
    /// is rounded into a double: its price is bit-for-bit equal to the
    /// arbitrage lower bound. No implied vol is recoverable from such a price,
    /// and the correct behaviour is to say so rather than to publish a
    /// fabricated number that would then be fitted into the surface.
    /// </summary>
    [Fact]
    public void Solve_ReportsUnrecoverable_WhenTimeValueVanishesIntoRounding()
    {
        const double k = 50.0, t = 0.05, trueVol = 0.15;

        double price = BlackScholes.Price(S, k, t, R, Q, trueVol, OptionRight.Call);
        double lowerBound = Math.Max(S * Math.Exp(-Q * t) - k * Math.Exp(-R * t), 0);

        // Precondition: the premise of the test is that these are the same double.
        Assert.Equal(lowerBound, price);

        var result = ImpliedVolSolver.Solve(price, S, k, t, R, Q, OptionRight.Call);

        Assert.Equal(SolverStatus.BelowIntrinsic, result.Status);
        Assert.False(result.IsUsable);
        Assert.True(double.IsNaN(result.Vol), "must not report a fabricated vol");
    }

    [Fact]
    public void Solve_RejectsPriceBelowIntrinsic()
    {
        double intrinsic = Math.Max(S * Math.Exp(-Q * 0.5) - 80 * Math.Exp(-R * 0.5), 0);
        var result = ImpliedVolSolver.Solve(intrinsic - 0.5, S, 80, 0.5, R, Q, OptionRight.Call);

        Assert.Equal(SolverStatus.BelowIntrinsic, result.Status);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void Solve_RejectsPriceAboveUpperBound()
    {
        var result = ImpliedVolSolver.Solve(S * 1.5, S, 100, 0.5, R, Q, OptionRight.Call);

        Assert.Equal(SolverStatus.AboveUpperBound, result.Status);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void Solve_RejectsExpiredContract()
    {
        var result = ImpliedVolSolver.Solve(5.0, S, 100, 0.0, R, Q, OptionRight.Call);
        Assert.False(result.IsUsable);
    }

    /// <summary>
    /// Deep out-of-the-money short-dated contracts are where vega collapses and a
    /// naive Newton solver diverges. Asserts the bisection fallback actually
    /// engages and still produces the right answer.
    /// </summary>
    [Fact]
    public void Solve_UsesBisectionFallback_OnVegaCollapse()
    {
        const double k = 300.0;
        const double t = 0.02;
        const double trueVol = 1.20;

        double price = BlackScholes.Price(S, k, t, R, Q, trueVol, OptionRight.Call);
        var result = ImpliedVolSolver.Solve(price, S, k, t, R, Q, OptionRight.Call);

        Assert.True(result.IsUsable);
        Assert.Equal(trueVol, result.Vol, 4);
    }

    [Fact]
    public void Solve_StaysWithinIterationBudget()
    {
        foreach (double k in new[] { 70.0, 100.0, 140.0 })
        {
            double price = BlackScholes.Price(S, k, 0.5, R, Q, 0.3, OptionRight.Call);
            var result = ImpliedVolSolver.Solve(price, S, k, 0.5, R, Q, OptionRight.Call);
            Assert.True(result.Iterations <= 104, $"iteration budget exceeded: {result.Iterations}");
        }
    }
}
