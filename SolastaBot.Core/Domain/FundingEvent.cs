using System;

namespace SolastaBot.Core.Domain;

/// <summary>
/// A funding settlement on a perpetual contract, normally every eight hours. A positive rate means
/// longs pay shorts. Ignoring these is the single most common way a perpetual backtest lies:
/// sustained trends can carry rates near 0.1% per settlement, roughly 9% per month.
/// </summary>
public readonly record struct FundingEvent(DateTime Time, decimal Rate);
