# Chain gate: not decided, and the reason is a missing price path

Measured 2026-09-12 against 749,691 pump.fun launches from 2026-05-12 to 2026-06-10, enriched with
live bonding-curve state on 5,775 of them — a 796-launch random cross-section and **every one of the
4,987 launches matching the filter**, of which 4,979 resolved.

## Verdict

**Do not build yet. Do not abandon either.** This spike could not reach a build-or-abandon call, and
reporting one would mean inventing the half of the measurement that is missing.

What it did establish is that the sleeve is not obviously dead, which is a change from the prior. A
pre-buy filter that is fully computable at buy time selects launches whose price runs both **higher**
and, more importantly, **very much later** than the cross-section. The unfiltered median launch
reaches its lifetime peak market capitalisation **4 seconds** after creation, which no C# process on
a home connection can reach. The filtered median reaches it at **100 seconds**, which is comfortably
reachable. That is the difference between a strategy that is latency-dead and one worth measuring
properly.

What it could not establish is whether the strategy makes money, because every free source of Solana
trade-level history for this window has closed. Without a price path there is no exit rule, no loss
distribution, and therefore no expectancy. The numbers below are an **upper bound on the upside with
nothing at all on the downside**, and an upper bound is not a return.

The single cheapest thing that would settle this is in [What to provision](#what-to-provision).

## The regime these numbers describe has already changed

Everything above is measured on launches from May 2026. Four months later the market is not the same
one, which matters because the filter below does not survive the move unaltered.

| | May 2026 | September 2026 |
| --- | --- | --- |
| Launches per day | 25,800 | roughly 64,000 |
| Quote asset | SOL | about half PUMP |
| Bonding curve start | 30 units | near 1,035 units for PUMP |

The `vSOL > 31.04` component is a "somebody already bought before you saw it" test, and it only reads
that way against a curve starting at 30. Applied unchanged to a PUMP-quoted launch starting near
1,035 it admits essentially everything and measures nothing. The generalisation is to learn the
buy-in threshold per quote asset as a running median scaled by `31.04/30`, which on SOL converges
back to 31.040 on its own. That it reproduces the pre-registered figure without being told to is the
check that the rule is the right one rather than a fitted one.

Read the peak and timing measurements as a description of the May regime, sound for what it was, and
not as a live filter specification. `tools/chain-collector/` implements the generalised rule.

## Gate

| Test | Bar | Measured | Verdict |
| --- | --- | --- | --- |
| Median return of the filtered subset, net of costs | positive | Not measurable. The upper bound — perfect-foresight exit at the exact all-time high — is **+24.5%** net at 2.70% cost and **+19.6%** at 6.51%. A realistic exit captures an unknown fraction of it, and the loss side is unmeasured. | **Not measured** |
| Expectancy at a five-second entry delay | positive | Not measurable. At 5 s, **13.3%** of filtered launches have already peaked, against **56.3%** unfiltered. | **Not measured** |
| Edge present at thirty seconds | present, even if smaller | At 30 s, **30.3%** of filtered launches have already peaked, against **78.3%** unfiltered. The filtered subset is still reachable at 30 s; the cross-section is not. | **Pass, on the timing evidence only** |
| Filtered subset size | large enough to trade | **153/day** in-sample, **179/day** out-of-sample; 4,987 launches over the 29-day window. | **Pass** |
| Out-of-sample month | confirms the in-sample result | Median peak **1.255x → 1.303x**; median time-to-peak **100 s → 100 s**; share already peaked at 30 s **30.8% → 30.0%**. Every filter component holds. | **Pass** |
| Worst-case sizing | survives a run of consecutive total losses | Bracketed, because the realised win rate is unmeasured. Worst-month losing run is **12 trades** at a 60% win rate and **201** at 4.1%. At 0.5% of sleeve per position with total losses, the sleeve retains **94%** to **37%** across that range. | **Not measured** |

Three rows pass, three cannot be measured. The three that cannot be measured all need the same
missing input, and they are the three that decide whether money is made.

## Provenance

The cross-section is RED-PUMP-2026-v1 (Zenodo concept DOI `10.5281/zenodo.20633486`, record 22286914
v1.5, CC-BY-4.0), the reproducibility package for arXiv:2607.02823. Only the two frozen input files
were used, pulled by HTTP range request against the ZIP central directory rather than downloading the
full 348 MB deposit. `red_pump_2026_v1_launches.jsonl.gz` verifies to the published SHA-256
`042940379e8c897ac97403e6b25a5b302fb32b6902a8fc0cef4ab70ac11e8f84`.

The cohort was rebuilt from those raw inputs rather than taken from the paper's outputs: join
launches to outcomes on mint, keep first sighting, restrict to the V3 collector window
2026-05-12T13:49:06Z to 2026-06-10T18:11:08Z. That yields **749,691 mints, 1,598 graduated, 748,093
timed out**, against the manuscript's 749,816 / 1,597 / 748,219 — a 0.017% difference, presumably a
different tie-break on the 19 duplicate launch rows. The split boundary is the paper's own,
2026-05-27T00:00:00Z.

Live enrichment is pump.fun's own `frontend-api-v3.pump.fun/coins/{mint}`, which needs no
authentication and still resolves four-month-old mints. SOL/USD is Binance `SOLUSDT` hourly klines,
also no authentication.

## The measurement that decides it

pump.fun's bonding curve is a constant product, so within one coin `k = vSOL · vTOKEN` is fixed and
market capitalisation is proportional to `vSOL²`. The API exposes `ath_market_cap` and
`ath_market_cap_timestamp` per mint. Those two fields give, for any launch, **how far it ever ran and
exactly when** — without needing the trades in between.

`ath_market_cap` is undocumented, so it was validated before use. Take the launches whose virtual SOL
reserve sat at exactly 30.0 both when the collector first saw them in May and today — tokens that
never traded at all. Their market cap is pinned at 27.959 SOL, so `ath_market_cap / 27.959` must
recover the SOL price at the moment the field was last written. Against the actual SOL price at each
coin's creation the ratio has median **1.0041**, p25 0.9993, and 72% of cases within ±5%. The field
is the peak of `market_cap × SOL/USD`, denominated in USD, seeded at creation.

The entry point is not assumed. The dataset records `detection_lag_min`, the gap between on-chain
creation and the collector first seeing the mint: median **33 seconds**, p25 17 s, p75 49 s. So the
recorded initial reserve is the curve state roughly half a minute after launch, which is the entry a
realistic bot gets. Crucially the lag is the same for the filtered subset (p50 0.60 min) as for the
cross-section (p50 0.57 min), so nothing below is an artifact of buying later.

### Time from creation to the all-time high

| Subset | n | p10 | p25 | p50 | p75 | p90 | ≤1 s | ≤5 s | ≤30 s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Random cross-section | 796 | 0 s | 1 s | **4 s** | 24 s | 146 s | 35.7% | 56.3% | 78.3% |
| Filtered subset | 4,979 | 3 s | 20 s | **100 s** | 578 s | 4,516 s | 6.4% | 13.3% | 30.3% |
| — in-sample | 2,297 | 3 s | 20 s | 100 s | 703 s | 4,942 s | 6.7% | 13.1% | 30.8% |
| — out-of-sample | 2,682 | 3 s | 21 s | 100 s | 499 s | 4,121 s | 6.1% | 13.4% | 30.0% |

The unfiltered row is the reason a naive sniper cannot work. By the time a home-connection process
has seen a launch and landed a buy, **78% of launches are already past their lifetime high**. There
is nothing left to sell into. 35.7% peak inside the first second, which is the deploy block itself.

The filtered row is the finding. The same 30-second entry is late for only 30.3% of them, and the
in-sample and out-of-sample halves agree to within a percentage point on every column.

The obvious objection is that the unfiltered row is dragged down by tokens that never traded, whose
peak is their creation by definition. It is not. Restricting both sets to launches that actually
traded above entry leaves the gap intact:

| Set, peak > 1.02x only | n | p25 | p50 | p75 | ≤30 s |
| --- | --- | --- | --- | --- | --- |
| Random cross-section | 424 | 1 s | **4 s** | 17 s | 81.6% |
| Filtered subset | 3,913 | 39 s | **178 s** | 935 s | 22.5% |

A 45-fold difference in median time to peak, between two sets entered at the same moment.

### Peak multiple from the ~33 s entry, perfect foresight

Buy at the collector's detection point, sell at the exact all-time high, zero slippage, zero fees,
guaranteed fill. A strict upper bound that no real strategy can reach.

| Subset | n | mean | p10 | p25 | p50 | p75 | p90 | p95 | p99 | max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Random cross-section | 796 | 2.12 | 1.000x | 1.000x | **1.045x** | 1.622x | 4.103x | 6.306x | 13.4x | 84x |
| Filtered subset | 4,979 | 5.05 | 1.000x | 1.044x | **1.279x** | 2.070x | 5.111x | 11.08x | 64.0x | 1,855x |
| — in-sample | 2,297 | 6.03 | 1.000x | 1.022x | 1.255x | 2.116x | 5.114x | 11.20x | 77.5x | 1,855x |
| — out-of-sample | 2,682 | 4.20 | 1.000x | 1.061x | 1.303x | 2.034x | 5.092x | 10.91x | 50.7x | 835x |

**31.8%** of the cross-section never trades above the entry at any instant. For the filtered subset
that falls to **13.7%**.

The medians agree across the temporal split to within 4%. The means do not — 6.03 against 4.20 — and
should not be used; see [the mean](#what-ruins-this-measurement-and-what-was-done-about-it) below.

### Costs

pump.fun charges **1.25% per trade** on the bonding curve (0.95% protocol, 0.30% creator; schedule of
2026-05-20). Curve slippage nets to zero on an immediate round trip, because a constant product is
reversible — the real slippage is other snipers moving the curve between your buy and your sell,
which this data cannot see.

| Position | Tip per tx | Round trip | Breakeven gross |
| --- | --- | --- | --- |
| 0.50 SOL | 0.0005 SOL | 2.70% | 1.0278x |
| 0.25 SOL | 0.0020 SOL | 4.10% | 1.0428x |
| 0.10 SOL | 0.0020 SOL | 6.51% | 1.0696x |

Median peak after cost: random cross-section **1.017x / 1.003x / 0.977x**, filtered subset **1.245x /
1.227x / 1.196x**. The cross-section's best possible median outcome sits inside the cost band. The
filtered subset's clears it — with perfect foresight.

### Reachable and worth reaching

Combining the two, the decision-relevant fraction is launches whose peak arrives **after** a
30-second entry *and* is above the 1.0428x breakeven at 0.25 SOL with a 0.002 SOL tip.

| Set | n | Peak after 30 s | Peak above breakeven | Both | Median peak, of those |
| --- | --- | --- | --- | --- | --- |
| Random cross-section | 796 | 21.7% | 50.4% | **9.0%** | 1.450x |
| Filtered subset | 4,979 | 69.7% | 75.1% | **59.6%** | 1.729x |

Nine percent against sixty. On the out-of-sample rate of 179 filtered launches a day, about **107 a
day** are both still rising when a realistic bot arrives and carry a peak worth taking — before any
question of whether the peak can actually be captured.

### How much of the peak would have to be captured

This is a model, not a measurement, and it is stated separately for that reason. Assume a strategy
realises a fixed fraction `f` of each launch's peak excess: `realised = 1 + f · (peak − 1)`. Median
outcome, net of cost:

| f | Random, 2.70% | Random, 4.10% | Filtered, 2.70% | Filtered, 4.10% | Filtered, 6.51% |
| --- | --- | --- | --- | --- | --- |
| 10% | 0.977 | 0.963 | 1.000 | 0.986 | 0.961 |
| 20% | 0.982 | 0.968 | **1.027** | **1.013** | 0.987 |
| 30% | 0.986 | 0.972 | 1.055 | 1.039 | **1.013** |
| 50% | 0.995 | 0.981 | 1.109 | 1.093 | 1.065 |
| 100% | 1.017 | 1.003 | 1.245 | 1.227 | 1.196 |

On the filtered subset the median trade breaks even at roughly a **20–30% capture rate**. The
cross-section never breaks even at any capture rate below perfect.

That 20–30% figure is still optimistic, and should not be read as a target. The model assumes a
losing launch is exited at entry, when in reality an abandoned bonding curve falls toward its floor
and the exit is worse than flat. The true requirement is higher by an amount only the price path can
establish. This table's purpose is to show what the missing measurement has to produce, not to stand
in for it.

## The filter

Every component is computable at buy time from the launch's own metadata and the curve state you
observe when you see it. Nothing here uses graduation, which is the future.

| Filter | In-sample | Out-of-sample | Per day |
| --- | --- | --- | --- |
| No filter | 0.2051% | 0.2217% | 25,806 |
| `has_telegram` | 1.6874% | 1.4664% | 612 |
| `has_twitter` | 0.2405% | 0.2515% | 16,774 |
| `has_website` | 0.2657% | 0.2998% | 10,111 |
| initial vSOL > 31.04 | 0.6684% | 0.6780% | 6,583 |
| telegram + twitter | 1.8037% | 1.8006% | 495 |
| telegram + vSOL > 31.04 | 4.0796% | 3.3533% | 201 |
| **telegram + twitter + vSOL > 31.04** | **4.7350%** | **4.0968%** | **153 / 179** |

These are **collector-observation rates, not pump.fun graduation rates**, and the distinction is the
author's own (see the withdrawal below). They are held here only as a cheap ordinal proxy for "did
anything happen", and nothing in the verdict rests on them. The headline peak and timing measurements
do not use this endpoint at all.

The peak measurement decomposes the same way, which is what a real signal looks like rather than a
fitted one:

| Subset | n | Median peak | Median time to peak | P(peak > 1.06x) |
| --- | --- | --- | --- | --- |
| All random | 796 | 1.045x | 4 s | 49.1% |
| Initial vSOL > 31.04 only | 188 | 1.118x | 17 s | 61.2% |
| Telegram + twitter + vSOL > 31.04 | 4,979 | 1.279x | 100 s | 72.6% |

Requiring initial vSOL above the 30 SOL platform default means requiring that somebody already bought
before you saw it. That is observable, not hindsight, and it is the strongest single component. The
31.04 threshold is not fitted here — it is the upper edge of the deposit's pre-registered
`30_to_31_04` market-cap bin, adopted as-is precisely so that no threshold search happened on this
data.

### An incidental correction to the dataset's labels

Checking each filtered launch against live pump.fun state found that **533 of 4,979 (10.70%) have
`complete = true`** — they did graduate. The collector recorded only 4.10%. The live rate is 2.6x the
recorded one, which independently confirms the deposit's own corrigendum: `TIMEOUT` means "this
collector stopped watching", not "did not graduate". Their median perfect-foresight peak is **9.03x**.

Anyone reusing this dataset should treat its terminal label as a lower bound and re-derive outcomes
from the live API, which costs nothing.

## The source paper's status is disputed, and nothing here rests on it

This matters because the chain sleeve's brief was built on the paper's numbers, so what each source
says is recorded in full. **The two accounts below have not been reconciled, and this document does
not adjudicate between them.** Nothing in the verdict, the peak measurements or the timing results
uses any published figure, so the dispute changes no conclusion here.

**What the data deposit says.** The Preprint Correction and Withdrawal Notice of 2026-08-21
(`11_corrigendum/` in the deposit) withdraws, as **not identified from the data**:

- the 24-hour graduation rate and its point estimate — it is a collector-observation rate, and
  `TIMEOUT` does not falsify platform-side graduation;
- the 3.18x decline against the September–October 2025 figure;
- the characterisation of Telegram as a lower-bound predictor of graduation;
- **all Kaplan-Meier estimates, log-rank statistics and Cox proportional-hazards results**, because
  the censoring-time reconstruction triggered its adequacy stopping rule at 2.6925% against a
  pre-registered 2% ceiling.

The author is explicit that "users who cited v1.3 for graduation-rate estimates, decline magnitudes,
or Telegram effects should not substitute v5 numbers; the corresponding quantities do not exist in
this dataset."

**What the arXiv preprint says.** Checked independently on 2026-09-12 against
`arxiv.org/abs/2607.02823` and its HTML full text, which carry versions v1 to v4 rather than the
deposit's v1.3 and v1.5. That page describes a far narrower withdrawal: an attempted
market-capitalisation correction that would have raised the pooled rate to about 0.333%, tested
against live bonding-curve state on a 100-mint sample where none had graduated. On that page the
0.198% rate is retained but reframed as a fast-regime figure over roughly a six-minute window and
therefore a lower bound on the true 24-hour rate, and the Telegram effect and Cox results are
presented as standing.

**Unreconciled.** The deposit and the preprint may be separate artefacts saying different things, the
preprint text may simply predate the deposit notice, or one reading may be wrong. Both accounts agree
on the underlying mechanism, which is that `TIMEOUT` means a collector stopped watching rather than
that a token failed to graduate, and the live sampling in this document measured that undercount
directly at 2.6x. Treat any published graduation rate as a floor of unknown tightness, and prefer a
filter measured against your own cross-section to one inherited from a paper. That instruction is the
same whichever account is correct, which is why the dispute did not need settling to proceed.

What survives is the v1.5 negative result: a pre-registered logistic model with development AUROC
0.8594 collapses to validation AUROC 0.4642, bootstrap interval [0.4112, 0.5196] containing 0.5,
calibration slope 0.0129 against a pre-registered [0.85, 1.15], six of nine stability gates failing.

Two things follow. First, the collector's `GRADUATED`/`TIMEOUT` label is unsound as an outcome, which
is why it appears above only as a descriptive proxy and why the verdict rests instead on curve state
read directly from the live API. Second, the v1.5 negative result is about a **40-column model with
date splines** and does not transfer to simple filters: measured directly, every marginal filter
above holds across the same temporal split, and so does the peak distribution. The splines are what
fails to extrapolate. Reading the abstract as "the social-presence effect does not generalise" would
have been the wrong conclusion to draw, and rebuilding the cohort from the raw inputs rather than
trusting the published outputs is what made the difference visible.

## Sizing, bracketed

The realised win rate is the thing this spike could not measure, so the losing-run arithmetic is
given across the plausible range rather than at one number. 179 trades a day, worst run over a month
at the 95th percentile, every loss assumed total — itself pessimistic, since a bonding curve normally
still lets you sell for something.

| Assumed win rate | Worst day | Worst week | Worst month | Sleeve left at 0.5%/position |
| --- | --- | --- | --- | --- |
| 60% — peak reachable and above breakeven | 5 | 7 | 12 | 94.2% |
| 25% — optimistic realised | 14 | 21 | 35 | 83.9% |
| 10% — pessimistic realised | 30 | 49 | 87 | 64.7% |
| 4.1% — collector-graduation proxy | 57 | 102 | 201 | 36.5% |

0.5% of sleeve per position survives the whole range without ruin; 1% does not, leaving 13% of the
sleeve at the bottom of the range. Sizing is therefore not the binding constraint — the win rate is,
and it is unmeasured. The 60% row is an upper bound twice over, since it counts a launch as won
whenever its *peak* cleared breakeven, which assumes the peak is captured.

## What ruins this measurement, and what was done about it

**Survivorship.** Handled. The sample is every launch in the window, including the 99.79% that never
graduate, and the enrichment sample is drawn at random from that cohort before any outcome is known —
and for the filtered subset it is not a sample at all but the complete population of 4,987, of which
4,979 resolved and 8 were lost to rate limiting. The random sample tracks the cohort on every
observable: telegram 1.63% against 2.44%, twitter 58.5% against 63.2%, website 35.9% against 38.1%,
identical median initial reserve. Two selection traps were found and fixed mid-analysis: an early cut
dropped graduated coins (the winners) because their curve constant is no longer derivable, and
dropped null-`ath_market_cap` coins — which turn out to be **100% tokens that never traded at all**,
the deadest rows in the sample. Both are now included.

**The mean.** Reported, and then disregarded. It is not a property of the distribution: on the
filtered subset the single largest row contributes 7.4% of the total, the top 25 rows contribute
38.2%, and the mean splits 6.03 against 4.20 across the temporal halves while the median moves only
1.255 to 1.303. Every table leads with the median and the full percentile spread.

**A tail that depends on an arbitrary choice.** Each coin's entry market cap needs its curve
constant, taken from current state. For 14.1% of the filtered subset that constant sits more than 20%
off the canonical value, usually because the coin has left the curve. Recomputing every entry with
the canonical constant instead moves the filtered median from 1.279x to 1.318x and the
reachable-and-profitable share from 59.6% to 61.4% — but moves the maximum from 1,855x to 835x and
the mean from 5.05 to 3.98. **The medians and the headline fraction are robust to the choice; the
mean and the extreme tail are not**, and are reported only to be discounted.

**A contaminated proxy, abandoned.** The dataset's own reserve readings pile up at a round 2,000 SOL —
141 of the 238 rows above 1,500, and nine at exactly 2000.0000. A bonding curve graduates long before
that, so this is a clamp or sentinel, not market data. It was one of two reasons the dataset's own
before/after reserve ratio was dropped in favour of the live API.

**Informative censoring, the other reason.** The collector only sees a mint while it is in the top-50
newest feed, so **49.5% of the cohort has a final reserve exactly equal to its initial** — never
observed to move even once. Those rows are censored, not flat. Worse, the censoring is not ignorable:
censored mints are recorded as graduating at 0.2625% against 0.1648% for the rest.

**Entry delay.** Measured, and it is the central result. The entry is not assumed instant: the
recorded lag is a median 33 seconds, and sensitivity is reported at 1, 5 and 30 seconds.

**Hindsight filters.** Every filter is metadata present at launch plus the curve state at the moment
of detection. Graduation is used only to describe subsets, never to select them.

**Costs.** The published 1.25% per side, exact constant-product arithmetic, base fee and a range of
tips. The one cost that could not be modelled is competing snipers moving the curve between the buy
and the sell.

## What is not measured

These are the gaps, stated plainly rather than filled in.

1. **There is no price path.** `ath_market_cap` gives one point — the maximum — and nothing else.
   Every realistic exit rule (a fixed multiple, a trailing stop, a time stop) needs the path between
   entry and exit. None can be backtested from this.
2. **The loss side is entirely unmeasured.** It is known that 13.7% of the filtered subset never
   trades above entry, but not how far the losers fall, nor how much of a position can actually be
   exited on the way down. Modelling a failed exit as a total loss, which the method requires, is
   impossible without sell-side data.
3. **Honeypots and rugs are not modelled.** While a token is on the bonding curve the pump.fun
   program structurally permits selling, which is a real mitigant the perp sleeve does not have. It
   says nothing about post-graduation liquidity removal.
4. **Sub-minute entry returns cannot be recovered from this cohort at all.** The collector polls once
   a minute. The 1 s and 5 s rows above are *timing* — the share of launches already past their peak
   — not returns at those delays. No amount of work on this dataset produces the latter.
5. **The tail is not resolved even at full population.** The filtered subset is complete, so its
   median carries no sampling error, but p99 still rests on roughly fifty rows whose values shift by
   a factor of two under the curve-constant sensitivity above. The tail is where a lottery payoff
   would have to live, and it is the least trustworthy column in this document.
6. **`ath_market_cap` semantics are inferred.** The validation is strong but indirect, and the field
   is undocumented and could change without notice.
7. **The cohort may not be every launch.** It is every launch the collector *saw*, and the collector
   only ever read the top-50 newest feed. During bursts a mint could in principle be pushed out
   before a poll. The deposit's own audit found 43 such cases with 97.7% at feed rank 45 or worse,
   which suggests the effect is small and concentrated at the window edge, but it is not zero and was
   not independently re-derived here. Nothing here depends on the collector's terminal label, but
   everything depends on this membership list.
8. **One window, one regime.** Twenty-nine days of May and June 2026. The split confirms stability
   across a fortnight, not across a market cycle.

## What to provision

No free route to Solana trade-level history for this window still exists. This was verified, not
assumed.

- **Flipside is gone.** `api-v2.flipsidecrypto.xyz` is NXDOMAIN; `flipsidecrypto.xyz` now redirects
  to `edisyl.com`. Flipside sold its blockchain data business to SonarX and Flipspace shut down on
  2026-06-17. The brief's "free tier, unlimited public queries" route no longer exists and the
  successor is enterprise-priced.
- **Bitquery returns a hard 401** on both `streaming.bitquery.io/eap` and `graphql.bitquery.io`
  without a token.
- **Dune** went view-only for legacy free accounts on 2026-09-10, as the brief said.
- **pump.fun has no public trades or candles endpoint.** `trades/{mint}`, `trades/all/{mint}`,
  `candlesticks/{mint}` and `coins/{mint}/trades` all return 404. Only `coins/{mint}` is open.

Two options, in order of preference.

**Option A — run our own collector forward. Free, about three weeks, and it is the better dataset.**
Nothing needs provisioning. `coins/{mint}` is open, needs no key, and returns full curve state;
polling the newest-coins feed captures launches. This beats buying history because it fixes the
defect that ruins the public dataset: poll each tracked mint *individually* on a short interval
rather than only while it sits in the top-50 feed, which removes the 49.5% censoring and yields a
real price path at whatever resolution we choose. Budget one cheap VPS. Respect the rate limit —
roughly **1 request/second sustained**; six concurrent workers triggered a multi-minute ban during
this spike, while 4,979 sequential requests at a 0.9 s gap completed in 87 minutes with zero
failures. The cost is calendar time before the gate can be re-run.

**Option B — buy the history. About $289/month, immediate.**
Bitquery at <https://bitquery.io/pricing>. The free trial is 7 days and 1,000 API points (about 200
calls, no card), enough to validate a query shape and nothing more. The trap is that self-service
plans carry only **30 days of rolling Solana DEX history**, so the $79/mo Pro plan alone would *not*
reach the 2026-05 window. It needs Pro ($79/mo, commercial use) **plus** the Solana historical-trading
archive add-on (**from $210/mo**). That unblocks exactly the three gate rows above: the full trade
series per mint, hence the price path, hence a real exit rule, a loss distribution, and expectancy at
1, 5 and 30 seconds.

If the sleeve is worth three weeks of calendar time, take Option A. If it is worth $289 to know this
month, take Option B. Do not fund the sleeve on the numbers in this document.

## What to do next

1. **Decide Option A or Option B.** Nothing else in the chain sleeve is worth doing first, and no C#
   should be written until one of them has produced a price path.
2. **Re-run this gate against the path.** The filter, the cohort construction, the cost model and the
   entry-delay framing are all done and reusable; only the return column is missing.
3. **Hold the entry-delay budget at 30 seconds as the design constraint.** The filtered subset is
   reachable there and the cross-section is not. If the eventual engineering cannot hold 30 seconds
   from deploy to landed buy, the measurement above says not to bother, because the filter's entire
   advantage is that its tokens peak late.
4. **Do not widen the filter to raise the trade count.** 153–179 launches a day is already ample for
   a lottery-shaped sleeve, and the decomposition shows the edge weakening monotonically as each
   component is dropped.
5. **Re-derive outcomes from the live API, never from the collector's label,** in any future work on
   this dataset. The label undercounts graduation by 2.6x.

Scripts and intermediate data for this spike were kept out of the repo. Nothing here touches
`SolastaBot.Core`, and no allocation follows from it: under the hard-wall rule a sleeve that has not
passed its gate is allocated nothing, and this one has not passed.
