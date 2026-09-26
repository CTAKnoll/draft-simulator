using System.Buffers.Binary;
using DraftSimulator.Protocol;

namespace DraftSimulator.Protocol.Tests;

public sealed class AssetChunkCodecTests
{
    private static readonly SessionId Session = new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
    private static readonly AssetHash Hash = new(new string('b', 64));

    [Fact]
    public void RoundTripUsesExactBigEndianHeader()
    {
        var validation = Validation(6);
        var chunk = new AssetChunk(Session, Hash, 1, 2, [5, 6]);
        var frame = AssetChunkCodec.Write(chunk, validation);

        Assert.Equal("DSAS"u8.ToArray(), frame[..4]);
        Assert.Equal(1, frame[4]);
        Assert.Equal(1, frame[5]);
        Assert.Equal([0, 0], frame[6..8]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), frame[8..24]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(56, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(60, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(64, 4)));
        var parsed = AssetChunkCodec.Parse(frame, validation);
        Assert.Equal(chunk.SessionId, parsed.SessionId);
        Assert.Equal(chunk.AssetHash, parsed.AssetHash);
        Assert.Equal(chunk.ChunkIndex, parsed.ChunkIndex);
        Assert.Equal(chunk.ChunkCount, parsed.ChunkCount);
        Assert.Equal(chunk.Payload, parsed.Payload);
    }

    [Fact]
    public void TruncationAndDeclaredLengthMismatchAreRejected()
    {
        var validation = Validation(6);
        var frame = AssetChunkCodec.Write(new AssetChunk(Session, Hash, 0, 2, [1, 2, 3, 4]), validation);
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Parse(frame[..67], validation));
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Parse(frame[..^1], validation));
        frame[67] = 5;
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Parse(frame, validation));
    }

    [Fact]
    public void WrongMagicVersionTypeAndReservedBytesAreRejected()
    {
        var validation = Validation(4);
        var original = AssetChunkCodec.Write(new AssetChunk(Session, Hash, 0, 1, [1, 2, 3, 4]), validation);
        foreach (var (offset, value) in new[] { (0, (byte)'X'), (4, (byte)2), (5, (byte)2), (6, (byte)1) })
        {
            var frame = original.ToArray();
            frame[offset] = value;
            Assert.Throws<ProtocolException>(() => AssetChunkCodec.Parse(frame, validation));
        }
    }

    [Fact]
    public void WrongSessionUnknownHashAndChunkBoundsAreRejected()
    {
        var validation = Validation(6);
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(new SessionId(Guid.NewGuid()), Hash, 0, 2, new byte[4]), validation));
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(Session, new AssetHash(new string('c', 64)), 0, 2, new byte[4]), validation));
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(Session, Hash, 2, 2, new byte[4]), validation));
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(Session, Hash, 0, 3, new byte[4]), validation));
    }

    [Fact]
    public void OversizedAndIncorrectFinalPayloadsAreRejected()
    {
        var validation = Validation(6);
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(Session, Hash, 0, 2, new byte[5]), validation));
        Assert.Throws<ProtocolException>(() => AssetChunkCodec.Write(new AssetChunk(Session, Hash, 1, 2, new byte[3]), validation));
    }

    private static AssetChunkValidation Validation(long length) => new(Session, new Dictionary<AssetHash, long> { [Hash] = length }, 4);
}
