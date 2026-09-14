using System.Text;
using Solnet.Wallet;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public static class SolanaPrograms
{
    public const string Pump = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
    public const string Amm = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
    public const string Fees = "pfeeUxB6jkeY1Hxd7CsFCAjcbHA9rWtchMGdZ6VojVZ";
    public const string System = "11111111111111111111111111111111";
    public const string Token = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    public const string Token2022 = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    public const string AssociatedToken = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";
    public const string WrappedSol = "So11111111111111111111111111111111111111112";

    public static string Pda(string program, params byte[][] seeds)
    {
        if (!PublicKey.TryFindProgramAddress(seeds, new(program), out PublicKey address, out byte bump))
        {
            throw new InvalidDataException("Cannot derive program address.");
        }

        return address.Key;
    }

    public static byte[] Seed(string value) => Encoding.UTF8.GetBytes(value);
    public static byte[] Key(string value) => new PublicKey(value).KeyBytes;
    public static string Curve(string mint) => Pda(Pump, Seed("bonding-curve"), Key(mint));
    public static string PoolAuthority(string mint) => Pda(Pump, Seed("pool-authority"), Key(mint));
    public static string Pool(string mint) => Pda(Amm, Seed("pool"), [0, 0], Key(PoolAuthority(mint)), Key(mint), Key(WrappedSol));
    public static string Ata(string owner, string mint, string tokenProgram) => Pda(AssociatedToken, Key(owner), Key(tokenProgram), Key(mint));
}
