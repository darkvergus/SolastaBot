namespace SolastaBot.Core.Domain;

/// <summary>
/// One closed price bar. Every <see cref="DateTime"/> in SolastaBot is UTC by contract; the data
/// and exchange layers are responsible for converting exchange epoch milliseconds on the way in.
/// </summary>
public readonly record struct Candle(DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)
{
    public bool IsWellFormed => High >= Low && High >= Open && High >= Close && Low <= Open && Low <= Close && Open > 0m && Low > 0m && Volume >= 0m;
}
