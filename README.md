# SolastaBot

Research tooling for cryptocurrency perpetual futures and Solana token launches, in .NET 10.

**Status: research; neither sleeve has passed its trading gate.** The perp data pipeline, the backtester and the walk-forward
validator are built, tested and verified against five years of real Binance data. Two strategies
have been measured against the gate and neither has passed. Funded trading is not implemented.

The Solana collector records launch price paths. The new `chain replay` command tests entry and
exit rules on copies of those files, retaining unknown exits and explicit cost assumptions.
See [docs/chain-replay.md](docs/chain-replay.md) for usage and measurement limits, and
[docs/chain-gate.md](docs/chain-gate.md) for the earlier research.

The host now runs a live-data Solana paper trader: launch selection, queued entries, simulated
buy/sell execution, position management, separate allocated cash and restart recovery.
See [docs/chain-trading.md](docs/chain-trading.md) for startup and controls. Funded-wallet execution
and a trained prediction model remain unimplemented.

- [docs/m4-gate.md](docs/m4-gate.md) — an hourly EMA cross. Rejected: turnover ate a thin edge.
- [docs/trend-band-gate.md](docs/trend-band-gate.md) — a four-hourly band entry that never reverses
  directly. Rejected: it cut costs from 20% of the account to 5% and the holdout year still lost
  money before fees and funding at every slippage assumption, so there was no edge to protect.

## Layout

| Project | Purpose | Does I/O |
| --- | --- | --- |
| `SolastaBot.Core` | Domain, indicators, strategy, risk, execution policy, backtester, walk-forward | No |
| `SolastaBot.Chain` | Solana strategy, paper execution, allocation limits and replay | No |
| `SolastaBot.ChainCollector` | Records token launches and curve observations | Yes |
| `SolastaBot.Data` | Binance Vision downloads, SQLite store, integrity checks | Yes |
| `SolastaBot.Exchange` | Public Solana launch/curve feed; perp adapter pending | Yes |
| `SolastaBot.Host` | Live-data Solana paper trading worker | Yes |
| `SolastaBot.Cli` | `solasta` command line | Yes |
| `SolastaBot.Tests` | xUnit v3, grouped by feature | Yes |

`SolastaBot.Core` references nothing outside the base class library. It computes its own indicators
so that the live loop and the backtester can share one warm-up path.

## Build and test

```powershell
dotnet build SolastaBot.slnx -c Release
dotnet run --project SolastaBot.Tests -c Release
```

## Commands

```powershell
# Download monthly bars and funding rates. Free, no API key, every file checksum-verified.
dotnet run --project SolastaBot.Cli -c Release -- data pull --symbol BTCUSDT --interval 1h --from 2021-01 --to 2025-12

# Report gaps, duplicates and malformed bars in the local store.
dotnet run --project SolastaBot.Cli -c Release -- data check --symbol BTCUSDT --interval 1h --from 2021-01 --to 2025-12

# Single-pass backtest. Refuses to run over a series with gaps.
# --strategy takes trend-band (default) or ema-cross; both rejected strategies stay runnable so the
# numbers in docs/ can be reproduced.
dotnet run --project SolastaBot.Cli -c Release -- backtest --strategy trend-band --interval 4h --from 2021-01 --to 2025-12 --slippage harsh

# Choose parameters on past data, measure them on the data that came next.
# --fixed rolls one frozen parameter set through the folds instead of searching a grid.
dotnet run --project SolastaBot.Cli -c Release -- walk-forward --strategy trend-band --interval 4h --from 2021-01 --to 2025-12 --slippage medium

dotnet run --project SolastaBot.Cli -c Release -- chain replay --data data/chain --settings examples/chain-replay.json --output artifacts/chain-replay.json --compare-delays
```

Downloaded data lands in `data/`, which is ignored by git.

## The four rules the design rests on

1. **The strategy is a pure function** of the closed bars and the current position. No clock, no I/O,
   no randomness. A strategy that cannot reach the wall clock cannot behave differently live than it
   did in the backtest. `BarSequencedStrategy` throws if a bar arrives twice or out of order, because
   a double-advanced indicator corrupts every reading after it without any visible symptom.
2. **Closed bars only, filled at the next bar's open.** Never at the close of the bar that produced
   the signal. This is asserted directly by a test where the signal bar closes at 100 and the next
   opens at 150.
3. **Risk can only veto or shrink.** `RiskGate` has no path that increases exposure, so a bug in a
   strategy cannot become a bug in position size. A parameter sweep asserts that liquidation always
   sits further from entry than the protective stop.
4. **The exchange is the source of truth** on startup and reconnect. This one is not yet exercised,
   since no connector exists.

Position lifecycle rules that both the backtester and any future live loop must share live in
`Core/Execution/ExecutionPolicy.cs`, not in the engine. A rule that exists in only one of them makes
the backtest stop describing live behaviour.

## What is deliberately not here

No funded-wallet router or live perp execution connector is implemented. The Solana worker uses
public market data and a paper router. Funded execution remains behind strategy validation and
transaction/restart checks.
