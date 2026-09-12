namespace SolastaBot.Core.Domain;

public enum PriceRounding
{
    /// <summary>Toward zero. Use for a buy limit, where a lower price is never worse.</summary>
    Down,

    /// <summary>Away from zero. Use for a sell limit, where a higher price is never worse.</summary>
    Up,

    Nearest
}