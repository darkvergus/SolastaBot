namespace SolastaBot.Core.Domain;

/// <summary>Which way a position faces. Quantities are always stored positive; this carries the sign.</summary>
public enum PositionSide
{
    Flat,
    Long,
    Short
}