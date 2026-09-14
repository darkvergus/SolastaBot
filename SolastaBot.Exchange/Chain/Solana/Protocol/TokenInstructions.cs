using System.Buffers.Binary;
using Solnet.Rpc.Models;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public static class TokenInstructions
{
    public static AccountMeta Write(string key, bool signer = false) => AccountMeta.Writable(new(key), signer);
    public static AccountMeta Read(string key, bool signer = false) => AccountMeta.ReadOnly(new(key), signer);
    public static byte[] Amounts(ulong first, ulong second, bool volume = false)
    {
        byte[] bytes = new byte[volume ? 17 : 16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, first);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), second);
        return bytes;
    }
    public static TransactionInstruction CreateAta(string user, string mint, string tokenProgram) => new()
    {
        ProgramId = SolanaPrograms.Key(SolanaPrograms.AssociatedToken), Data = [1],
        Keys = [Write(user, true), Write(SolanaPrograms.Ata(user, mint, tokenProgram)), Read(user), Read(mint), Read(SolanaPrograms.System), Read(tokenProgram)]
    };
    public static TransactionInstruction Transfer(string user, string destination, ulong lamports)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), lamports);
        return new() { ProgramId = SolanaPrograms.Key(SolanaPrograms.System), Keys = [Write(user, true), Write(destination)], Data = bytes };
    }
    public static TransactionInstruction Sync(string account) => new() { ProgramId = SolanaPrograms.Key(SolanaPrograms.Token), Keys = [Write(account)], Data = [17] };
    public static TransactionInstruction Close(string account, string user) => new()
    {
        ProgramId = SolanaPrograms.Key(SolanaPrograms.Token), Keys = [Write(account), Write(user), Read(user, true)], Data = [9]
    };
}
