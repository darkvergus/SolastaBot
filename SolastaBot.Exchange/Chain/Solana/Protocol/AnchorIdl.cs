using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public sealed class AnchorIdl
{
    private readonly JsonElement root;
    public string Program => root.GetProperty("address").GetString()!;

    public AnchorIdl(string name)
    {
        using Stream stream = typeof(AnchorIdl).Assembly.GetManifestResourceStream($"SolastaBot.Exchange.Chain.Solana.Protocol.Fixtures.{name}.json")
            ?? throw new InvalidDataException("Missing pinned IDL.");
        using JsonDocument document = JsonDocument.Parse(stream);
        root = document.RootElement.Clone();
    }

    public Dictionary<string, object> Decode(string name, SolanaAccount account)
    {
        if (account.Owner != Program || account.Executable)
        {
            throw new InvalidDataException($"Unexpected owner for {name}.");
        }

        JsonElement definition = root.GetProperty("accounts").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        byte[] discriminator = definition.GetProperty("discriminator").EnumerateArray().Select(value => value.GetByte()).ToArray();
        if (account.Data.Length < 8 || !account.Data.AsSpan(0, 8).SequenceEqual(discriminator))
        {
            throw new InvalidDataException($"Invalid {name} discriminator.");
        }

        int offset = 8;
        Dictionary<string, object> fields = [];
        foreach (JsonElement field in Definition(name).GetProperty("fields").EnumerateArray())
        {
            if (offset == account.Data.Length)
            {
                break;
            }

            fields.Add(field.GetProperty("name").GetString()!, Read(field.GetProperty("type"), account.Data, ref offset));
        }
        return fields;
    }

    public TransactionInstruction Instruction(string name, IReadOnlyDictionary<string, string> accounts, byte[] arguments, IReadOnlyList<AccountMeta>? remaining = null)
    {
        JsonElement definition = root.GetProperty("instructions").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        List<AccountMeta> keys = [];
        foreach (JsonElement account in definition.GetProperty("accounts").EnumerateArray())
        {
            string accountName = account.GetProperty("name").GetString()!;
            string address = accounts.TryGetValue(accountName, out string? supplied) ? supplied : account.GetProperty("address").GetString()!;
            if (account.TryGetProperty("address", out JsonElement fixedAddress) && address != fixedAddress.GetString())
            {
                throw new InvalidDataException("Fixed program account mismatch.");
            }

            bool signer = account.TryGetProperty("signer", out JsonElement signerFlag) && signerFlag.GetBoolean();
            bool writable = account.TryGetProperty("writable", out JsonElement writableFlag) && writableFlag.GetBoolean();
            keys.Add(writable ? AccountMeta.Writable(new(address), signer) : AccountMeta.ReadOnly(new(address), signer));
        }
        if (remaining is not null)
        {
            keys.AddRange(remaining);
        }

        byte[] discriminator = definition.GetProperty("discriminator").EnumerateArray().Select(value => value.GetByte()).ToArray();
        return new() { ProgramId = SolanaPrograms.Key(Program), Keys = keys, Data = [.. discriminator, .. arguments] };
    }

    public static BigInteger Number(IReadOnlyDictionary<string, object> fields, string name) => fields.TryGetValue(name, out object? value) ? (BigInteger)value : BigInteger.Zero;
    public static string Address(IReadOnlyDictionary<string, object> fields, string name) => (string)fields[name];
    public static bool Flag(IReadOnlyDictionary<string, object> fields, string name) => fields.TryGetValue(name, out object? value) && (bool)value;

    private JsonElement Definition(string name) => root.GetProperty("types").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("type");

    private object Read(JsonElement type, byte[] data, ref int offset)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            string primitive = type.GetString()!;
            int count = primitive switch { "bool" or "u8" => 1, "u16" => 2, "u32" => 4, "u64" or "i64" => 8, "u128" or "i128" => 16, "pubkey" => 32, _ => throw new InvalidDataException($"Unsupported IDL type {primitive}.") };
            if (offset + count > data.Length)
            {
                throw new InvalidDataException("Truncated protocol account.");
            }

            ReadOnlySpan<byte> bytes = data.AsSpan(offset, count);
            offset += count;
            if (primitive == "pubkey")
            {
                return new PublicKey(bytes).Key;
            }

            if (primitive == "bool")
            {
                return bytes[0] <= 1 ? bytes[0] == 1 : throw new InvalidDataException("Invalid boolean.");
            }

            return new BigInteger(bytes, !primitive.StartsWith('i'), false);
        }
        if (type.TryGetProperty("defined", out JsonElement defined))
        {
            Dictionary<string, object> values = [];
            foreach (JsonElement field in Definition(defined.GetProperty("name").GetString()!).GetProperty("fields").EnumerateArray())
            {
                values.Add(field.GetProperty("name").GetString()!, Read(field.GetProperty("type"), data, ref offset));
            }

            return values;
        }
        JsonElement element;
        int length;
        if (type.TryGetProperty("vec", out element))
        {
            if (offset + 4 > data.Length)
            {
                throw new InvalidDataException("Truncated vector.");
            }

            length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
            offset += 4;
        }
        else
        {
            JsonElement array = type.GetProperty("array");
            element = array[0];
            length = array[1].GetInt32();
        }
        if (length > 10000)
        {
            throw new InvalidDataException("Oversized protocol vector.");
        }

        List<object> items = [];
        for (int index = 0; index < length; index++)
        {
            items.Add(Read(element, data, ref offset));
        }

        return items;
    }
}
