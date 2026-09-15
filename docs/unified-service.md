# Unified paper portfolio

Start one process for collection, trading, research and the browser dashboard:

```powershell
dotnet run --project SolastaBot.Host -c Release -- serve --settings examples/unified-paper.json --state data/unified
```

Open http://127.0.0.1:5080. Four instances run concurrently: Solana launch, Solana continuation,
Binance hourly EMA and Binance four-hour trend band. All balances and fills are paper. No wallet or
exchange credentials are required. Neither Binance baseline passed historical validation. Legacy
Paper, Simulate and Devnet entry points retain their existing journals and behaviour.

When using `dotnet run`, relative settings and state paths resolve from the repository root.
Published executables resolve relative paths from their working directory; the systemd unit uses absolute paths.

## Controls and accounting

The dashboard controls entry pauses, flatten requests, available-budget transfers, profit splits and
versioned rule changes. Pausing keeps exits active. Global resume does not override an individual
pause or daily risk halt. Rule changes require no open positions or pending entries. Dashboard settings
persist in the journal; editing startup settings cannot silently replace an existing portfolio.
Operational settings such as RPC URLs, tracking capacity and dashboard port can change on restart
without resetting strategy state. Initial strategy identities and capital remain checked.

Each strategy owns quantities, cash, reservations and P/L. Solana fills use subsequent reserves with
fees, impact, slippage, network and setup costs. Managed strategies consume available liquidity in
evaluation order. Partial sales allocate cost proportionally and preserve the trailing high. Missing
sell liquidity retains the position, marks it zero and records an unresolved exit. Triggers cannot
guarantee their requested fill price.

Binance uses Core indicators and RiskGate, one-times isolated paper exposure, instrument filters and
subsequent mark observations for fills. Published funding rates apply to exposure at settlement time,
including positions closed before publication. This mark-driven simulation does not reconstruct every
intrabar path of the historical OHLC backtester. Liquidation currently uses a single 0.5% maintenance
assumption for bounded paper positions, not a production venue's complete tiered liquidation engine.
The market socket uses `/market/stream`, including the combined-message envelope, as documented in
[Binance's market stream reference](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market).

Daily-loss and consecutive-loss halts reset at UTC midnight. Reserved funds cannot transfer. Managed
exposure is capped at 50% per asset in addition to strategy limits. Trials have independent virtual
capital. A single instrument is capped at 25% of managed asset equity; stale managed positions veto
new exposure in the same asset. Trials do not contribute money or results to the managed accounts.

Profit splits apply to settled net profits above recovered losses. Incoming allocations are
contributions, not trading profits. Binance distributions wait until funding publication covers closed
exposures. Same-currency allocations move automatically; cross-currency shares remain source-currency
liabilities until a uniquely referenced paper conversion records the net received amount. No actual
withdrawal, bridge, currency swap or exchange transfer is performed.

## Collection and research

One adaptive pump.fun HTTP limiter is shared. Confirmed Solana program logs trigger discovery and the
newest-token HTTP feed supplies fallback coverage. Reconnect recovery is capped at 100 transaction
reads; exceeding it records incomplete coverage. A shared read-only RPC limiter allows two requests
per second with failure backoff. Supported migrations use the canonical PumpSwap decoder for reserves
and dynamic fees.

Four tracked tokens is the conservative public-endpoint default; open positions take priority. Raising
capacity can make continuation sampling too sparse. One-minute observation bars need samples within
ten seconds of both boundaries and no internal gap over ten seconds. Breakouts need ten consecutive
preceding complete bars. These sampled reserve-price bars are not full trade-by-trade candles.

Discoveries record selection probability and admission. Capacity exclusions have zero probability:
the dataset cannot estimate returns for the unobserved population. Observations and errors are stored
alongside the portfolio. Legacy collector files remain separate; sparse historical paths are not
treated as complete bars.

Daily research tests up to three fixed alternatives plus the current configuration on 28 training days, selects on training equity and tests
the selected candidate and unchanged baseline on the next seven days. Selection never uses test results. Solana varies trailing pullback (20/25/30%);
trend-band varies entry band (40/50/60 bps); EMA varies slow period (44/55/66). Reports retain data hashes,
window results and trade counts. Coverage requires usable observations on every day in both windows
and recorded feed errors below 1% of usable observations; incomplete windows cannot start trials.
Individual incomplete bars are excluded regardless of the window error rate. Positive training/test equity,
coverage, at least 20 closed test
trades and no unresolved test positions permit an independent paper trial, with eight trials maximum.
Trials stop entries and request closure after seven days. Completed results remain visible and release
their trial slot; unavailable exits remain unresolved rather than being reported as sales.
This is screening, not statistical proof or a final holdout: rolling test windows overlap. There is no
automatic real-money promotion. New installations report insufficient coverage. Rejected historical
strategy reports remain unchanged.

## Ubuntu package and cutover

```powershell
dotnet publish SolastaBot.Host -c Release -r linux-x64 --self-contained true -o artifacts/unified-linux
```

Use `tools/deploy/solastabot.service`, a dedicated `solasta` user, `/opt/solastabot` for binaries,
`/etc/solastabot/unified-paper.json` for settings and `/var/lib/solastabot/unified` for state. The publish
directory includes the executable and dashboard assets. Back up the whole journal directory after a
clean stop, or use SQLite's backup facilities; copying only a running database can omit WAL changes.

Deployment has not been performed. At a separately scheduled cutover, stop the old collector, preserve
its original output, install the new package/settings/unit, then start the unified service. Check
`/health`, dashboard freshness and `journalctl -u solastabot`. Do not run both collectors against the
same public endpoint budget. Rollback stops this service and restarts the old collector on its original
files. Never import a devnet transaction journal into this paper portfolio.

Access the loopback dashboard through:

```bash
ssh -L 5080:127.0.0.1:5080 your-user@your-server
```

Then open http://127.0.0.1:5080 locally. Host validation, loopback binding and Razor antiforgery protect
the control surface. `/health` confirms process operation, not trading success. For an offline smoke
run, copy settings, set `EnableNetwork` to `false`, and use a new temporary state directory.
