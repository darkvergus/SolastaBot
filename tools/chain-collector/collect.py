#!/usr/bin/env python3
"""
pump.fun launch collector for the chain sleeve gate.

Why this exists: the public RED-PUMP-2026 dataset cannot answer the gate because its collector only
ever read the top-50 "newest coins" feed. A mint that scrolled out of that feed was never looked at
again, so 49.5% of its rows have a final reserve exactly equal to their initial one and there is no
price path for anything. See docs/chain-gate.md.

The fix is the one thing that collector never did: poll each tracked mint INDIVIDUALLY on a
front-loaded schedule, so we get a real price series through the window where the peak actually
happens (median 4s unfiltered, 100s filtered).

Two streams, both append-only JSONL:

  launches.jsonl   one row per launch ever seen in the feed. Cheap, and it is the denominator that
                   keeps the sample free of survivorship. Never filtered.
  polls.jsonl      one row per individual poll of a tracked mint. This is the price path.

Tracking is necessarily selective: at ~0.8 req/s and ~71,000 launches/day, tracking everything buys
about one poll per launch. So we track the filtered arms plus a random control, and record each
launch's admission probability so the arms can be reweighted into an unbiased picture later.
Dropping the control would rebuild the exact bias this collector exists to remove.

QUOTE ASSETS. As of 2026-09 only about a third of launches are SOL-quoted; most are quoted in PUMP,
whose bonding curve starts near 1,035 units rather than 30. A fixed "virtual reserve > 31.04" rule
would therefore select every PUMP-quoted coin at launch and measure nothing. The portable form of
"somebody already bought before I saw it" is a reserve above that quote asset's OWN default, so the
threshold is learned per quote asset at runtime as a running median. For SOL that reproduces the
31.04 figure from the gate document exactly.
"""

import json, os, random, statistics, sys, threading, time, urllib.error, urllib.request
from collections import defaultdict, deque

API = "https://frontend-api-v3.pump.fun"
FEED = API + "/coins?offset=0&limit=50&sort=created_timestamp&order=DESC&includeNsfw=true"
UA = {"User-Agent": "Mozilla/5.0", "Accept": "application/json"}

SOL_QUOTE = "11111111111111111111111111111111"

# Poll schedule per tracked mint: (from_s, to_s, every_s). Front-loaded, because the peak is early.
SCHEDULE = [(0, 120, 3), (120, 600, 15), (600, 3600, 60), (3600, 10800, 300)]
TRACK_FOR = SCHEDULE[-1][1]
POLLS_PER_MINT = sum((b - a) // s for a, b, s in SCHEDULE)

FEED_EVERY = 20.0        # the 50-item window covers ~40s of launches at current rates; 20s is safe
RATE_LIMIT = 0.80        # sustained req/s. Six concurrent workers got banned; ~1/s is safe.
ACTIVE_CAP = 70          # safety valve on concurrently tracked mints; overflow is dropped AND counted
BUY_IN = 31.04 / 30.0    # "already bought" ratio, from the deposit's pre-registered bin edge
BASELINE_MIN = 40        # samples needed before a quote asset's baseline is trusted

# Admission probability per arm. filtered_sol is the arm the gate document actually measured, so it
# is taken whole; filtered_alt is exploratory and sampled; control is the honest denominator.
ADMIT = {"filtered_sol": 1.00, "filtered_alt": 0.40, "control": 1.00}
CONTROL_RATE = 1 / 890.0  # ~80/day against ~71,000 launches/day


def now():
    return time.time()


class Limiter:
    """Token bucket with adaptive backoff. One global limiter for every request this process makes."""

    def __init__(self, rate):
        self.base = rate
        self.rate = rate
        self.next_at = 0.0
        self.lock = threading.Lock()

    def wait(self):
        with self.lock:
            t = now()
            delay = max(0.0, self.next_at - t)
            self.next_at = max(t, self.next_at) + 1.0 / self.rate
        if delay:
            time.sleep(delay)

    def penalise(self):
        with self.lock:
            self.rate = max(0.15, self.rate * 0.5)
            self.next_at = now() + 30.0

    def recover(self):
        with self.lock:
            if self.rate < self.base:
                self.rate = min(self.base, self.rate * 1.05)


LIM = Limiter(RATE_LIMIT)


def get(url, attempts=5):
    for i in range(attempts):
        LIM.wait()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=25) as r:
                out = json.loads(r.read().decode("utf-8", "replace"))
            LIM.recover()
            return out, None
        except urllib.error.HTTPError as e:
            if e.code == 429 or e.code >= 500:
                LIM.penalise()
                continue
            return None, "http%d" % e.code
        except Exception:
            time.sleep(1.0 + i)
    return None, "exhausted"


class Writer:
    """Append-only JSONL, flushed per line so a crash costs at most the line in flight."""

    def __init__(self, path):
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        self.f = open(path, "a", encoding="utf-8")
        self.lock = threading.Lock()

    def write(self, row):
        line = json.dumps(row, separators=(",", ":"))
        with self.lock:
            self.f.write(line + "\n")
            self.f.flush()


# real_* reserves matter as much as virtual_*: they are what a sell can actually be filled into,
# which is the exit-feasibility question the peak-only analysis in docs/chain-gate.md could not touch.
CURVE_FIELDS = (
    "complete", "market_cap", "usd_market_cap", "market_cap_quote",
    "ath_market_cap", "ath_market_cap_timestamp",
    "virtual_sol_reserves", "virtual_token_reserves",
    "real_sol_reserves", "real_token_reserves",
    "total_supply", "last_trade_timestamp", "reply_count", "pool_address",
)


class Collector:
    def __init__(self, outdir):
        self.launches = Writer(os.path.join(outdir, "launches.jsonl"))
        self.polls = Writer(os.path.join(outdir, "polls.jsonl"))
        self.seen = set()
        self.seen_order = deque()
        self.tracked = {}
        self.baseline = defaultdict(lambda: deque(maxlen=600))   # quote_mint -> recent initial reserves
        self.lock = threading.Lock()
        self.rng = random.Random(20260912)
        self.stats = defaultdict(int)
        self.stop = False

    # -- classification -----------------------------------------------------
    def threshold(self, quote):
        d = self.baseline[quote]
        if len(d) < BASELINE_MIN:
            return None
        return statistics.median(d) * BUY_IN

    def classify(self, c, vsol):
        quote = c.get("quote_mint")
        thr = self.threshold(quote)
        social = bool(c.get("telegram")) and bool(c.get("twitter"))
        if social and thr is not None and vsol > thr:
            return ("filtered_sol" if quote == SOL_QUOTE else "filtered_alt"), thr
        return None, thr

    # -- feed ---------------------------------------------------------------
    def feed_loop(self):
        while not self.stop:
            t0 = now()
            coins, err = get(FEED)
            if err:
                self.stats["feed_errors"] += 1
            else:
                self.stats["feed"] += 1
                for c in coins if isinstance(coins, list) else []:
                    self.on_coin(c)
            time.sleep(max(0.0, FEED_EVERY - (now() - t0)))

    def on_coin(self, c):
        mint = c.get("mint")
        if not mint:
            return
        with self.lock:
            if mint in self.seen:
                return
            self.seen.add(mint)
            self.seen_order.append(mint)
            while len(self.seen_order) > 500000:
                self.seen.discard(self.seen_order.popleft())

        vs = c.get("virtual_sol_reserves")
        vsol = (vs / 1e9) if vs else 0.0
        quote = c.get("quote_mint")
        with self.lock:
            self.baseline[quote].append(vsol)

        arm, thr = self.classify(c, vsol)
        if arm is None and self.rng.random() < CONTROL_RATE:
            arm = "control"
        p = ADMIT.get(arm, 0.0) if arm else 0.0
        admitted = bool(arm) and (p >= 1.0 or self.rng.random() < p)

        desc = c.get("description") or ""
        row = {
            "mint": mint, "symbol": c.get("symbol"), "name": c.get("name"),
            "creator": c.get("creator"), "created_timestamp": c.get("created_timestamp"),
            "seen_at": now(),
            "quote_mint": quote, "quote_decimals": c.get("quote_decimals"),
            "program": c.get("program"), "protocol": c.get("protocol"),
            "has_twitter": bool(c.get("twitter")), "has_telegram": bool(c.get("telegram")),
            "has_website": bool(c.get("website")), "description_length": len(desc),
            "initial_vsol": vsol, "initial_market_cap": c.get("market_cap"),
            "initial_real_sol": c.get("real_sol_reserves"),
            "buyin_threshold": thr,           # recorded so every admission decision is auditable
            "arm": arm or "untracked",
            "admit_p": (CONTROL_RATE * p) if arm == "control" else p,
            "admitted": admitted,
        }

        if admitted:
            with self.lock:
                if len(self.tracked) >= ACTIVE_CAP:
                    row["admitted"] = False
                    row["dropped_full"] = True
                    self.stats["dropped_full"] += 1
                else:
                    self.tracked[mint] = {"first_seen": row["seen_at"], "next_at": now(),
                                          "until": now() + TRACK_FOR, "arm": arm}
                    self.stats["track_" + arm] += 1
        self.launches.write(row)
        self.stats["launches"] += 1

    # -- individual polling -------------------------------------------------
    @staticmethod
    def interval(age):
        for _a, b, step in SCHEDULE:
            if age < b:
                return step
        return None

    def poll_loop(self):
        while not self.stop:
            t = now()
            pick = None
            with self.lock:
                for m, s in list(self.tracked.items()):
                    if t > s["until"]:
                        del self.tracked[m]
                due = [(s["next_at"], m) for m, s in self.tracked.items() if s["next_at"] <= t]
                if due:
                    due.sort()
                    pick = due[0][1]
                    self.tracked[pick]["next_at"] = t + 1e6   # claim it; real time set after the poll
            if pick is None:
                time.sleep(0.2)
                continue
            self.poll_one(pick)

    def poll_one(self, mint):
        with self.lock:
            s = self.tracked.get(mint)
            if s is None:
                return
            arm, first = s["arm"], s["first_seen"]
        d, err = get(API + "/coins/" + mint)
        t = now()
        age = t - first
        step = self.interval(age)
        with self.lock:
            s = self.tracked.get(mint)
            if s is not None:
                if step is None:
                    del self.tracked[mint]
                else:
                    s["next_at"] = t + step
        if err:
            self.stats["poll_errors"] += 1
            self.polls.write({"mint": mint, "t": t, "age": round(age, 2), "arm": arm, "error": err})
            return
        row = {"mint": mint, "t": t, "age": round(age, 2), "arm": arm}
        for k in CURVE_FIELDS:
            row[k] = d.get(k)
        self.polls.write(row)
        self.stats["polls"] += 1

    def report_loop(self):
        t0 = now()
        while not self.stop:
            time.sleep(60)
            el = now() - t0
            s = self.stats
            with self.lock:
                active = len(self.tracked)
            per_day = s["launches"] * 86400 / max(el, 1)
            print("[%5.2fh] launches=%d (%.0f/day) track sol/alt/ctl=%d/%d/%d active=%d "
                  "polls=%d err=%d/%d full=%d rate=%.2f/s"
                  % (el / 3600, s["launches"], per_day, s["track_filtered_sol"],
                     s["track_filtered_alt"], s["track_control"], active, s["polls"],
                     s["feed_errors"], s["poll_errors"], s["dropped_full"], LIM.rate), flush=True)

    def run(self):
        for fn in (self.feed_loop, self.poll_loop, self.report_loop):
            threading.Thread(target=fn, daemon=True).start()
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            self.stop = True
            print("stopping", flush=True)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "data"
    print("budget: %d polls/mint over %ds; cap %d active; limit %.2f req/s"
          % (POLLS_PER_MINT, TRACK_FOR, ACTIVE_CAP, RATE_LIMIT), flush=True)
    print("collecting into %s/  (launches.jsonl, polls.jsonl)  ctrl-c to stop" % out, flush=True)
    Collector(out).run()
