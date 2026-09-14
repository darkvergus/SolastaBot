using SolastaBot.Chain.Trading;

namespace SolastaBot.Host.Chain;

public sealed record PaperWorkerSettings(string StateDirectory, string SettingsHash, PaperTradingOptions Trading);
