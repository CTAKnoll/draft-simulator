using System.Security.Cryptography;
using System.Text;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class SteamRoomCodeTests
{
    [Fact]
    public void GeneratesTenCrockfordBase32Characters()
    {
        var codes = Enumerable.Range(0, 100).Select(_ => SteamRoomCode.Generate()).ToArray();

        Assert.All(codes, code =>
        {
            Assert.Equal(SteamRoomCode.Length, code.Length);
            Assert.All(code, character => Assert.Contains(character, SteamRoomCode.Alphabet));
        });
        Assert.True(codes.Distinct(StringComparer.Ordinal).Count() > 1);
    }

    [Theory]
    [InlineData("01234-abcde", "01234ABCDE")]
    [InlineData(" 0 1 2 3 4 - A B C D E ", "01234ABCDE")]
    public void NormalizesAsciiSpacesHyphensAndCase(string input, string expected) =>
        Assert.Equal(expected, SteamRoomCode.Normalize(input));

    [Theory]
    [InlineData("01234ABCDEZ")]
    [InlineData("01234ABCD")]
    [InlineData("01234ABCDI")]
    [InlineData("01234ABCDL")]
    [InlineData("01234ABCDO")]
    [InlineData("01234ABCDU")]
    [InlineData("01234_ABCD")]
    [InlineData("01234\tABCD")]
    public void RejectsInvalidCodes(string input)
    {
        Assert.False(SteamRoomCode.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void FormatsAndHashesTheNormalizedCode()
    {
        const string normalized = "01234ABCDE";
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

        Assert.Equal("01234-ABCDE", SteamRoomCode.Format("01234 abcde"));
        Assert.Equal(expectedHash, SteamRoomCode.Hash("01234-abcde"));
        Assert.True(SteamRoomCode.HashMatches("01234 abcde", expectedHash));
        Assert.False(SteamRoomCode.HashMatches("01234 ABCDF", expectedHash));
        Assert.False(SteamRoomCode.HashMatches(normalized, new string('z', 64)));
    }
}
