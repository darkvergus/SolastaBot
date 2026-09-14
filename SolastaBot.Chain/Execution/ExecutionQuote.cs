namespace SolastaBot.Chain.Execution;

public sealed record ExecutionQuote(decimal BaseReserve, decimal QuoteReserve, decimal RealBaseReserve, decimal RealQuoteReserve,
    IReadOnlyList<decimal> FeeBasisPoints, IReadOnlyDictionary<string, string> Accounts, string ProtocolCommit);
