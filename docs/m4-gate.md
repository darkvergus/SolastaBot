# M4 gate: the EMA cross strategy does not survive validation

Measured against BTCUSDT perpetual, hourly bars, 2021-01-01 to 2025-12-31. 43,824 bars and 5,478
funding settlements, downloaded from Binance Vision and verified to contain no gaps, no duplicates
and no malformed bars.

Figures re-measured 2026-09-12 after a latching defect was fixed in `ExecutionPolicy`. The
re-entry block used to release only on the opposite direction, which held it longer than intended
and suppressed roughly a hundred trades. See the correction at the end. The verdict is unchanged.

## Verdict

**Fails. The strategy should be replaced, not tuned.** No exchange connector should be written for it.

## Single pass over the whole period

| Slippage | Final equity | Return | Max drawdown | Sharpe | Profit factor |
| --- | --- | --- | --- | --- | --- |
| none | 12,002.60 | +20.03% | 23.51% | 0.33 | 1.07 |
| medium | 9,540.03 | -4.60% | 27.05% | 0.00 | 0.98 |
| harsh | 5,898.24 | -41.02% | 47.04% | -0.67 | 0.84 |

790 trades, win rate 22.66%, 88% of the time in the market. At medium slippage the run pays 2,557 in
fees and 518 in funding on a 10,000 account, which is 30.75% of the starting balance.

The trades make money before fees and funding at every profile but the harshest.

| Slippage | Before fees and funding |
| --- | --- |
| none | +5,432.94 |
| medium | +2,611.40 |
| harsh | -1,628.55 |

So this strategy has a thin edge that execution cost consumes, rather than no edge at all. That
distinction matters, and it is the one the successor strategy was built to exploit. It did not
survive either, for a different reason: see `trend-band-gate.md`.

## Walk-forward, 180-day training and 60-day test windows

27 folds, medium slippage, parameters chosen on each training window and measured on the window
that followed.

| Measure | Value |
| --- | --- |
| Out-of-sample equity | 10,000 to 7,522 |
| Out-of-sample return | -24.78% |
| Max drawdown | 33.75% |
| Trades | 797 |
| Profitable folds | 11 of 27 |
| Parameter changes | 23 of 26 rolls |
| Fees and funding | 2,564 |

Training windows returned well over +10% on average. The windows that followed them averaged
slightly negative. That gap is the overfitting, measured rather than argued.

The parameter instability is the clearest signal: the training window chose different parameters on
23 of 26 rolls. A real edge keeps selecting roughly the same settings as the window advances. One
selected in and immediately out is fitting noise.

## The bugs this exercise found

Three, and all three shared a shape: the run completed, the numbers looked plausible, and nothing
failed.

### A halt that could not be lifted

The first five-year run reported +5.80% at medium slippage and 40 trades. All 40 trades fell in the
first four months.

`RiskLedger` halted trading after four consecutive losses, and the counter cleared only on a winning
trade. A halt that only a win can lift can never be lifted, because the halt prevents the trade that
would lift it. Four losses in a row in May 2021 switched the strategy off for the remaining four and
a half years, and the run still reported a plausible profit over the whole period, with a flattering
3.73% maximum drawdown produced by not trading.

The fix makes both halts mean the same thing, which is a stop for the remainder of the UTC day.
Two regression tests now cover it: one asserting the halt lifts at the day boundary, and one
asserting a long backtest is still trading in its final quarter. The second is the more valuable of
the two, because the failure looks like a flat equity curve rather than an error.

The lesson generalises. A risk control that can latch permanently produces results that look like a
cautious strategy rather than a broken one.

### A second latch, in the same codebase, after the lesson was written down

Found on 2026-09-12 while measuring the successor strategy. `ExecutionPolicy` refuses the direction
it was just stopped out of, and released that block only when the opposite direction was wanted.
A long-only configuration never wants the opposite direction, so under the supported `--long-only`
flag the block was taken once and held forever, and a single stop-out ended the run silently.

This was written after the halt above had been diagnosed, documented, and turned into a rule in
`CLAUDE.md`. Knowing the failure mode was not enough to avoid repeating it, which is the argument
for the reviewer checking every piece of retained state for a release path rather than relying on
whoever writes the next one to remember.

The block now releases on any target other than the blocked side. That is a behaviour change, so
every figure above was re-measured under the fixed code; the old numbers were 692 trades and -38.89%
out of sample. Two tests cover it, one of which drives the policy the way a long-only strategy does.

### Metrics that lied about what they summed

`BacktestMetrics` reported fields named `GrossProfit` and `GrossLoss` that summed `NetPnl`, so both
were net of fees and funding while claiming to be gross. This mattered precisely when asking whether
costs caused a loss, which is the question that decided the successor strategy.

They are now `NetProfit` and `NetLoss`, and a new `GrossPnl` field sums the pre-fee figure so the
question has a first-class answer. It is gross of fees and funding but net of slippage, since
slippage is applied to the fill price, and the report and doc comment both say so.

## What would be worth trying next

In rough order of expected value.

1. ~~**Trade a slower timeframe.**~~ Done. Four-hourly bars cut fees from 1,437 to 185 and turned
   the in-sample result positive. It was not enough; see `trend-band-gate.md`.
2. ~~**Stop reversing directly.**~~ Done, in the same successor. Worth about three points and better
   risk-adjusted columns, so it earned its place, but it was not the missing piece either.
3. **Use funding as a signal, not just a cost.** The data is already downloaded. Extreme positive
   funding marks crowded longs, which is information, not merely a bill.
4. **Post-only entries.** Maker rebates instead of taker fees change the cost structure entirely,
   at the price of missed fills that the backtester would need to model honestly.
5. **Accept that trend-following on a single liquid perpetual is a heavily mined seam** and consider
   whether a different family of strategy is a better use of the infrastructure now in place.

The infrastructure is not wasted. The data pipeline, the backtester, the risk gate and the
walk-forward validator are strategy-agnostic. Only the contents of `Core/Strategy` need to change.
