# M4 gate: the EMA cross strategy does not survive validation

Measured 2026-09-11 against BTCUSDT perpetual, hourly bars, 2021-01-01 to 2025-12-31.
43,824 bars and 5,388 funding settlements, downloaded from Binance Vision and verified to contain no
gaps, no duplicates and no malformed bars.

## Verdict

**Fails. The strategy should be replaced, not tuned.** No exchange connector should be written for it.

## Single pass over the whole period

| Slippage | Final equity | Return | Max drawdown | Sharpe | Profit factor |
| --- | --- | --- | --- | --- | --- |
| none | 11,602.50 | +16.02% | 22.13% | 0.29 | 1.06 |
| medium | 9,531.00 | -4.69% | 27.85% | 0.00 | 0.98 |
| harsh | 6,340.80 | -36.59% | 44.00% | -0.60 | 0.85 |

692 trades, win rate 22%, 79% of the time in the market. At medium slippage the run pays 2,269 in
fees and 471 in funding on a 10,000 account. The gross edge exists; execution costs consume all of it.

## Walk-forward, 180-day training and 60-day test windows

27 folds, medium slippage, parameters chosen on each training window and measured on the window
that followed.

| Measure | Value |
| --- | --- |
| Out-of-sample equity | 10,000 to 6,111 |
| Out-of-sample return | -38.89% |
| Max drawdown | 44.17% |
| Profitable folds | 9 of 27 |
| Parameter changes | 20 of 26 rolls |
| Fees and funding | 2,088 |

Training windows returned well over +10% on average. The windows that followed them averaged
slightly negative. That gap is the overfitting, measured rather than argued.

The parameter instability is the clearest signal: the training window chose different parameters on
20 of 26 rolls. A real edge keeps selecting roughly the same settings as the window advances. One
selected in and immediately out is fitting noise.

## The bug this exercise found

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

## What would be worth trying next

In rough order of expected value.

1. **Trade a slower timeframe.** 692 trades in five years on hourly bars pays taker fees twice per
   reversal. The same logic on 4-hour or daily bars would cut turnover several-fold, and fees are
   currently the single largest cost.
2. **Stop reversing directly.** Every cross closes and reopens in one bar, paying two taker fees.
   Going flat and requiring a fresh entry condition would drop a large share of the trade count.
3. **Use funding as a signal, not just a cost.** The data is already downloaded. Extreme positive
   funding marks crowded longs, which is information, not merely a bill.
4. **Post-only entries.** Maker rebates instead of taker fees change the cost structure entirely,
   at the price of missed fills that the backtester would need to model honestly.
5. **Accept that trend-following on a single liquid perpetual is a heavily mined seam** and consider
   whether a different family of strategy is a better use of the infrastructure now in place.

The infrastructure is not wasted. The data pipeline, the backtester, the risk gate and the
walk-forward validator are strategy-agnostic. Only the contents of `Core/Strategy` need to change.
