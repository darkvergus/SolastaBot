using System.Buffers.Binary;
using Solnet.Wallet;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public static class TokenAccounts
{
    public static ulong MintSupply(SolanaAccount mint)
    {
        if (mint.Owner is not (SolanaPrograms.Token or SolanaPrograms.Token2022) || mint.Executable || mint.Data.Length < 82 || mint.Data[45] != 1)
        {
            throw new InvalidDataException("Unsupported or uninitialized mint.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(mint.Data.AsSpan(46)) != 0)
        {
            throw new InvalidDataException("Freeze authority is not supported.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(mint.Data) != 0)
        {
            throw new InvalidDataException("Mint authority must be revoked before entry.");
        }

        if (mint.Owner == SolanaPrograms.Token && mint.Data.Length != 82)
        {
            throw new InvalidDataException("Invalid legacy mint size.");
        }

        if (mint is { Owner: SolanaPrograms.Token2022, Data.Length: > 82 })
        {
            if (mint.Data.Length < 166 || mint.Data[165] != 1)
            {
                throw new InvalidDataException("Invalid Token-2022 mint type.");
            }

            int offset = 166;
            while (offset < mint.Data.Length)
            {
                if (mint.Data.AsSpan(offset).IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }

                if (offset + 4 > mint.Data.Length)
                {
                    throw new InvalidDataException("Truncated mint extension.");
                }

                ushort extension = BinaryPrimitives.ReadUInt16LittleEndian(mint.Data.AsSpan(offset));
                ushort length = BinaryPrimitives.ReadUInt16LittleEndian(mint.Data.AsSpan(offset + 2));
                if (extension is not (18 or 19))
                {
                    throw new InvalidDataException($"Unsupported Token-2022 mint extension {extension}.");
                }

                offset = checked(offset + 4 + length);
                if (offset > mint.Data.Length)
                {
                    throw new InvalidDataException("Truncated mint extension value.");
                }
            }
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(mint.Data.AsSpan(36));
    }

    public static ulong Balance(SolanaAccount account, string mint, string owner, string tokenProgram)
    {
        if (account.Owner != tokenProgram || account.Data.Length < 165 || account.Executable || account.Data[108] != 1 ||
            new PublicKey(account.Data.AsSpan(0, 32)).Key != mint || new PublicKey(account.Data.AsSpan(32, 32)).Key != owner)
        {
            throw new InvalidDataException("Token account owner, mint or state mismatch.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(account.Data.AsSpan(64));
    }
}
