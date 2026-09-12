namespace SolastaBot.ChainCollector;

internal sealed class TrackedMint(double firstSeen, double nextAt, double until, string arm)
{
    public double FirstSeen { get; } = firstSeen;

    public double NextAt { get; set; } = nextAt;

    public double Until { get; } = until;

    public string Arm { get; } = arm;
}