# Trend-band gate: cheaper execution was not enough

Measured 2026-09-12 against BTCUSDT perpetual, four-hourly bars, downloaded from Binance Vision and
verified to contain no gaps, no duplicates and no malformed bars: 10,956 bars and 5,478 funding
settlements from 2021-01-01 to 2025-12-31.

In-sample is 2021-01 to 2024-12. The holdout is 2025 and was run once, at the end, after the
strategy was frozen.

**Every figure below was re-measured after two defects found during this exercise were fixed.** The
`ExecutionPolicy` latch fix changes behaviour, so the whole document was re-run rather than patched;
nothing here predates the fix. Both defects are described at the end.

## Verdict

**Fails.** The strategy fixes the problem it was built to fix and then fails for a different reason.
Execution cost is no longer what stands between this strategy family and a profit; the absence of a
gross edge is. No exchange connector should be written for it.

Two of the six gate rows fail, and the holdout is the one that decides it.

| Test | Bar | Measured | |
| --- | --- | --- | --- |
| Walk-forward out-of-sample return, medium slippage | positive | **+9.26%** | pass |
| Walk-forward out-of-sample return, harsh slippage | positive | **+5.55%** | pass |
| Profitable folds | more than half | **12 of 21** | pass |
| Parameter changes across rolls | fewer than half | **14 of 20** medium, **13 of 20** harsh | **fail** |
| Out-of-sample trades | at least 100 | **107** medium, **100** harsh | pass |
| Holdout year, harsh slippage | positive | **-3.02%** | **fail** |

## What the strategy is

`TrendBandStrategy` takes the two ideas ranked first and second in `docs/m4-gate.md`.

**A slower timeframe.** Four-hourly bars rather than hourly.

**No direct reversals.** Entry needs the fast average a full band beyond the slow one; exit happens
the moment they cross back. The gap between those two thresholds is a dead zone the strategy sits
flat in. Exits are resolved before entries, so a bar that ends one side and would begin the other
goes flat instead of reversing, and the opposing entry must wait for a later bar and a real move.
The separation is measured as a fraction of the slow average, so one parameter means the same thing
at 20,000 and at 100,000.

Everything else is inherited unchanged from the rejected strategy so the comparison is honest: ATR
period 14, ADX period 14 with a floor of 20 gating entries only, a protective stop at 2.5 ATR, 0.5%
of equity risked per trade and a hard leverage cap of 3.

The frozen configuration, declared before the holdout was touched: **21/55, entry band 50 bp**.

## How to read the cost figures below

`BacktestReport` prints a row labelled **Before fees/funding**, which is `BacktestMetrics.GrossPnl`.
It is gross of fees and funding but **net of slippage**, because slippage moves the fill price and is
therefore already inside every trade's PnL. So a negative figure means the trades lost money before
fees and funding were taken, *at that run's slippage assumption*.

To state the edge with no execution assumption at all, the same run has to be repeated at
`--slippage none`. Both are quoted wherever the distinction decides anything, because it does decide
something here, and in the opposite direction for the two strategies.

## The cost problem is solved

Single pass over the in-sample period, 2021-01 to 2024-12.

| Slippage | Final equity | Return | Max drawdown | Sharpe | Profit factor | Before fees/funding |
| --- | --- | --- | --- | --- | --- | --- |
| none | 11,873.80 | +18.74% | 7.18% | 0.71 | 1.46 | +2,401.98 |
| low | 11,844.81 | +18.45% | 7.22% | 0.70 | 1.45 | +2,372.88 |
| medium | 11,727.02 | +17.27% | 7.35% | 0.66 | 1.41 | +2,253.96 |
| harsh | 11,420.71 | +14.21% | 7.78% | 0.55 | 1.33 | +1,939.70 |

118 trades, win rate 23%, 77% of the time in the market, no liquidations.

The row that matters is the spread between the first and the last: 4.5 points across the whole range
of execution assumptions. The rejected strategy spread 58.1 points across the same window and the
same range, which is another way of saying its result was a statement about the slippage model
rather than about the market.

Costs collapsed. Over the same in-sample window at the same harsh slippage, the rejected strategy
paid 1,583 in fees and 442 in funding, or 20.3% of the starting balance; this one pays 149 and 372,
or 5.2%. Fees fell by a factor of ten while funding barely moved, so funding is now the larger of the
two costs. That inverts the picture in `docs/m4-gate.md`, and it is the direct consequence of holding
a position for days instead of hours.

### Where the improvement came from

Each row is the same in-sample window at harsh slippage, so the columns are comparable.

| Configuration | Return | Trades | Fees | Sharpe | Profit factor | Before fees/funding |
| --- | --- | --- | --- | --- | --- | --- |
| ema-cross, 1 hour (the rejected strategy) | -15.65% | 613 | 1,583 | -0.22 | 0.93 | +459.60 |
| ema-cross, 4 hour (timeframe alone) | +10.98% | 154 | 199 | 0.41 | 1.21 | +1,655.95 |
| trend-band, 4 hour (timeframe and dead zone) | +14.21% | 118 | 149 | 0.55 | 1.33 | +1,939.70 |

The timeframe does most of the work. Removing the direct reversal is a real but secondary
improvement, worth 3.2 points of return and, more tellingly, an improvement in every risk-adjusted
column rather than only in the total. It earns its place, but it was never going to be the thing
that decided this.

## Walk-forward, 180-day training and 60-day test windows

21 folds over the in-sample period, parameters chosen on each training window from a six-candidate
grid (three speeds crossed with two entry bands, with the stop multiple and the ADX floor fixed
rather than searched) and measured on the window that followed.

| Measure | Medium | Harsh |
| --- | --- | --- |
| Out-of-sample equity | 10,000 to 10,926 | 10,000 to 10,555 |
| Out-of-sample return | +9.26% | +5.55% |
| Max drawdown | 8.93% | 9.36% |
| Profitable folds | 12 of 21 | 12 of 21 |
| Parameter changes | 14 of 20 rolls | 13 of 20 rolls |
| Out-of-sample trades | 107 | 100 |
| Fees and funding | 329 | 307 |

Training windows averaged +3.2% at medium slippage and the windows that followed averaged +0.5%.
That gap is far narrower than the rejected strategy's, where training returned well over +10% on
average and the windows that followed averaged slightly negative. The out-of-sample estimate is
positive rather than catastrophic. It is still not a pass.

### The parameter instability is real, not an artefact of a wide grid

Six candidates, and the training window still chose differently on 14 of 20 rolls. The rejected
strategy managed 23 of 26 from a grid of fifty-four. Shrinking the grid by a factor of nine barely
moved the churn rate, which rules out the comfortable explanation that the old grid was simply too
wide.

The sweep below says why. Each cell is a single pass over the in-sample window at harsh slippage.

| Speed | 25 bp | 50 bp | 75 bp | 100 bp | 150 bp |
| --- | --- | --- | --- | --- | --- |
| 13/34 | +2.92% | -4.96% | -2.15% | | |
| 21/55 | +11.59% | +14.21% | +13.91% | +16.02% | +14.19% |
| 34/89 | +24.78% | +19.52% | +15.67% | | |

The speed is the load-bearing parameter and the band is not. Across 21/55 the band can be moved from
25 bp to 150 bp and the return stays in a range of 4.4 points, but dropping the speed to 13/34 turns
every band negative or near zero. Two of the six grid candidates are 13/34, so a third of the grid
has nothing in it, and a training window that picks one of them is picking noise. That is the churn:
not a grid too large to be meaningful, but a grid containing candidates that do not work, selected by
a return-over-drawdown score that a ten-trade window cannot measure reliably.

Narrowing the grid to the speeds that worked would fix the row and prove nothing, because the
narrowing would have been done with in-sample results in hand.

### Freezing the parameters trades one failing row for another

The strongest form of the small-grid defence is no grid. Rolling the frozen 21/55 band 50 bp set
through the same 21 folds:

| Measure | Medium | Harsh |
| --- | --- | --- |
| Out-of-sample return | +14.48% | +11.58% |
| Max drawdown | 7.73% | 8.13% |
| Profitable folds | 11 of 21 | 11 of 21 |
| Parameter changes | 0 of 20 | 0 of 20 |
| Out-of-sample trades | 97 | 97 |

This is the more honest of the two walk-forward results, because nothing is chosen and so nothing
can be chosen by luck. It returns more than the searched configuration at both slippage levels,
which is the plainest statement available that the grid search was subtracting value rather than
adding it. It buys the parameter-stability row by construction and then fails the trade-count row
that the searched configuration passes, 97 against a bar of 100.

So neither configuration passes all six rows, and the two fail different rows. Widening the test
window would lift the frozen configuration's trade count over the bar and was not done, because
choosing the measurement that clears the bar is the same mistake as choosing the parameters that
clear it. It would not have mattered: the holdout fails in both.

## The holdout, run once

2025-01-01 to 2025-12-31 at harsh slippage, frozen configuration, one run. This is the verdict.

| Measure | Value |
| --- | --- |
| Final equity | 9,697.52 |
| Return | **-3.02%** |
| Max drawdown | 5.48% |
| Sharpe | -0.51 |
| Profit factor | 0.69 |
| Trades | 28 |
| Win rate | 21% |
| Before fees/funding | **-250.41** |
| Fees paid | 43.82 |
| Funding paid | 8.25 |

Costs consumed 0.52% of the starting balance. The strategy lost 3.02% while paying essentially
nothing to trade, and the report says so directly: *the trades lost money before fees and funding, so
trading less often will not rescue this.*

Repeating the same year at `--slippage none`, as a diagnostic rather than a second verdict, closes
the remaining gap. With no execution assumption at all the year still returns **-1.79%**, and the
figure before fees and funding is still negative at **-126.73**. The sign does not flip anywhere
between zero slippage and harsh.

That distinction is the whole finding, and it separates this strategy from the one it replaced:

| Run | Before fees/funding at `none` | at `harsh` | Diagnosis |
| --- | --- | --- | --- |
| ema-cross 1h, 2021-2025 | **+5,432.94** | **-1,628.55** | sign flips; cost and slippage took a real edge |
| trend-band 4h, 2025 holdout | **-126.73** | **-250.41** | negative throughout; there is no edge to take |

The rejected strategy had something that execution destroyed. This one has nothing for execution to
destroy. Collapsing both into "no gross edge" would lose the only distinction that tells you where
to look next.

The holdout loss is evenly spread rather than concentrated in a disaster: negative in all four
quarters, 6 wins against 22 losses, best trade +212 and worst -61, and both sides losing (shorts
-160, longs -143). The trend-follower's payoff shape survived — small losses, occasional large
wins — but 2025 did not hand it enough large wins to pay for them. 14 of the 28 exits were stops.

## What this measurement is worth

The rejected strategy failed because its costs ate a thin edge. This one fails with its costs
removed, which is a more useful result than it looks. It converts an open question into a closed
one: on a single liquid perpetual, an exponential-moving-average trend filter on four-hourly bars
does not have enough gross edge to be worth trading, whatever is done about execution. Two more
places to look for the problem have been eliminated rather than merely suspected.

The in-sample record is thinner than the headline suggests. Net trade profit at medium slippage was
+41 in 2021, **-51 in 2022**, +1,275 in 2023 and +476 in 2024, on roughly 30 trades a year. One year
in four carries 74% of the total and another is negative. A strategy whose expectancy depends that
heavily on which years you measured is a strategy whose expectancy you have not measured.

## The two defects found on the way, both now fixed

Neither was introduced by this strategy. Both were found by measuring it, and both are the reason
every figure above was re-run.

**A latch in `ExecutionPolicy`.** The block on the direction that was just stopped out released only
when the *opposite* direction was wanted. For the rejected strategy that released constantly, because
its target was Long or Short on almost every bar. For any strategy with a flat state it was much
stickier, and under the `--long-only` flag, which the CLI supports, it could never release at all: a
single stop-out would suppress every long for the remainder of a run, and the run would report a flat
equity curve rather than an error. This is the same shape as the consecutive-loss halt documented in
`docs/m4-gate.md`, and it violated the rule in `CLAUDE.md` that every block must have an explicit
release path with a test that proves it releases. The block now releases on any target other than
the blocked side, and a test asserts a long-only strategy can always trade again after a stop-out.

This is the fix that moved the numbers. It lets the strategy re-enter after a stop-out once its own
signal has stood down, which raised the in-sample trade count from 103 to 118 and the walk-forward
out-of-sample count from 91 to 100 at harsh. It did not flatter the results: in-sample return at
harsh fell from +18.79% to +14.21%, and the holdout from -1.90% to -3.02%. The verdict was already
fail and remains fail, for the same reason and by a wider margin.

**A mislabel in `BacktestMetrics`.** Rows printed as "Gross profit" and "Gross loss" were built by
summing `TradeRecord.NetPnl`, so they were net of fees and funding, not gross of them. That is
precisely backwards for the question this gate turned on. They are now `NetProfit` and `NetLoss`, and
a new `GrossPnl` field sums `TradeRecord.GrossPnl` so the figure before fees and funding is a
first-class metric rather than something to be recovered from a ledger by hand. The display label is
"Before fees/funding" rather than "before costs", because it remains net of slippage, as the section
above explains.

## What would be worth trying next

The first two entries on the previous list are now spent. What remains, reordered by what this
measurement taught.

1. **Stop trying to fix trend following on BTC perpetual.** Two strategies in this family have now
   been measured properly, and the second one removed the excuse the first one had. Entry 5 on the
   previous list has been promoted to entry 1 by evidence. The infrastructure is strategy-agnostic;
   spend it on a different family rather than on a third variation of this one.
2. **Use funding as a signal, not just a cost.** Still the best of the untried ideas, and now better
   motivated: funding is the dominant cost of a multi-day holding period, at 372 against 149 of fees
   in-sample, and the data is already downloaded. Extreme positive funding marks crowded longs, which
   is information. This is a different edge rather than a cheaper version of the same one, which is
   what the evidence says is needed.
3. **Carry or basis rather than direction.** Perpetual against quarterly, or funding capture with a
   delta hedge. The payoff is not a trend payoff and so is not mined by the same crowd, and it uses
   the perpetual data already stored.
4. **Cross-sectional rather than single-instrument.** Ranking twenty perpetuals and holding the
   extremes is a different bet from timing one. It needs the data pipeline pointed at more symbols,
   which is a day's work against a pipeline that already exists, and it multiplies the trade count
   that one instrument could only just reach.
5. **Post-only entries.** Unchanged in position and now clearly not urgent. The holdout year paid 44
   in fees on a year that lost 127 before fees and funding even at zero slippage, so the entire
   maker-taker spread is smaller than the hole it would be filling. Cost engineering cannot rescue a
   strategy with no edge, and this measurement is what demonstrates that.

The data pipeline, the backtester, the risk gate and the walk-forward validator all held up under a
second strategy. The only shared code that had to change was the two defects above, both of which
predated this strategy and neither of which was a design fault in the layering.
