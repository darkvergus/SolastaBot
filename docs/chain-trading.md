# Running the launch trading loop

This page describes Paper mode. For signed transactions, devnet operation and migration routing,
see [Solana execution](chain-execution.md).

The worker now follows the operational sequence: discover a launch, evaluate an entry rule, queue
a buy, obtain a later price, buy, monitor the open position, and sell when its exit rule triggers.
It reads the public live feed and currently sends orders to a paper router. The paper router
simulates fills and changes the session's simulated SOL balance; it does not submit transactions.

## What is implemented

| Capability | State |
| --- | --- |
| Read newly launched tokens | Public pump.fun launch feed, bounded request rate and backoff |
| Read prices for open positions | Individual curve requests, timestamped when received |
| Decide whether to buy | Configurable launch filter, age and reserve checks |
| Buy and sell automatically | Paper execution with fees, slippage and finite allocated cash |
| Manage positions | Position cap, pending cash reservations, time/profit/loss exits |
| Control operation | Status, events, entry halt, resume and flatten commands |
| Recover after restart | SQLite positions, pending entries, balances and previously seen mints |
| Split capital | Separate simulated chain allocation; reserved perp funds are unavailable to chain trades |
| Predict future prices | No trained prediction model; the entry rule is an unvalidated hypothesis |
| Execute funded-wallet trades | Not implemented |
| Trade after curve migration | Not implemented; these positions stay unresolved and block new entries |
| Run the perp trading sleeve live | Not implemented; its existing research tools remain available |

## Start explicitly

The example settings are illustrative, not validated strategy parameters. Starting this worker
makes live HTTP requests and creates a persistent paper session in the selected directory.

```powershell
dotnet run --project SolastaBot.Host -c Release -- --settings examples/chain-paper.json --state data/chain-paper
```

With no arguments, the host prints usage and exits. It is not installed as a background service by
the development changes. The existing Ubuntu collector has not been changed or restarted.

The worker has its own request budget. Running it alongside the collector from the same public IP
would combine their traffic; do not assume their separate limits enforce one shared allowance.
The default is at most 0.8 requests per second for this worker, with a lower rate and a pause after
throttling. This polling feed does not guarantee discovery of every on-chain launch.

## What it does with the example settings

The simulated account begins with 10 SOL. Two SOL are allocated to chain trading, five are reserved
for the future perp sleeve and three remain unallocated. The chain worker sees only its two SOL
balance. It cannot use the other eight SOL when a trade loses money. These are accounting units in
a simulation, not transfers to a blockchain wallet or an exchange.

The entry rule requires an explicitly identified SOL pump curve, a launch at most sixty seconds
old, both Telegram and Twitter metadata, virtual SOL reserves above 31.04 and real SOL reserves
of at least one SOL. Those fields are eligibility criteria, not evidence that a token is genuine
or that its price will rise. The threshold is explicit and fixed for the session; it does not
inherit the collector's evolving median threshold or automatically retune itself.

A qualifying launch queues a 0.01 SOL purchase, plus configured costs. The worker waits for the
entry delay and requests that mint's curve again. It rechecks its controls, cash and position
allowance before a fill. Pending entries reserve cash and count towards the position limit. If an
entry expires or cannot fill, the reservation is released.

At most three positions or pending entries are allowed together. Each entry including costs must
fit within five percent of marked chain equity. No entry occurs while an existing position has
stale observations or unresolved migration.

The worker requests an exit after five minutes, a 100% net gain or a 50% net loss. It obtains a
later price to fill that exit. A price crash after a profit signal can therefore still produce a
loss. If real reserves cannot fund the sale, the position stays open and the worker retries when
new observations arrive. Migration is retained as unresolved instead of manufacturing a curve fill.

A five-percent daily marked-equity loss halts entries and requests exits. The daily halt releases
at the next UTC day. Manual halts persist until resumed. A restart preserves both positions and
the daily risk state; it does not replenish the simulated balance.

## Operate it

Use the same state directory as the running worker:

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain paper status --state data/chain-paper
dotnet run --project SolastaBot.Cli -c Release -- chain paper events --state data/chain-paper
dotnet run --project SolastaBot.Cli -c Release -- chain paper halt --state data/chain-paper
dotnet run --project SolastaBot.Cli -c Release -- chain paper flatten --state data/chain-paper
dotnet run --project SolastaBot.Cli -c Release -- chain paper resume --state data/chain-paper
```

`halt` cancels pending entries and leaves existing positions under their normal management rules.
`flatten` also requests exits for every open position. It is a request, not a claim that everything
has sold; the worker must be running and must obtain usable market data. `resume` clears manual
controls but does not cancel already requested exits or bypass daily risk limits.

Status is the last persisted state. Read its timestamp to distinguish a running worker from a
stopped session. Marked equity is a simulation value based on the most recent usable observations;
it is not a wallet balance or guaranteed liquidation proceeds.

The state directory contains `paper.db`, an exclusive worker lock and optional `HALT`/`FLATTEN`
markers. Positions, pending entries, previously seen mints and decision events commit together in
SQLite. Only one cooperative worker can open the same state directory. Event records retain the
curve observation used for a fill. Repeated feed entries, including after a restart, do not open a
second trade in the same mint.

Restart with the same command and settings to resume the same paper account. A settings mismatch
fails rather than resetting cash or reinterpreting existing positions. Use a new state directory
for a separate experiment. Ctrl+C stops the worker without pretending to sell its positions; they
remain persisted for restart.

## What remains before funded trading

The funded router still needs wallet signing, on-chain account and token checks, fee/account
configuration, transaction simulation, submission, confirmation and reconciliation against actual
holdings after ambiguous submissions and restarts. Pool discovery and PumpSwap execution are needed
to manage positions that migrate. The fixed entry filter still needs evidence of profitability.

The replay tool and this paper worker support those checks. Neither is a trained predictor or an
implemented funded-wallet connector. `Mode: Live` fails at startup and no private key is read.
