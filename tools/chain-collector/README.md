# chain-collector

Builds the dataset that `docs/chain-gate.md` could not buy: a real price path for pump.fun launches
through the window where the peak actually happens.

The gate stalled on one missing input. Every free source of Solana trade-level history has closed
(Flipside sold to SonarX and shut down, Bitquery 401s without a key and its self-service plans carry
only 30 days, Dune went view-only). But `frontend-api-v3.pump.fun/coins/{mint}` is open, needs no
key, and returns full bonding-curve state. Polling it per mint on a schedule *is* the price path.

```powershell
python tools/chain-collector/collect.py data/chain
```

Output is two append-only JSONL streams under the given directory. `data/` is gitignored.

## What it records

| File | One row per | Purpose |
| --- | --- | --- |
| `launches.jsonl` | every launch seen in the feed | the denominator — this is what keeps the sample free of survivorship |
| `polls.jsonl` | every individual poll of a tracked mint | the price path |

Each poll carries `virtual_sol_reserves` and `virtual_token_reserves` (price, via the constant
product) and `real_sol_reserves` and `real_token_reserves`. The real reserves matter as much as the
virtual ones: they are what a sell can actually be filled into, which is the exit-feasibility
question the peak-only analysis could not touch.

## The two decisions that matter

**It cannot track everything, so it tracks arms.** At ~0.8 req/s and ~89,000 launches a day,
tracking every launch buys about one poll each. So it admits three arms and records each launch's
`admit_p` so they can be reweighted later:

| Arm | Rule | Admitted | Rate |
| --- | --- | --- | --- |
| `filtered_sol` | SOL-quoted, telegram + twitter, reserve above the buy-in threshold | 100% | ~180/day |
| `filtered_alt` | same but quoted in PUMP or another asset | 40% | ~81/day |
| `control` | uniform random over everything else | 1 in 890 | ~100/day |

The control arm is not optional. Without it there is no way to tell whether the filter is selecting
anything, and the dataset rebuilds the exact bias this collector exists to remove.

**The buy-in threshold is learned per quote asset, not hardcoded.** As of 2026-09 only about half of
launches are SOL-quoted; most are quoted in PUMP, whose curve starts near 1,035 units rather than 30.
A fixed `> 31.04` rule would admit every PUMP-quoted coin at launch and measure nothing. Instead the
threshold is a running median of that quote asset's own initial reserves times `31.04/30`. On SOL it
converges to 31.040 on its own, reproducing the figure in `docs/chain-gate.md` exactly — which is
also the check that the rule is right.

Selection is made on what is visible the moment a launch is first seen, never on anything that
happens afterwards. `buyin_threshold` is written to every launch row so each decision is auditable.

## Poll schedule

Front-loaded, because the peak is early — median 4 s unfiltered, 100 s filtered.

| Age | Interval | Polls |
| --- | --- | --- |
| 0–120 s | 3 s | 40 |
| 120–600 s | 15 s | 32 |
| 600–3600 s | 60 s | 50 |
| 1–3 h | 300 s | 24 |

146 polls per mint over three hours, which covers the p90 time-to-peak of 4,516 s for the filtered
arm. Total load is about 0.66 req/s against the 0.80 limit, with ~45 mints active at once against a
cap of 70.

## Rate limits

Roughly **1 request/second sustained** is safe. Six concurrent workers earned a multi-minute ban
during the research spike; 4,979 sequential requests at a 0.9 s gap completed with zero failures. One
global token bucket governs every request, halving its rate and pausing 30 s on any 429 and easing
back afterwards. Do not run two copies against the same IP.

## Running it for real

The cost of this approach is calendar time, so it wants a cheap VPS rather than a workstation that
sleeps. It is a single file with no dependencies beyond the standard library.

```bash
nohup python3 collect.py /var/lib/chain-collector > collector.log 2>&1 &
```

Both streams are append-only and flushed per line, so it is safe to kill and restart; a restart
loses only in-flight tracking, not history. Watch the hourly line in the log — `full=` counts
launches dropped because the active cap was hit, and a rising number there means the arms need
tightening or the cap raising.

## When it has run long enough

About three weeks gives roughly 3,800 filtered and 2,100 control paths. At that point re-run the
gate in `docs/chain-gate.md` against real returns rather than the perfect-foresight upper bound: a
realistic exit rule, a loss distribution, and expectancy at 1, 5 and 30 seconds. The filter, the
cohort construction and the cost model are already done and reusable; only the return column is
missing.
