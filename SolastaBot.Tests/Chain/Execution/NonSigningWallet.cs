using SolastaBot.Exchange.Chain.Solana.Interfaces;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolastaBot.Tests.Chain.Execution;

internal sealed class NonSigningWallet : ITransactionSigner
{
    public string Address { get; } = new Account().PublicKey.Key;

    public byte[] Sign(string blockhash, IReadOnlyList<TransactionInstruction> instructions) => throw new InvalidOperationException("Simulation must not call the signer.");
}
