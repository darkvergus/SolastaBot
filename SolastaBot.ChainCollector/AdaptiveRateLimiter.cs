namespace SolastaBot.ChainCollector;

public sealed class AdaptiveRateLimiter
{
    private readonly object syncRoot = new();
    private readonly double baseRate;

    private double currentRate;
    private DateTimeOffset nextAt = DateTimeOffset.MinValue;

    public AdaptiveRateLimiter(double rate)
    {
        if (rate <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        baseRate = rate;
        currentRate = rate;
    }

    public double CurrentRate
    {
        get
        {
            lock (syncRoot)
            {
                return currentRate;
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;

        lock (syncRoot)
        {
            DateTimeOffset currentTime = DateTimeOffset.UtcNow;

            delay = nextAt > currentTime ? nextAt - currentTime : TimeSpan.Zero;
            
            DateTimeOffset anchor = nextAt > currentTime ? nextAt : currentTime;

            nextAt = anchor.AddSeconds(1d / currentRate);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    public void Penalise()
    {
        lock (syncRoot)
        {
            currentRate = Math.Max(0.15d, currentRate * 0.5d);
            nextAt = DateTimeOffset.UtcNow.AddSeconds(30d);
        }
    }

    public void Recover()
    {
        lock (syncRoot)
        {
            if (currentRate < baseRate)
            {
                currentRate = Math.Min(baseRate, currentRate * 1.05d);
            }
        }
    }
}