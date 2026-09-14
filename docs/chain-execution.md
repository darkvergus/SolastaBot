# Solana execution

The worker supports `Paper`, `Simulate`, and `Devnet`. Devnet builds, signs and submits Pump or
canonical PumpSwap transactions using a dedicated wallet. Mainnet submission is disabled at the
configuration, router and RPC transport boundaries. Neither strategy sleeve has earned a real-money
allocation, and the launch filter is not a trained prediction model.

## Start with a dedicated devnet wallet

Create the keypair outside the repository. This command writes a Solana CLI-compatible 64-byte
JSON keypair with an owner-only Windows ACL or Unix mode 600. It prints only the public address.

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain connected wallet-create --path "$env:LOCALAPPDATA/SolastaBot/wallets/devnet.json"
```

On Ubuntu, use an absolute path such as `/home/solasta/.config/solastabot/devnet-wallet.json`.
Copy [chain-devnet.json](../examples/chain-devnet.json) into your local configuration directory
and change `WalletPath`. Do not put a private key in settings or source control.

`Strategy` retains the existing paper settings shape so both paths use the same entry and exit
rules. Its nested `Mode: Paper` identifies that settings schema; the outer `Mode` selects the
execution router. Likewise, `TotalPaperCapitalSol` is the existing allocation field name: the
connected path uses it as a spending ceiling, never as an invented wallet balance. It also checks
the actual wallet balance. Only the chain fraction is spendable.

Connected pricing reads on-chain fee tiers and integer reserves. The replay fee/slippage/setup
estimates under `Strategy.Execution` do not set the connected transaction's fees. The outer
`SlippageBasisPoints` sets minimum output; `FeeReserveLamports` caps the additional budget held for
network fees and account creation. Marks deduct a current RPC network-fee quote, not the entire
reserve. Confirmed wallet changes include actual fees and rent payments.

Fund the public address with **devnet test SOL** using [Solana's faucet](https://faucet.solana.com/),
or request one test SOL through the CLI before starting the session:

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain connected airdrop --settings <settings.json> --state data/chain-connected
dotnet run --project SolastaBot.Cli -c Release -- chain connected check --settings <settings.json> --state data/chain-connected
```

Wait for the airdrop to finalize before `check`. Use a new session and wallet for an independent
exercise. Before its first recorded order, a session can receive initial funding even if an earlier
check saw a zero balance; the configured allocation does not increase with that deposit.
Changing network, wallet, settings or journal location does not silently reset an existing
session. The wallet is bound to its journal under the current OS user's local application-data
`SolastaBot/sessions` directory; a second process or another state root cannot use that same binding.
Use one machine and OS account for a trading wallet. This is not a distributed wallet lock.

## Exercise the routes

Use a devnet mint, never a mainnet mint copied from the public launch feed.

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain connected quote --settings <settings.json> --state data/chain-connected --mint <devnet-mint>
dotnet run --project SolastaBot.Cli -c Release -- chain connected buy --settings <settings.json> --state data/chain-connected --mint <devnet-mint>
dotnet run --project SolastaBot.Cli -c Release -- chain connected reconcile --settings <settings.json> --state data/chain-connected
dotnet run --project SolastaBot.Cli -c Release -- chain connected sell --settings <settings.json> --state data/chain-connected --mint <devnet-mint>
```

`buy` uses the configured trade size and risk limits. `sell` uses confirmed journal holdings.
These explicit commands exercise execution without applying the social/launch-age entry filter.
After a submission, run `reconcile` until the transaction is confirmed, failed, or expired. A returned
signature is an acknowledgement, not a fill. The implementation waits for a **finalized** receipt
before recognizing holdings. This deliberately serializes transactions and is not a low-latency
sniper implementation.

For automatic devnet operation, put test mints in `DevnetMints`, then start:

```powershell
dotnet run --project SolastaBot.Host -c Release -- --settings <settings.json> --state data/chain-connected
```

The explicit devnet watchlist supplies test age/social metadata at first observation. It exercises
entry delays, reserve thresholds, orders, monitoring and exits; it cannot validate selection quality.
The worker remembers tested mints, so restarting does not buy them again. A rejected or interrupted
entry is not retried as a new launch. With an empty watchlist it only manages existing positions.

`Simulate` builds an unsigned transaction and runs RPC simulation with signature verification
disabled. It never supplies an executable wallet signature to the RPC and never records simulated
holdings as actual positions. To inspect mainnet candidates without sending, set outer `Mode: Simulate`,
`Network: Mainnet`, `RpcUrl: https://api.mainnet-beta.solana.com`, `DiscoverMainnetLaunches: true`,
and leave `DevnetMints` empty. This still needs an appropriately funded, dedicated simulation wallet
for Solana's balance checks; it does not override account balances. Paper mode remains the full
virtual-position exercise without a wallet.

## Status and controls

The worker prints its exact session directory, including mode, network and wallet address. The
control commands use that directory, not its parent. Status and events require no signing key.

```powershell
dotnet run --project SolastaBot.Cli -c Release -- chain connected status --state <session-directory>
dotnet run --project SolastaBot.Cli -c Release -- chain connected events --state <session-directory>
dotnet run --project SolastaBot.Cli -c Release -- chain connected halt --state <session-directory>
dotnet run --project SolastaBot.Cli -c Release -- chain connected flatten --state <session-directory>
dotnet run --project SolastaBot.Cli -c Release -- chain connected resume --state <session-directory>
```

Halt stops new buys and buy rebroadcasts. Flatten also schedules sells. Submitted transactions cannot
be cancelled by a local control file. Resume clears manual controls, not a daily-loss halt or an
already scheduled exit. The daily-loss halt releases at UTC midnight. Stopping the process retains
the journal and positions; it does not automatically sell them.

## Recovery and supported tokens

The SQLite journal commits signed bytes, signature, expiry, quote, output bound and reserved funds
**before** sending. After a timeout or restart, it checks the same signature and may resend only the
same bytes. It blocks another transaction while the outcome is unresolved. Expiry requires finalized
block height beyond the validity window, no signature in RPC history, and unchanged wallet holdings.
Finalized receipts update positions and cash atomically. Failed sells retain holdings and charge
the actual fee; unsold tokens remain recorded if a receipt reports a partial sale.

Reconciliation compares actual SOL and associated token accounts with the journal. External balance
changes block trading rather than silently becoming available capital. Restoring the expected
balances releases that discrepancy. Receipt-bound violations remain a recorded execution fault:
investigate them before using `chain connected acknowledge-fault --confirm --settings ... --state ...`.
That command refuses pending signatures and cannot alter balances or invent positions; wallet and
journal must already agree. It is not a way to accept unknown fills.

Routes support ordinary native-SOL Pump curves and their canonical WSOL PumpSwap migration pools.
Completed curves trigger a fresh pool lookup. Missing migration data or unusable liquidity retains
the holding. Account owner, discriminator, mint, vault and fee configuration are checked. Mayhem,
cashback, holder-reward, boosted and non-SOL routes are refused. Freeze/mint authorities and
unsupported Token-2022 extensions are refused; metadata extensions are supported. PumpSwap wraps
and unwraps SOL in the transaction and requires no pre-existing wallet WSOL account, avoiding an
unrequested close of an existing account. Associated base-token rent is not reclaimed automatically.

Only one transaction is outstanding per wallet. Public RPC and polling have latency and coverage
limits. Paper and connected execution share selection/exit policy but have different fill and fee
models. Neither matching decisions nor successful devnet transactions establish profitability.

## Validation and protocol sources

Offline tests cover signed-before-send crashes, timeout recovery, identical rebroadcast bytes,
duplicate receipts, failed/partial sells, expiry and holdings checks, spend/fee limits, simulation-only
behavior, control changes during submission, wallet signing and UTC halt release. Existing paper and
replay tests remain in the suite.

The IDLs are pinned to pump-public-docs commit
[`81091419e4457566469d4e2a27f64ed84d42419c`](https://github.com/pump-fun/pump-public-docs/tree/81091419e4457566469d4e2a27f64ed84d42419c).
Account ordering also follows its
[upgrade notes](https://github.com/pump-fun/pump-public-docs/blob/81091419e4457566469d4e2a27f64ed84d42419c/docs/BREAKING_FEE_RECIPIENT.md).
The reference SDK versions inspected were `@pump-fun/pump-sdk` 2.0.0 and
`@pump-fun/pump-swap-sdk` 1.20.0. Solnet.Rpc 6.1.0 supplies transaction and wallet primitives.

Public devnet transactions and account snapshots in `SolastaBot.Tests/Fixtures/Solana` independently
check Pump sell encoding/account order and PumpSwap migration/account ordering. Their captured
accounts describe the capture time, not the historical transaction's exact execution state, so these
are protocol fixtures rather than a trading backtest. Tests also pin the IDL SHA-256 hashes.

The opt-in integration test performs one devnet buy and sell and checks actual holdings and wallet
changes. Use absolute environment paths because the test runner changes its working directory:

```powershell
$env:SOLASTA_DEVNET_TEST = '1'
$env:SOLASTA_DEVNET_SETTINGS = (Resolve-Path <settings.json>).Path
$env:SOLASTA_DEVNET_STATE = [System.IO.Path]::GetFullPath('data/chain-connected')
$env:SOLASTA_DEVNET_MINT = '<devnet-mint>'
dotnet run --project SolastaBot.Tests -c Release -- -class SolastaBot.Tests.Chain.Execution.DevnetRoundTripTests
```

On 2026-09-14, read-only quotes succeeded against devnet Pump mint
`CwcJ42bRmZeuJywVXKBxfGhh3baVGubTuxwAF9VoKALi` and migrated mint
`GaSKTF4rCdWC8CNFpddrcdD5AFcDXAJXvCFKeAQNpump`. The faucet returned an internal error followed by
HTTP 429 indicating an exhausted allowance or dry faucet. The opt-in round-trip test consequently
reported **skipped: insufficient test SOL**. No funded buy/sell success is claimed. The test wallet's
public address is `naex6CemuGDPK2Z4Avo67DPLMynxZhWrbwCGDRMSmBg`; its protected key is outside the repo.
Nothing was deployed to the Ubuntu server or changed in its collector.
