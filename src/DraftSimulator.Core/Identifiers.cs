using System.Security.Cryptography;

namespace DraftSimulator.Core;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CardDefinitionId(Guid Value)
{
    public static CardDefinitionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CardInstanceId(Guid Value)
{
    public static CardInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PackId(Guid Value)
{
    public static PackId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct AssetHash
{
    public const int ByteLength = 32;
    public const int HexLength = ByteLength * 2;

    public AssetHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != HexLength || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An asset hash must be a 64-character SHA-256 hexadecimal value.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static AssetHash Compute(ReadOnlySpan<byte> bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));

    public static bool TryParse(string? value, out AssetHash hash)
    {
        if (value is not null && value.Length == HexLength && value.All(Uri.IsHexDigit))
        {
            hash = new AssetHash(value);
            return true;
        }

        hash = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}
