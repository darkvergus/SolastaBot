# SolastaBot

Automated trading research, .NET 10 / C# 14. Read `README.md` for layout and commands, and
`docs/m4-gate.md` for what has already been measured and rejected.

## Two sleeves

Capital runs in two independent sleeves, chosen because their payoffs are close to uncorrelated.

- **Perp** trades Binance USD-M perpetual futures on a bar-driven strategy. Many roughly symmetric
  bets, sized fixed-fractionally against a stop. This is what `SolastaBot.Core` models today.
- **Chain** buys token launches on Solana. A lottery payoff: most positions go to zero and a small
  tail pays for them, so sizing is many tiny bets rather than one stopped position. An
  average-true-range stop is meaningless on a token minutes old.

They share the discipline below and almost no code. Resist merging their domains: a `Candle` and a
token launch are not the same object, and forcing one abstraction over both buys nothing.

**Hard walls.** Each sleeve gets a fixed fraction of total capital and cannot reach into the other's
allocation to fund a drawdown. A sleeve that has not passed its gate is allocated nothing, so the
split is meaningful only once a sleeve has earned a share. The global kill switch stops both.

## Build and test

```powershell
dotnet build SolastaBot.slnx -c Release
dotnet run --project SolastaBot.Tests -c Release
```

Tests run through `dotnet run`, not `dotnet test`. Warnings are errors, so a warning fails the build.

## The four rules

These are the invariants the whole design rests on. Breaking one produces a backtest that no longer
predicts live behaviour, which is indistinguishable from a working system until real money is on it.

1. **The strategy is a pure function** of closed bars and the current position. It reads no clock,
   performs no I/O and draws no random numbers. Derive from `BarSequencedStrategy`, which throws when
   a bar arrives twice or out of order.
2. **Closed bars only, filled at the next bar's open.** Never at the close of the bar that produced
   the signal.
3. **Risk only vetoes or shrinks.** `RiskGate` has no path that increases exposure.
4. **The exchange is the source of truth** on startup and on every reconnect. Rebuild internal state
   from live positions and open orders.

Rules about the position lifecycle belong in `Core/Execution/ExecutionPolicy.cs`, which the
backtester and the live loop both drive. A lifecycle rule written into only one of them makes the two
diverge silently.

## Conventions

- `SolastaBot.Core` references nothing outside the base class library. Keep it that way.
- Explicit types everywhere. The `.editorconfig` sets every `var` rule to `error`.
- One public type per file, named for the file.
- `decimal` for every price, quantity and balance. `double` belongs only in statistics such as Sharpe.
- Every `DateTime` is UTC by contract. The data and exchange layers convert on the way in.
- Comments explain why, not what. The existing files set the density.

## Latching

A risk control that can enter a state it cannot leave is the failure mode this codebase has already
been bitten by. A consecutive-loss halt that cleared only on a winning trade switched the bot off for
four and a half years of a five-year backtest, and the run still reported a profit. Give every halt,
block and circuit breaker an explicit release path, and a test that proves it releases.

## Capital ladder

Backtest, then walk-forward, then the untouched holdout period, then paper against live data, then
Binance testnet, then live with minimal size. Each rung is earned by the one before it.

Secrets reach the process through user-secrets in development and environment variables in
production. `data/`, `secrets/` and `*.db` stay out of git.
