# Replaying the collector data

The collector records what happened to token launches. `solasta chain replay` now reads those files
and simulates a fixed buying and selling rule. It does not connect to a wallet or place orders.
The perpetual-futures commands remain separate and unchanged.

## Run it

Use completed copies of `launches.jsonl` and `polls.jsonl` from the same collection. The directory
passed to `--data` must contain both files. Leave the server collector running; analyse a copy on
your development machine. The short recording already in `data/chain` is suitable for a smoke test.

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain replay --data data/chain --settings examples/chain-replay.json --output artifacts/chain-replay.json --compare-delays
```

`--compare-delays` runs the same rule three times, entering 1, 5 and 30 seconds after the collector
first detected each token. Without it, the command uses `EntryDelaySeconds` from the settings file.
These are delays after detection, not after creation on Solana. The report includes actual simulated
entry timestamps, which can be later because an observation must exist.

The example settings are an illustration, not a selected strategy or a verified historical fee
schedule. They spend 0.01 SOL per trade, assume 125 basis points of trading fees per side, a further
300 basis points of adverse slippage, 0.00001 SOL per transaction, and 0.0025 SOL of entry setup
cost. Setup cost is charged in full without assuming account rent is later recovered. All four
cost fields must be present, even when intentionally set to zero; misspelled settings fail.

The example asks to exit after five minutes, a 100% net gain, or a 50% net loss, whichever is first
observed. A 100% gain means doubling the money spent, including entry costs. Exit execution uses a
later observation, so a take-profit trigger can still lead to a losing fill. A gap exceeding thirty
seconds stops the measurement rather than assuming nothing happened inside it.

## Reading the report

Each delay has separate rows for the filtered and control samples. Quote assets are never pooled:
the current simulator supports only records explicitly identifying SOL, nine quote decimals and the
`pump` protocol. Other markets remain in the report as unsupported.

| Field | Meaning |
| --- | --- |
| Admitted | Distinct tokens selected by the collector at their first recorded sighting |
| Entered | A usable curve observation permitted a simulated purchase |
| Resolved | A later observation permitted the rule's simulated sale |
| Unknown exits | A position was opened but its exit could not be established |
| Missing entries | No usable observation established an entry within the allowed delay |
| Unavailable entries | The curve had completed or could not supply the purchase without being exhausted |
| Unsupported | Market metadata was absent or outside the supported SOL curve model |
| Resolved median/mean | Returns on positions with observed exits only |
| Stress mean | Returns on entered positions after writing every unknown exit off completely, plus one exit transaction cost |

Resolved-only results can look too good because they omit unknown outcomes. Stress results are a
specific pessimistic scenario, not measured realised returns and not a guaranteed lower bound on
live performance. Missing entries remain unmeasured; the simulator does not invent purchases there.
The complete JSON report retains each token's outcome, admission probability, costs, timestamps and
profit or unknown value. It also includes win rates, file checksums, duplicate counts and capacity
drops. Repeating a run on identical files and settings produces identical JSON.

The report always says `Gate: NotEvaluated`. Neither a positive result nor three weeks of collection
automatically authorises trading.

## What the model establishes

The simulator uses integer base-unit reserves with decimal arithmetic. The constant-product quote
includes the size of the hypothetical purchase and sale, fees, adverse slippage and fixed costs.
Purchases must leave real tokens in the curve. A sale must have sufficient real SOL reserves for
its full pre-fee output. It never clips an unaffordable sale into an invented partial fill.

An exit signal uses only observations already reached. Its sale waits for a strictly later
observation and the configured exit delay. There is no interpolation through a gap and no sale at
the token's eventual high. A migrated curve produces an unknown exit, since its later exchange
reserves are not in these files.

The reader keeps the earliest launch record per mint, including its original admission decision.
A later restart cannot turn a previously excluded token into a retrospectively selected winner.
Exact adjacent duplicate polls are counted once. Conflicting or out-of-order polls and malformed
JSON fail with a filename and line number. Changed input files fail; use a stable copy.

## Remaining measurement limits

- This is a snapshot approximation, not a Solana transaction emulator. A hypothetical trade changes
  its fill price but is not propagated into subsequent historical snapshots.
- The collector records request timestamps, not response receipt, slot commitment or landed
  transactions. HTTP latency, stale responses and transaction failures need separate measurement.
- The collector does not record the historical fee configuration. Fees and account setup costs
  must remain explicit scenario assumptions. This does not reproduce every on-chain rounding rule.
- Graduation requires a separate PumpSwap price and execution path. Treating the final curve
  reserves as an exchange price would manufacture profitable exits.
- No weighting is applied to the samples. Stored admission probabilities alone do not correct
  missed feed launches, capacity drops, correlated sampling after restarts or changes in the filter.
- Trades use independent fixed amounts. Their sum is not a portfolio return: simultaneous positions,
  available funds, the two capital allocations and sleeve drawdown still need their own model.
- This does not validate mint authorities, Token-2022 extensions, bundled activity or transaction
  sellability. A positive replay is not proof that a wallet could execute it safely or profitably.
- A frozen rule and a later untouched sample are still required. Repeatedly selecting settings on
  the same recording is research, not out-of-sample confirmation.

## Next integration points

The new `SolastaBot.Chain` project contains the pure replay and curve-pricing model.
`SolastaBot.Data/Chain` reads the existing collector format, and `SolastaBot.Cli/Chain` runs reports.
The active collector and its format do not need deployment changes to use this command.

The next measurements need a larger completed copy from the Ubuntu collector, followed by a frozen
evaluation window. Gaps and migration coverage in that report determine which collection work is
needed. Portfolio simulation must enforce the separate funding allocations before any paper or
live execution is connected.

## Protocol references checked on 2026-09-13

The [Pump program documentation](https://github.com/pump-fun/pump-public-docs/blob/main/docs/PUMP_PROGRAM_README.md)
describes virtual and real reserves and migration to PumpSwap. Its
[buy instruction](https://github.com/pump-fun/pump-public-docs/blob/main/docs/instructions/BUY.md)
requires available real tokens and describes account creation costs.
The [fee program documentation](https://github.com/pump-fun/pump-public-docs/blob/main/docs/FEE_PROGRAM_README.md)
describes fees selected using configuration and market capitalisation. The collector does not save
that configuration, so this replay does not present one fixed rate as historical fact.
