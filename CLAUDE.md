# SolastaBot

Perpetual futures research and trading, .NET 10 / C# 14. Read `README.md` for layout and commands,
and `docs/m4-gate.md` for what has already been measured and rejected.

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
