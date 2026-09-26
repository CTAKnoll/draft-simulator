using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DraftSimulator.Core;

public interface IRandomSource
{
    int NextInt(int exclusiveMaximum);
    double NextDouble();
}

public sealed class SeededRandomSource : IRandomSource
{
    private readonly Random _random;

    public SeededRandomSource(int seed) => _random = new Random(seed);

    public int NextInt(int exclusiveMaximum) => _random.Next(exclusiveMaximum);
    public double NextDouble() => _random.NextDouble();

    public static (int Seed, SeededRandomSource Source) CreateCryptographic()
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        RandomNumberGenerator.Fill(bytes);
        var seed = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return (seed, new SeededRandomSource(seed));
    }
}

internal static class RandomSourceExtensions
{
    public static void Shuffle<T>(this IRandomSource random, IList<T> values)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var swapIndex = random.NextInt(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }
}
