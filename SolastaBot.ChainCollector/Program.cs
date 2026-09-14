using SolastaBot.ChainCollector;

string outputDirectory = args.Length > 0 ? args[0] : "data";

CollectorOptions options = new();

using HttpClient httpClient = new();

httpClient.Timeout = TimeSpan.FromSeconds(25d);

AdaptiveRateLimiter rateLimiter = new(options.RateLimit);

PumpFunClient client = new(httpClient, rateLimiter);

using Collector collector = new(outputDirectory, options, client, rateLimiter);

using CancellationTokenSource cancellationTokenSource = new();

ConsoleCancelEventHandler cancelHandler = (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

Console.CancelKeyPress += cancelHandler;

Console.WriteLine($"budget: {options.PollsPerMint} polls/mint over {options.TrackForSeconds}s; cap {options.ActiveCap} active; limit {options.RateLimit:F2} req/s");

Console.WriteLine($"collecting into {outputDirectory}/  (launches.jsonl, polls.jsonl)  ctrl-c to stop");

try
{
    await collector.RunAsync(cancellationTokenSource.Token);
}
catch (OperationCanceledException)when (cancellationTokenSource.IsCancellationRequested)
{
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

Console.WriteLine("stopping");