using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SolastaBot.Exchange.Chain.Solana.Interfaces;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class FileWalletSigner : ITransactionSigner
{
    private readonly Account account;
    public string Address => account.PublicKey.Key;

    public FileWalletSigner(string path)
    {
        ValidatePath(path);
        VerifyPermissions(path);
        byte[] secret = [];
        try
        {
            int[] numbers = JsonSerializer.Deserialize<int[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing keypair.");
            if (numbers.Length != 64 || numbers.Any(number => number is < 0 or > 255))
            {
                throw new InvalidDataException("Expected a Solana 64-byte keypair JSON array.");
            }

            secret = [.. numbers.Select(number => (byte)number)];
            Array.Clear(numbers);
            account = new([.. secret], secret[32..]);
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            if (!account.PublicKey.Verify(challenge, account.Sign(challenge)))
            {
                throw new InvalidDataException("Keypair public key does not match its signing key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public byte[] Sign(string blockhash, IReadOnlyList<TransactionInstruction> instructions)
    {
        TransactionBuilder builder = new TransactionBuilder().SetFeePayer(account.PublicKey).SetRecentBlockHash(blockhash);
        foreach (TransactionInstruction instruction in instructions)
        {
            builder.AddInstruction(instruction);
        }

        return builder.Build(account);
    }

    public static string Create(string path)
    {
        ValidatePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FileStream created;
        if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Missing Windows identity.");
            FileSecurity security = new();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(owner);
            security.AddAccessRule(new(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            created = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        }
        else
        {
            created = new(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
        using FileStream stream = created;
        Account generated = new();
        JsonSerializer.Serialize(stream, generated.PrivateKey.KeyBytes.Select(value => (int)value).ToArray());
        stream.Flush(true);
        return generated.PublicKey.Key;
    }

    private static void ValidatePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        DirectoryInfo? directory = new(Path.GetDirectoryName(fullPath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, "SolastaBot.slnx")))
            {
                throw new ArgumentException("Wallet keypairs must be stored outside the repository.");
            }

            directory = directory.Parent;
        }
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Wallet must not be a symbolic link.");
        }
    }

    private static void VerifyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode permissions = File.GetUnixFileMode(path);
            if ((permissions & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            {
                throw new UnauthorizedAccessException("Wallet file requires mode 600.");
            }

            return;
        }
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Missing Windows identity.");
        AuthorizationRuleCollection rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(owner) &&
                !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
                !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
            {
                throw new UnauthorizedAccessException("Wallet ACL grants access to another identity. Use wallet-create to create a protected keypair.");
            }
        }
    }
}
