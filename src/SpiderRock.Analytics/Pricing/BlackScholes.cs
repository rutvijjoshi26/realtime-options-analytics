using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Pricing;

/// <summary>
/// Generalized Black-Scholes-Merton pricing and risk factors for European options
/// on a dividend-paying underlying.
///
/// All methods are static, allocation-free, and safe to call concurrently: the
/// analytics engine fans quotes out across worker tasks and calls straight in.
/// </summary>
public static class BlackScholes
{
    /// <summary>Standard normal PDF.</summary>
    public static double Pdf(double x) => 0.3989422804014327 * Math.Exp(-0.5 * x * x);

    /// <summary>
    /// Standard normal CDF via Hart's double-precision rational approximation,
    /// accurate to roughly 1e-15 absolute across the full range.
    ///
    /// The obvious cheaper choice here is the Abramowitz &amp; Stegun 7.1.26 erf
    /// approximation, but its ~1.5e-7 error is not survivable downstream: the
    /// implied vol solver inverts this function, and inverting a price that is
    /// only good to 1e-7 caps recovered vol accuracy at roughly 8e-4 vol points.
    /// That error is larger than the bid/ask edge on a tight options market, so
    /// the accuracy is bought deliberately.
    /// </summary>
    public static double Cdf(double x)
    {
        double y = Math.Abs(x);

        // Beyond 37 standard deviations the tail underflows double precision.
        if (y > 37.0) return x > 0 ? 1.0 : 0.0;

        double e = Math.Exp(-y * y / 2.0);
        double c;

        if (y < 7.07106781186547)
        {
            // Rational polynomial branch for the body of the distribution.
            double num = 3.52624965998911e-02 * y + 0.700383064443688;
            num = num * y + 6.37396220353165;
            num = num * y + 33.912866078383;
            num = num * y + 112.079291497871;
            num = num * y + 221.213596169931;
            num = num * y + 220.206867912376;

            double den = 8.83883476483184e-02 * y + 1.75566716318264;
            den = den * y + 16.064177579207;
            den = den * y + 86.7807322029461;
            den = den * y + 296.564248779674;
            den = den * y + 637.333633378831;
            den = den * y + 793.826512519948;
            den = den * y + 440.413735824752;

            c = e * num / den;
        }
        else
        {
            // Continued-fraction branch for the far tail, where the polynomial
            // ratio loses precision.
            double b = y + 1.0 / (y + 2.0 / (y + 3.0 / (y + 4.0 / (y + 0.65))));
            c = e / (b * 2.506628274631);
        }

        return x > 0 ? 1.0 - c : c;
    }

    private static (double d1, double d2) D(double s, double k, double t, double r, double q, double vol)
    {
        double sqrtT = Math.Sqrt(t);
        double d1 = (Math.Log(s / k) + (r - q + 0.5 * vol * vol) * t) / (vol * sqrtT);
        return (d1, d1 - vol * sqrtT);
    }

    /// <summary>
    /// Theoretical price. At t = 0 or vol = 0 the option is worth its intrinsic
    /// value; returning that instead of dividing by zero keeps expiring contracts
    /// from poisoning the surface fit on expiration day.
    /// </summary>
    public static double Price(double s, double k, double t, double r, double q, double vol, OptionRight right)
    {
        if (t <= 0 || vol <= 0)
        {
            double intrinsic = right == OptionRight.Call ? s - k : k - s;
            return Math.Max(intrinsic, 0.0);
        }

        var (d1, d2) = D(s, k, t, r, q, vol);
        double dfQ = Math.Exp(-q * t);
        double dfR = Math.Exp(-r * t);

        return right == OptionRight.Call
            ? s * dfQ * Cdf(d1) - k * dfR * Cdf(d2)
            : k * dfR * Cdf(-d2) - s * dfQ * Cdf(-d1);
    }

    /// <summary>
    /// All five risk factors in one pass. Computing them together rather than
    /// through five separate entry points avoids recomputing d1, d2, and the two
    /// discount factors five times over on every tick.
    ///
    /// Vega is per 1 vol point, theta is per calendar day, rho is per 1% rate
    /// move - the conventions a trader reads off a risk screen.
    /// </summary>
    public static Greeks ComputeGreeks(double s, double k, double t, double r, double q, double vol, OptionRight right)
    {
        if (t <= 0 || vol <= 0)
        {
            // At expiry delta is a step function and the higher-order greeks vanish.
            double itmDelta = right == OptionRight.Call
                ? (s > k ? 1.0 : 0.0)
                : (s < k ? -1.0 : 0.0);
            return new Greeks(itmDelta, 0, 0, 0, 0);
        }

        var (d1, d2) = D(s, k, t, r, q, vol);
        double sqrtT = Math.Sqrt(t);
        double dfQ = Math.Exp(-q * t);
        double dfR = Math.Exp(-r * t);
        double pdfD1 = Pdf(d1);

        double delta = right == OptionRight.Call
            ? dfQ * Cdf(d1)
            : dfQ * (Cdf(d1) - 1.0);

        double gamma = dfQ * pdfD1 / (s * vol * sqrtT);
        double vega = s * dfQ * pdfD1 * sqrtT / 100.0;

        double term1 = -(s * dfQ * pdfD1 * vol) / (2.0 * sqrtT);
        double theta = right == OptionRight.Call
            ? (term1 - r * k * dfR * Cdf(d2) + q * s * dfQ * Cdf(d1)) / 365.0
            : (term1 + r * k * dfR * Cdf(-d2) - q * s * dfQ * Cdf(-d1)) / 365.0;

        double rho = right == OptionRight.Call
            ? k * t * dfR * Cdf(d2) / 100.0
            : -k * t * dfR * Cdf(-d2) / 100.0;

        return new Greeks(delta, gamma, vega, theta, rho);
    }

    /// <summary>
    /// Raw vega (per 1.00 of vol, not per point) used internally by the Newton
    /// step in the implied vol solver, where the /100 scaling would have to be
    /// undone anyway.
    /// </summary>
    internal static double RawVega(double s, double k, double t, double r, double q, double vol)
    {
        if (t <= 0 || vol <= 0) return 0.0;
        var (d1, _) = D(s, k, t, r, q, vol);
        return s * Math.Exp(-q * t) * Pdf(d1) * Math.Sqrt(t);
    }
}

public readonly record struct Greeks(
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    double Rho);
