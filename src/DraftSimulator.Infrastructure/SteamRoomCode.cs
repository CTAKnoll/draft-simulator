using System.Security.Cryptography;
using System.Text;

namespace DraftSimulator.Infrastructure;

public static class SteamRoomCode
{
    public const int Length = 10;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<byte> random = stackalloc byte[Length];
        RandomNumberGenerator.Fill(random);

        Span<char> code = stackalloc char[Length];
        for (var index = 0; index < code.Length; index++)
            code[index] = Alphabet[random[index] & 31];

        return new string(code);
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Span<char> normalized = stackalloc char[Length];
        var length = 0;
        foreach (var character in value)
        {
            if (character is ' ' or '-')
                continue;

            if (length == Length)
                throw new FormatException("Room codes must contain exactly 10 Crockford Base32 characters.");

            var upper = character is >= 'a' and <= 'z' ? (char)(character - ('a' - 'A')) : character;
            if (!Alphabet.Contains(upper, StringComparison.Ordinal))
                throw new FormatException("Room codes may contain only Crockford Base32 characters.");
            normalized[length++] = upper;
        }

        if (length != Length)
            throw new FormatException("Room codes must contain exactly 10 Crockford Base32 characters.");

        return new string(normalized);
    }

    public static bool TryNormalize(string? value, out string normalized)
    {
        try
        {
            normalized = Normalize(value!);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentNullException or FormatException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static string Format(string normalizedCode)
    {
        var normalized = Normalize(normalizedCode);
        return string.Concat(normalized.AsSpan(0, 5), "-", normalized.AsSpan(5, 5));
    }

    public static string Hash(string roomCode)
    {
        var normalized = Normalize(roomCode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static bool HashMatches(string roomCode, string expectedLowercaseHexHash)
    {
        ArgumentNullException.ThrowIfNull(expectedLowercaseHexHash);
        if (expectedLowercaseHexHash.Length != SHA256.HashSizeInBytes * 2)
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedLowercaseHexHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(roomCode)));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
