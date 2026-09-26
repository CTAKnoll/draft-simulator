using System.Buffers.Binary;

namespace DraftSimulator.Protocol;

public sealed record AssetChunk(SessionId SessionId, AssetHash AssetHash, uint ChunkIndex, uint ChunkCount, byte[] Payload);

public sealed record AssetChunkValidation(SessionId SessionId, IReadOnlyDictionary<AssetHash, long> AssetLengths, int TransferChunkBytes);

public static class AssetChunkCodec
{
    public const int HeaderLength = 68;
    private static ReadOnlySpan<byte> Magic => "DSAS"u8;

    public static byte[] Write(AssetChunk chunk, AssetChunkValidation validation)
    {
        ValidateChunk(chunk, validation);
        var result = new byte[checked(HeaderLength + chunk.Payload.Length)];
        Magic.CopyTo(result);
        result[4] = 1;
        result[5] = 1;
        chunk.SessionId.Value.TryWriteBytes(result.AsSpan(8, 16), bigEndian: true, out _);
        chunk.AssetHash.ToBytes().CopyTo(result, 24);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(56, 4), chunk.ChunkIndex);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(60, 4), chunk.ChunkCount);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(64, 4), checked((uint)chunk.Payload.Length));
        chunk.Payload.CopyTo(result, HeaderLength);
        return result;
    }

    public static AssetChunk Parse(ReadOnlySpan<byte> frame, AssetChunkValidation validation)
    {
        if (frame.Length < HeaderLength) throw new ProtocolException("Asset frame is truncated.");
        if (!frame[..4].SequenceEqual(Magic)) throw new ProtocolException("Invalid asset frame magic.");
        if (frame[4] != 1) throw new ProtocolException($"Unsupported binary protocol version {frame[4]}.");
        if (frame[5] != 1) throw new ProtocolException($"Unknown binary message type {frame[5]}.");
        if (frame[6] != 0 || frame[7] != 0) throw new ProtocolException("Asset frame reserved bytes must be zero.");

        var sessionId = new SessionId(new Guid(frame.Slice(8, 16), bigEndian: true));
        var hash = AssetHash.FromBytes(frame.Slice(24, 32));
        var index = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(56, 4));
        var count = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(60, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(64, 4));
        if (payloadLength > int.MaxValue || frame.Length != HeaderLength + (int)payloadLength)
            throw new ProtocolException("Asset frame payload length does not match the frame length.");

        var chunk = new AssetChunk(sessionId, hash, index, count, frame[HeaderLength..].ToArray());
        ValidateChunk(chunk, validation);
        return chunk;
    }

    private static void ValidateChunk(AssetChunk chunk, AssetChunkValidation validation)
    {
        if (validation.TransferChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(validation));
        if (chunk.SessionId != validation.SessionId) throw new ProtocolException("Asset frame belongs to a different session.");
        if (!validation.AssetLengths.TryGetValue(chunk.AssetHash, out var assetLength))
            throw new ProtocolException("Asset hash is not in the active manifest.");
        if (assetLength <= 0) throw new ProtocolException("Manifest asset length must be positive.");

        var expectedCount = checked((uint)((assetLength + validation.TransferChunkBytes - 1) / validation.TransferChunkBytes));
        if (chunk.ChunkCount != expectedCount || chunk.ChunkIndex >= chunk.ChunkCount)
            throw new ProtocolException("Asset chunk count or index is out of range.");
        var expectedPayloadLength = chunk.ChunkIndex + 1 == chunk.ChunkCount
            ? checked((int)(assetLength - (long)chunk.ChunkIndex * validation.TransferChunkBytes))
            : validation.TransferChunkBytes;
        if (chunk.Payload.Length != expectedPayloadLength || chunk.Payload.Length > validation.TransferChunkBytes)
            throw new ProtocolException("Asset chunk payload length is invalid for its index.");
    }
}
