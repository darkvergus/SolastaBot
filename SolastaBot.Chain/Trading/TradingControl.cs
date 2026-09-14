namespace SolastaBot.Chain.Trading;

public sealed record TradingControl(bool HaltEntries = false, bool Flatten = false);
