using System.Collections.Concurrent;
using System.Text.Json;

namespace SolastaBot.ChainCollector;

public sealed class Collector : IDisposable
{
    private static readonly string[] CurveFields =
    [
        "complete",
        "market_cap",
        "usd_market_cap",
        "market_cap_quote",
        "ath_market_cap",
        "ath_market_cap_timestamp",
        "virtual_sol_reserves",
        "virtual_token_reserves",
        "real_sol_reserves",
        "real_token_reserves",
        "total_supply",
        "last_trade_timestamp",
        "reply_count",
        "pool_address"
    ];

    private readonly object syncRoot = new();
    private readonly CollectorOptions options;
    private readonly PumpFunClient client;
    private readonly AdaptiveRateLimiter rateLimiter;
    private readonly JsonLineWriter launchesWriter;
    private readonly JsonLineWriter pollsWriter;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly Dictionary<string, TrackedMint> tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<decimal>> baselines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> statistics = new(StringComparer.Ordinal);
    private readonly Random random = new(20260912);

    public Collector(string outputDirectory, CollectorOptions options, PumpFunClient client, AdaptiveRateLimiter rateLimiter)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        }

        this.options = options ?? throw new ArgumentNullException(nameof(options));

        this.client = client ?? throw new ArgumentNullException(nameof(client));

        this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

        launchesWriter = new(Path.Combine(outputDirectory, "launches.jsonl"));

        pollsWriter = new(Path.Combine(outputDirectory, "polls.jsonl"));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task feedTask = FeedLoopAsync(linkedCancellation.Token);

        Task pollTask = PollLoopAsync(linkedCancellation.Token);

        Task reportTask = ReportLoopAsync(linkedCancellation.Token);

        await Task.WhenAny(feedTask, pollTask, reportTask);

        linkedCancellation.Cancel();

        await Task.WhenAll(feedTask, pollTask, reportTask);
    }

    public void Dispose()
    {
        launchesWriter.Dispose();
        pollsWriter.Dispose();
    }

    private async Task FeedLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double startedAt = NowSeconds();

            PumpRequestResult result = await client.GetAsync(options.FeedUrl, cancellationToken);

            if (result.Error is not null)
            {
                Increment("feed_errors");
            }
            else
            {
                Increment("feed");

                if (result.Data is JsonElement { ValueKind: JsonValueKind.Array } data)
                {
                    foreach (JsonElement coin in data.EnumerateArray())
                    {
                        OnCoin(coin);
                    }
                }
            }

            double elapsed = NowSeconds() - startedAt;

            double delaySeconds = Math.Max(0d, options.FeedEverySeconds - elapsed);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        }
    }

    private void OnCoin(JsonElement coin)
    {
        string? mint = JsonElementReader.GetString(coin, "mint");

        if (string.IsNullOrEmpty(mint))
        {
            return;
        }

        lock (syncRoot)
        {
            if (!seen.Add(mint))
            {
                return;
            }

            seenOrder.Enqueue(mint);

            while (seenOrder.Count > options.SeenCapacity)
            {
                string expiredMint = seenOrder.Dequeue();

                seen.Remove(expiredMint);
            }
        }

        decimal rawVirtualReserve = JsonElementReader.GetDecimal(coin, "virtual_sol_reserves");

        decimal virtualQuoteReserve = rawVirtualReserve == 0m ? 0m : rawVirtualReserve / 1_000_000_000m;

        string? quoteMint = JsonElementReader.GetString(coin, "quote_mint");

        string quoteKey = quoteMint ?? string.Empty;

        decimal? threshold;

        lock (syncRoot)
        {
            if (!baselines.TryGetValue(quoteKey, out Queue<decimal>? baseline))
            {
                baseline = new();
                baselines.Add(quoteKey, baseline);
            }

            baseline.Enqueue(virtualQuoteReserve);

            while (baseline.Count > options.BaselineCapacity)
            {
                baseline.Dequeue();
            }

            threshold = CollectorPolicy.CalculateThreshold(baseline, options.BaselineMinimum, options.BuyInRatio);
        }

        bool hasTelegram = JsonElementReader.IsTruthy(coin, "telegram");

        bool hasTwitter = JsonElementReader.IsTruthy(coin, "twitter");

        string? arm = CollectorPolicy.Classify(quoteMint, hasTelegram, hasTwitter, virtualQuoteReserve, threshold, options.SolQuoteMint);

        if (arm is null && random.NextDouble() < options.ControlRate)
        {
            arm = "control";
        }

        double admissionRate = arm is not null && options.AdmissionRates.TryGetValue(arm, out double configuredAdmissionRate) ? configuredAdmissionRate : 0d;

        bool admitted = arm is not null && (admissionRate >= 1d || random.NextDouble() < admissionRate);

        string? description = JsonElementReader.GetString(coin, "description");

        double seenAt = NowSeconds();

        Dictionary<string, object?> row = new(StringComparer.Ordinal)
            {
                ["mint"] = mint,
                ["symbol"] = JsonElementReader.GetValue(coin, "symbol"),
                ["name"] = JsonElementReader.GetValue(coin, "name"),
                ["creator"] = JsonElementReader.GetValue(coin, "creator"),
                ["created_timestamp"] = JsonElementReader.GetValue(coin, "created_timestamp"),
                ["seen_at"] = seenAt,
                ["quote_mint"] = JsonElementReader.GetValue(coin, "quote_mint"),
                ["quote_decimals"] = JsonElementReader.GetValue(coin, "quote_decimals"),
                ["program"] = JsonElementReader.GetValue(coin, "program"),
                ["protocol"] = JsonElementReader.GetValue(coin, "protocol"),
                ["has_twitter"] = hasTwitter,
                ["has_telegram"] = hasTelegram,
                ["has_website"] = JsonElementReader.IsTruthy(coin, "website"),
                ["description_length"] = description?.Length ?? 0,
                ["initial_vsol"] = virtualQuoteReserve,
                ["initial_market_cap"] = JsonElementReader.GetValue(coin, "market_cap"),
                ["initial_real_sol"] = JsonElementReader.GetValue(coin, "real_sol_reserves"),
                ["buyin_threshold"] = threshold,
                ["arm"] = arm ?? "untracked",
                ["admit_p"] = string.Equals(arm, "control", StringComparison.Ordinal) ? options.ControlRate * admissionRate : admissionRate,
                ["admitted"] = admitted
            };

        if (admitted &&
            arm is not null)
        {
            lock (syncRoot)
            {
                if (tracked.Count >= options.ActiveCap)
                {
                    row["admitted"] = false;
                    row["dropped_full"] = true;

                    Increment("dropped_full");
                }
                else
                {
                    double currentTime = NowSeconds();

                    tracked[mint] = new(seenAt, currentTime, currentTime + options.TrackForSeconds, arm);

                    Increment($"track_{arm}");
                }
            }
        }

        launchesWriter.Write(row);

        Increment("launches");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double currentTime = NowSeconds();
            string? selectedMint = null;
            double selectedNextAt = double.MaxValue;
            List<string> expiredMints = [];

            lock (syncRoot)
            {
                foreach (KeyValuePair<string, TrackedMint> trackedMint in tracked)
                {
                    if (currentTime > trackedMint.Value.Until)
                    {
                        expiredMints.Add(trackedMint.Key);

                        continue;
                    }

                    bool due = trackedMint.Value.NextAt <= currentTime;

                    bool earlier = trackedMint.Value.NextAt < selectedNextAt;

                    bool sameTimeEarlierMint = trackedMint.Value.NextAt == selectedNextAt && (selectedMint is null || string.CompareOrdinal(trackedMint.Key, selectedMint) < 0);

                    if (due && (earlier || sameTimeEarlierMint))
                    {
                        selectedMint = trackedMint.Key;

                        selectedNextAt = trackedMint.Value.NextAt;
                    }
                }

                foreach (string expiredMint in expiredMints)
                {
                    tracked.Remove(expiredMint);
                }

                if (selectedMint is not null && tracked.TryGetValue(selectedMint, out TrackedMint? state))
                {
                    state.NextAt = double.PositiveInfinity;
                }
            }

            if (selectedMint is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200d), cancellationToken);

                continue;
            }

            await PollOneAsync(selectedMint, cancellationToken);
        }
    }

    private async Task PollOneAsync(string mint, CancellationToken cancellationToken)
    {
        string arm;
        double firstSeen;

        lock (syncRoot)
        {
            if (!tracked.TryGetValue(mint, out TrackedMint? state))
            {
                return;
            }

            arm = state.Arm;
            firstSeen = state.FirstSeen;
        }

        string url = $"{options.ApiBase}/coins/{Uri.EscapeDataString(mint)}";

        PumpRequestResult result = await client.GetAsync(url, cancellationToken);

        double currentTime = NowSeconds();

        double age =
            currentTime - firstSeen;

        int? intervalSeconds = CollectorPolicy.GetPollIntervalSeconds(age, options.Schedule);

        lock (syncRoot)
        {
            if (tracked.TryGetValue(mint, out TrackedMint? state))
            {
                if (intervalSeconds is null)
                {
                    tracked.Remove(mint);
                }
                else
                {
                    state.NextAt = currentTime + intervalSeconds.Value;
                }
            }
        }

        if (result.Error is not null)
        {
            Increment("poll_errors");

            Dictionary<string, object?> errorRow = new(StringComparer.Ordinal)
                {
                    ["mint"] = mint,
                    ["t"] = currentTime,
                    ["age"] = Math.Round(age, 2, MidpointRounding.ToEven),
                    ["arm"] = arm,
                    ["error"] = result.Error
                };

            pollsWriter.Write(errorRow);

            return;
        }

        if (result.Data is not JsonElement { ValueKind: JsonValueKind.Object } data)
        {
            Increment("poll_errors");

            Dictionary<string, object?> invalidRow = new(StringComparer.Ordinal)
                {
                    ["mint"] = mint,
                    ["t"] = currentTime,
                    ["age"] = Math.Round(age, 2, MidpointRounding.ToEven),
                    ["arm"] = arm,
                    ["error"] = "invalid"
                };

            pollsWriter.Write(invalidRow);

            return;
        }

        Dictionary<string, object?> row = new(StringComparer.Ordinal)
            {
                ["mint"] = mint,
                ["t"] = currentTime,
                ["age"] = Math.Round(age, 2, MidpointRounding.ToEven),
                ["arm"] = arm
            };

        foreach (string field in CurveFields)
        {
            row[field] = JsonElementReader.GetValue(data, field);
        }

        pollsWriter.Write(row);

        Increment("polls");
    }

    private async Task ReportLoopAsync(CancellationToken cancellationToken)
    {
        double startedAt = NowSeconds();

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(60d), cancellationToken);

            double elapsed = NowSeconds() - startedAt;

            long launchCount = GetStatistic("launches");

            double perDay = launchCount * 86_400d / Math.Max(elapsed, 1d);

            int activeCount;

            lock (syncRoot)
            {
                activeCount = tracked.Count;
            }

            string message = FormattableString.Invariant($"[{elapsed / 3600d,5:F2}h] launches={launchCount} ({perDay:F0}/day) track sol/alt/ctl={GetStatistic("track_filtered_sol")}/{GetStatistic("track_filtered_alt")}/{GetStatistic("track_control")} active={activeCount} polls={GetStatistic("polls")} err={GetStatistic("feed_errors")}/{GetStatistic("poll_errors")} full={GetStatistic("dropped_full")} rate={rateLimiter.CurrentRate:F2}/s");

            Console.WriteLine(message);
        }
    }

    private void Increment(string statisticName)
    {
        statistics.AddOrUpdate(statisticName, 1L, static (statisticKey, currentValue) => currentValue + 1L);
    }

    private long GetStatistic(string statisticName) => statistics.TryGetValue(statisticName, out long value) ? value : 0L;

    private static double NowSeconds() => (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds;
}