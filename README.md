# Options Analytics Service (.NET / C#)

A real-time equity options analytics service: it ingests NBBO quotes off a UDP
feed, solves implied volatility, computes the full set of risk factors, fits an
arbitrage-screened volatility surface per expiry, streams results to clients over
REST and WebSocket, and persists end-of-session snapshots to MySQL.

Built on .NET 10 / ASP.NET Core.

```
        UDP quote feed                 REST  /api/analytics
              |                              /api/surface
              v                              /api/feed/stats
      +---------------+                      /api/history
      | QuoteCodec    |  56-byte binary      /api/close
      | gap detection |                        ^
      +-------+-------+                        |
              |                          +-----+------+
              v                          |  Minimal   |
      +---------------+   TICK           |    API     |
      | ImpliedVol    |----------------> +-----+------+
      | Newton +      |                        |
      | bisection     |   LOOP           +-----v------+     WebSocket
      +-------+-------+----------------> | Live hub   |---> /ws/analytics
              |                          | bounded    |
              v                          | queues     |
      +---------------+                  +------------+
      | Surface fit   |   CLOSE
      | + arbitrage   |----------------> MySQL (close_analytics, close_surface)
      +---------------+
```

Analytics are recomputed on three cadences: **TICK** when top-of-book changes,
**LOOP** on a fixed timer, and **CLOSE** as the persisted end-of-session snapshot.

## Running it

```bash
# 1. Load the schema (MySQL 8+)
mysql -u root < sql/schema.sql

# 2. Start the service
dotnet run -c Release --project src/SpiderRock.Analytics

# 3. Publish quotes into the feed
pwsh ops/Send-Quotes.ps1 -Underlying SPY -Spot 500 -Count 400

# 4. Read the analytics
curl "http://localhost:5080/api/analytics?underlying=SPY"
curl "http://localhost:5080/api/surface/SPY?refit=true"
curl -X POST "http://localhost:5080/api/close"
```

## Measured performance

Release build, Apple M-series, 8 logical cores, server GC:

| Stage | Throughput | p50 | p99 |
|---|---|---|---|
| Quote decode | 8.47M/sec | 83 ns | 125 ns |
| Implied vol + 5 Greeks | 958K/sec | 1.54 us | 6.54 us |
| Surface refit (1,518 contracts, 6 expiries) | — | 0.57 ms | — |

## Design notes

### The implied vol solver terminates on vol, not on price

Newton-Raphson converges in a few iterations near the money, but its step is
proportional to `1/vega`, and vega collapses toward zero in the wings. The solver
keeps Newton on the fast path while maintaining a bracket, and hands off to
bisection the moment a Newton step tries to leave it.

The subtler issue is the *termination criterion*. Stopping on an absolute pricing
error looks reasonable and is wrong for exactly the contracts that need the most
care: a deep out-of-the-money option can be worth 2.3e-7 with a vega of 1e-6, so
a price tolerance of 1e-8 is several percent of the option's value and lets the
solver stop while implied vol is still off by 8e-4. Bisecting on the width of the
volatility bracket bounds the answer directly, no matter how insensitive price is
to vol. This was caught by a convergence sweep over a 300+ point grid, which is
now a regression test.

### The normal CDF is deliberately expensive

The usual Abramowitz & Stegun 7.1.26 erf approximation is accurate to ~1.5e-7.
Since the solver *inverts* this function, that error caps recovered vol accuracy
at roughly 8e-4 vol points, which is wider than the edge on a tight options
market. The Hart rational approximation used here costs more per call and is
accurate to ~1e-15. Greeks are validated against central finite differences of
the pricer, which fails immediately if this precision is given up.

### Unrecoverable inputs are reported, not guessed

A deep in-the-money short-dated call can have a price that is bit-for-bit equal
to its arbitrage lower bound in double precision: its time value has vanished
into rounding. No implied vol exists to be recovered. The solver returns a status
and `NaN` rather than a plausible-looking number, because a fabricated vol would
be silently fitted into the surface and corrupt every strike near it.

### A slow client cannot slow the feed

Each WebSocket subscriber owns a bounded queue. When it fills, the oldest pending
message is dropped and counted rather than blocking the publisher. For market
data this is the right trade: a client that has fallen behind wants the newest
analytic, not a backlog of stale ones.

### Bad packets must never stop the socket

The decoder returns `false` on malformed input instead of throwing, because an
exception on the receive path would stop the loop and take the whole feed down.
Verified against 20,000 random-byte datagrams in the test suite, and against
1,050 malformed datagrams sent at the running service, which produced 1,050
counted decode failures and zero exceptions.

## Tests

59 xUnit tests, all passing:

```bash
dotnet test tests/SpiderRock.Analytics.Tests
```

- **Pricing** — put-call parity across the strike grid, monotonicity in vol,
  intrinsic value at expiry, no-arbitrage bounds
- **Greeks** — all five validated against central finite differences of the
  pricer; call/put gamma and vega equality; delta sign and range
- **Solver** — round-trip vol recovery, a 300+ point convergence grid asserting
  zero failures and worst-case error under 1e-5, vega-collapse fallback,
  arbitrage-bound rejection, iteration budget
- **Codec** — round trip, truncated datagrams, 20,000 random-byte packets
  asserting no throw, invalid dates and option rights
- **Feed** — sequence gap detection and late-arrival classification
- **Engine** — crossed/locked market rejection, spread filtering, UDP reorder
  protection, surface fit accuracy and arbitrage screening

## Verified end to end

Against a live instance with MySQL 9.6: 186 quotes ingested with zero decode
failures from an independent Python publisher (cross-language wire format check),
call and put at the same strike independently recovering the same implied vol to
9 decimal places, surface fit recovering the generating curvature with RMS error
9e-11, 186 rows persisted and read back through MySQL, and an idempotent re-run
of the close job leaving 186 rows rather than 372.

## Layout

```
src/SpiderRock.Analytics/
  Model/      option key, quote, and analytic contracts
  Pricing/    Black-Scholes, Greeks, implied vol solver
  Surface/    per-expiry smile fit and arbitrage screening
  Feed/       UDP handler, binary codec, gap statistics
  Api/        WebSocket fan-out hub
  Storage/    MySQL close-snapshot repository
  Program.cs  minimal API endpoints and host wiring
tests/        xUnit suite
bench/        throughput and latency harness
ops/          PowerShell quote publisher and deployment smoke test
sql/          schema
```
