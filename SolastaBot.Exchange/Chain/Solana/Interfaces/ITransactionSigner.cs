using Solnet.Rpc.Models;

namespace SolastaBot.Exchange.Chain.Solana.Interfaces;

public interface ITransactionSigner
{
    string Address { get; }
    byte[] Sign(string blockhash, IReadOnlyList<TransactionInstruction> instructions);
}
