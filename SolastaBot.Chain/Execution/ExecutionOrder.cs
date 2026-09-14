namespace SolastaBot.Chain.Execution;

public sealed record ExecutionOrder(OrderIntent Intent, OrderStatus Status, string Transaction, string Signature, ulong LastValidBlockHeight,
    ulong ReservedLamports, ulong MinimumOutput, string Route, ulong QuoteSlot, string? Detail = null, ExecutionQuote? Quote = null, ExecutionReceipt? Receipt = null)
{
    public bool Pending => Status is OrderStatus.Signed or OrderStatus.Submitted or OrderStatus.Unresolved;
}
