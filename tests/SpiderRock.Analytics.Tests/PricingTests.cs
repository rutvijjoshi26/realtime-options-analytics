using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Pricing;
using Xunit;

namespace SpiderRock.Analytics.Tests;

public class BlackScholesTests
{
    private const double S = 100.0;
    private const double R = 0.05;
    private const double Q = 0.02;

    [Fact]
    public void Cdf_MatchesKnownValues()
    {
        Assert.Equal(0.5, BlackScholes.Cdf(0.0), 6);
        Assert.Equal(0.8413447, BlackScholes.Cdf(1.0), 5);
        Assert.Equal(0.1586553, BlackScholes.Cdf(-1.0), 5);
        Assert.Equal(0.9772499, BlackScholes.Cdf(2.0), 5);
    }

    [Fact]
    public void Cdf_IsSymmetric()
    {
        for (double x = 0.1; x <= 4.0; x += 0.1)
            Assert.Equal(1.0, BlackScholes.Cdf(x) + BlackScholes.Cdf(-x), 6);
    }

    /// <summary>
    /// Put-call parity is the single strongest correctness check on a pricer:
    /// C - P must equal S*e^(-qT) - K*e^(-rT) at every strike, for any vol.
    /// A sign error anywhere in the formula breaks this immediately.
    /// </summary>
    [Theory]
    [InlineData(80.0, 0.25, 0.20)]
    [InlineData(100.0, 0.25, 0.20)]
    [InlineData(120.0, 0.25, 0.20)]
    [InlineData(100.0, 2.00, 0.45)]
    [InlineData(60.0, 1.00, 0.80)]
    public void PutCallParity_Holds(double k, double t, double vol)
    {
        double call = BlackScholes.Price(S, k, t, R, Q, vol, OptionRight.Call);
        double put = BlackScholes.Price(S, k, t, R, Q, vol, OptionRight.Put);
        double expected = S * Math.Exp(-Q * t) - k * Math.Exp(-R * t);

        Assert.Equal(expected, call - put, 8);
    }

    [Fact]
    public void Price_IsMonotonicIncreasingInVol()
    {
        double previous = 0;
        for (double vol = 0.05; vol <= 2.0; vol += 0.05)
        {
            double price = BlackScholes.Price(S, 105, 0.5, R, Q, vol, OptionRight.Call);
            Assert.True(price > previous, $"price fell at vol={vol}");
            previous = price;
        }
    }

    [Fact]
    public void Price_AtExpiry_IsIntrinsic()
    {
        Assert.Equal(10.0, BlackScholes.Price(110, 100, 0, R, Q, 0.3, OptionRight.Call), 10);
        Assert.Equal(0.0, BlackScholes.Price(90, 100, 0, R, Q, 0.3, OptionRight.Call), 10);
        Assert.Equal(10.0, BlackScholes.Price(90, 100, 0, R, Q, 0.3, OptionRight.Put), 10);
    }

    [Fact]
    public void Price_RespectsNoArbitrageBounds()
    {
        for (double k = 60; k <= 140; k += 5)
        {
            const double t = 0.75;
            double call = BlackScholes.Price(S, k, t, R, Q, 0.35, OptionRight.Call);
            double lower = Math.Max(S * Math.Exp(-Q * t) - k * Math.Exp(-R * t), 0);
            double upper = S * Math.Exp(-Q * t);

            Assert.True(call >= lower - 1e-9, $"call below intrinsic at K={k}");
            Assert.True(call <= upper + 1e-9, $"call above upper bound at K={k}");
        }
    }
}

public class GreeksTests
{
    private const double S = 100.0;
    private const double R = 0.05;
    private const double Q = 0.02;
    private const double Vol = 0.30;
    private const double T = 0.5;

    /// <summary>
    /// Every analytic greek is validated against a central finite-difference of
    /// the pricer. This catches the errors that unit-range assertions miss:
    /// a wrong scaling constant, a dropped dividend discount factor, or a
    /// derivative taken with respect to the wrong variable.
    /// </summary>
    [Theory]
    [InlineData(85.0, OptionRight.Call)]
    [InlineData(100.0, OptionRight.Call)]
    [InlineData(115.0, OptionRight.Call)]
    [InlineData(85.0, OptionRight.Put)]
    [InlineData(100.0, OptionRight.Put)]
    [InlineData(115.0, OptionRight.Put)]
    public void Delta_MatchesFiniteDifference(double k, OptionRight right)
    {
        const double h = 1e-4;
        double up = BlackScholes.Price(S + h, k, T, R, Q, Vol, right);
        double down = BlackScholes.Price(S - h, k, T, R, Q, Vol, right);
        double expected = (up - down) / (2 * h);

        var greeks = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, right);
        Assert.Equal(expected, greeks.Delta, 5);
    }

    [Theory]
    [InlineData(85.0)]
    [InlineData(100.0)]
    [InlineData(115.0)]
    public void Gamma_MatchesSecondFiniteDifference(double k)
    {
        const double h = 1e-2;
        double up = BlackScholes.Price(S + h, k, T, R, Q, Vol, OptionRight.Call);
        double mid = BlackScholes.Price(S, k, T, R, Q, Vol, OptionRight.Call);
        double down = BlackScholes.Price(S - h, k, T, R, Q, Vol, OptionRight.Call);
        double expected = (up - 2 * mid + down) / (h * h);

        var greeks = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, OptionRight.Call);
        Assert.Equal(expected, greeks.Gamma, 5);
    }

    [Theory]
    [InlineData(85.0, OptionRight.Call)]
    [InlineData(100.0, OptionRight.Put)]
    [InlineData(115.0, OptionRight.Call)]
    public void Vega_MatchesFiniteDifference_PerVolPoint(double k, OptionRight right)
    {
        const double h = 1e-5;
        double up = BlackScholes.Price(S, k, T, R, Q, Vol + h, right);
        double down = BlackScholes.Price(S, k, T, R, Q, Vol - h, right);
        double expectedPerUnit = (up - down) / (2 * h);

        var greeks = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, right);
        Assert.Equal(expectedPerUnit / 100.0, greeks.Vega, 6);
    }

    [Theory]
    [InlineData(100.0, OptionRight.Call)]
    [InlineData(100.0, OptionRight.Put)]
    public void Theta_MatchesFiniteDifference_PerCalendarDay(double k, OptionRight right)
    {
        const double h = 1e-6;
        double later = BlackScholes.Price(S, k, T + h, R, Q, Vol, right);
        double earlier = BlackScholes.Price(S, k, T - h, R, Q, Vol, right);
        // Theta is the derivative with respect to time passing, i.e. -d/dT.
        double expectedPerYear = -(later - earlier) / (2 * h);

        var greeks = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, right);
        Assert.Equal(expectedPerYear / 365.0, greeks.Theta, 5);
    }

    [Theory]
    [InlineData(100.0, OptionRight.Call)]
    [InlineData(100.0, OptionRight.Put)]
    public void Rho_MatchesFiniteDifference_PerPercent(double k, OptionRight right)
    {
        const double h = 1e-6;
        double up = BlackScholes.Price(S, k, T, R + h, Q, Vol, right);
        double down = BlackScholes.Price(S, k, T, R - h, Q, Vol, right);
        double expectedPerUnit = (up - down) / (2 * h);

        var greeks = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, right);
        Assert.Equal(expectedPerUnit / 100.0, greeks.Rho, 5);
    }

    [Fact]
    public void CallAndPutGamma_AreEqual()
    {
        var call = BlackScholes.ComputeGreeks(S, 100, T, R, Q, Vol, OptionRight.Call);
        var put = BlackScholes.ComputeGreeks(S, 100, T, R, Q, Vol, OptionRight.Put);
        Assert.Equal(call.Gamma, put.Gamma, 10);
        Assert.Equal(call.Vega, put.Vega, 10);
    }

    [Fact]
    public void DeltaSignAndRange_AreCorrect()
    {
        for (double k = 60; k <= 140; k += 10)
        {
            var call = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, OptionRight.Call);
            var put = BlackScholes.ComputeGreeks(S, k, T, R, Q, Vol, OptionRight.Put);

            Assert.InRange(call.Delta, 0.0, 1.0);
            Assert.InRange(put.Delta, -1.0, 0.0);
        }
    }
}
