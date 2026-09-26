using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using DraftSimulator.Protocol;
using CoreAssetHash = DraftSimulator.Core.AssetHash;
using ProtocolAssetHash = DraftSimulator.Protocol.AssetHash;

namespace DraftSimulator.Infrastructure;

public class AssetTransferException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class InsufficientAssetDiskSpaceException(long requiredBytes, long availableBytes)
    : AssetTransferException($"Asset transfer requires {requiredBytes} free bytes, but only {availableBytes} are available.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}

public static class AssetManifestPager
{
    public static IReadOnlyList<AssetManifestDto> CreatePages(
        SessionAssetManifest manifest,
        long revision,
        SnapshotId? snapshotId = null,
        int pageSize = ProtocolConstants.MaxPageEntries)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidatePageSize(pageSize);
        var id = snapshotId ?? new SnapshotId(Guid.NewGuid());
        var pageCount = Math.Max(1, (manifest.Assets.Count + pageSize - 1) / pageSize);
        var pages = new List<AssetManifestDto>(pageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var assets = manifest.Assets.Skip(pageIndex * pageSize).Take(pageSize).Select(ToDto).ToArray();
            pages.Add(new(
                new SessionId(manifest.SessionId), manifest.ProtocolVersion, manifest.TransferChunkBytes,
                new PageMetadata(id, revision, pageIndex, pageCount), assets));
        }
        return pages;
    }

    private static AssetManifestEntryDto ToDto(AssetManifestEntry asset) => new(
        new ProtocolAssetHash(asset.Hash.Value), asset.EncodedByteLength, asset.PixelWidth, asset.PixelHeight,
        asset.MimeType, asset.ChunkCount);

    internal static void ValidatePageSize(int pageSize)
    {
        if (pageSize is <= 0 or > ProtocolConstants.MaxPageEntries)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
    }
}

public sealed record ManifestPageResult(bool IsComplete, SessionAssetManifest? Manifest, long Revision);

public sealed class AssetManifestAccumulator
{
    private SnapshotId? _snapshotId;
    private long _revision = long.MinValue;
    private SessionId _sessionId;
    private int _protocolVersion;
    private int _transferChunkBytes;
    private AssetManifestEntryDto[]?[]? _pages;

    public ManifestPageResult Add(AssetManifestDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Validate(page);

        if (page.Page.Revision < _revision)
            throw new AssetTransferException("The asset manifest page has a stale revision.");

        if (page.Page.Revision > _revision)
            Begin(page);
        else if (_snapshotId != page.Page.SnapshotId || _sessionId != page.SessionId ||
                  _protocolVersion != page.ProtocolVersion || _transferChunkBytes != page.TransferChunkBytes || _pages!.Length != page.Page.PageCount)
            throw new AssetTransferException("The asset manifest page conflicts with the active snapshot.");

        var existing = _pages![page.Page.PageIndex];
        if (existing is not null)
        {
            if (!existing.SequenceEqual(page.Assets))
                throw new AssetTransferException("A duplicate asset manifest page has conflicting contents.");
        }
        else
        {
            _pages[page.Page.PageIndex] = page.Assets.ToArray();
        }

        if (_pages.Any(x => x is null))
            return new(false, null, _revision);

        var assets = _pages.SelectMany(x => x!).Select(ToManifestEntry).ToArray();
        if (assets.Select(x => x.Hash).Distinct().Count() != assets.Length)
            throw new AssetTransferException("The completed asset manifest contains duplicate hashes.");
        foreach (var asset in assets)
        {
            var expected = checked((int)((asset.EncodedByteLength + _transferChunkBytes - 1) / _transferChunkBytes));
            if (asset.ChunkCount != expected)
                throw new AssetTransferException("The asset manifest chunk count is inconsistent with its chunk size.");
        }
        return new(true, new(_sessionId.Value, _protocolVersion, _transferChunkBytes, assets), _revision);
    }

    private void Begin(AssetManifestDto page)
    {
        _revision = page.Page.Revision;
        _snapshotId = page.Page.SnapshotId;
        _sessionId = page.SessionId;
        _protocolVersion = page.ProtocolVersion;
        _transferChunkBytes = page.TransferChunkBytes;
        _pages = new AssetManifestEntryDto[page.Page.PageCount][];
    }

    private static void Validate(AssetManifestDto page)
    {
        if (page.SessionId.Value == Guid.Empty || page.Page.SnapshotId.Value == Guid.Empty || page.ProtocolVersion <= 0 || page.TransferChunkBytes <= 0)
            throw new AssetTransferException("The asset manifest identity is invalid.");
        if (page.Page.PageCount <= 0 || page.Page.PageIndex < 0 || page.Page.PageIndex >= page.Page.PageCount)
            throw new AssetTransferException("The asset manifest page bounds are invalid.");
        if (page.Assets is null || page.Assets.Length > ProtocolConstants.MaxPageEntries)
            throw new AssetTransferException("The asset manifest page has too many entries.");
        foreach (var asset in page.Assets)
        {
            if (asset is null || asset.EncodedByteLength <= 0 || asset.PixelWidth <= 0 || asset.PixelHeight <= 0 ||
                asset.MimeType != "image/webp" || asset.ChunkCount <= 0)
                throw new AssetTransferException("The asset manifest contains an invalid entry.");
        }
    }

    public void Reset()
    {
        _snapshotId = null;
        _revision = long.MinValue;
        _sessionId = default;
        _protocolVersion = 0;
        _transferChunkBytes = 0;
        _pages = null;
    }

    private static AssetManifestEntry ToManifestEntry(AssetManifestEntryDto asset) => new(
        new CoreAssetHash(asset.Hash.Value), asset.EncodedByteLength, asset.PixelWidth, asset.PixelHeight,
        asset.MimeType, asset.ChunkCount);
}

public static class AssetNeedPager
{
    public static IReadOnlyList<AssetNeedDto> CreatePages(
        Guid sessionId,
        IEnumerable<CoreAssetHash> hashes,
        long revision,
        SnapshotId? snapshotId = null,
        int pageSize = ProtocolConstants.MaxPageEntries)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        AssetManifestPager.ValidatePageSize(pageSize);
        var values = hashes.Distinct().Select(x => new ProtocolAssetHash(x.Value)).ToArray();
        var pageCount = Math.Max(1, (values.Length + pageSize - 1) / pageSize);
        var id = snapshotId ?? new SnapshotId(Guid.NewGuid());
        return Enumerable.Range(0, pageCount)
            .Select(index => new AssetNeedDto(
                new SessionId(sessionId), new PageMetadata(id, revision, index, pageCount),
                values.Skip(index * pageSize).Take(pageSize).ToArray()))
            .ToArray();
    }
}

public static class AssetCacheValidator
{
    public static bool IsValid(string path, CoreAssetHash expectedHash, long expectedLength)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
                return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
                FileOptions.SequentialScan);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedHash.Value));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record AssetTransferProgress(
    long BytesReady, long TotalBytes, int AssetsReady, int TotalAssets)
{
    public bool IsComplete => AssetsReady == TotalAssets;
}

public sealed record AssetTransferPlan(IReadOnlyList<AssetNeedDto> NeedPages, AssetTransferProgress Progress);
public sealed record AssetChunkReceipt(CoreAssetHash Hash, int ChunkIndex, bool WasDuplicate, bool AssetCompleted, AssetTransferProgress Progress);

public sealed class ClientAssetTransferEngine
{
    public const long RequiredFreeSpaceReserve = AssetPreparer.RequiredFreeSpaceReserve;
    private static ReadOnlySpan<byte> BitsetMagic => "DSBM"u8;

    private readonly object _gate = new();
    private readonly SessionAssetManifest _manifest;
    private readonly string _assetsDirectory;
    private readonly int _transferChunkBytes;
    private readonly IDiskSpaceProvider _diskSpaceProvider;
    private readonly Dictionary<CoreAssetHash, AssetState> _states;
    private readonly AssetChunkValidation _validation;
    private bool _prepared;

    public ClientAssetTransferEngine(
        SessionAssetManifest manifest,
        string sessionDirectory,
        IDiskSpaceProvider? diskSpaceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (manifest.TransferChunkBytes <= 0) throw new AssetTransferException("The asset manifest transfer chunk size is invalid.");

        _manifest = manifest;
        _assetsDirectory = Path.Combine(sessionDirectory, "assets");
        _transferChunkBytes = manifest.TransferChunkBytes;
        _diskSpaceProvider = diskSpaceProvider ?? new DriveDiskSpaceProvider();
        _states = new();
        var lengths = new Dictionary<ProtocolAssetHash, long>();
        foreach (var asset in manifest.Assets)
        {
            var expectedChunks = checked((int)((asset.EncodedByteLength + _transferChunkBytes - 1) / _transferChunkBytes));
            if (asset.EncodedByteLength <= 0 || asset.ChunkCount != expectedChunks || !_states.TryAdd(asset.Hash, new(asset)))
                throw new AssetTransferException("The asset manifest is inconsistent with the transfer chunk size.");
            lengths.Add(new ProtocolAssetHash(asset.Hash.Value), asset.EncodedByteLength);
        }
        _validation = new(new SessionId(manifest.SessionId), lengths, _transferChunkBytes);
    }

    public AssetTransferPlan Prepare(long needRevision, SnapshotId? snapshotId = null, int pageSize = ProtocolConstants.MaxPageEntries)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_assetsDirectory);
            long missingBytes = 0;
            foreach (var state in _states.Values)
            {
                state.IsComplete = AssetCacheValidator.IsValid(CompletePath(state.Asset), state.Asset.Hash, state.Asset.EncodedByteLength);
                if (!state.IsComplete)
                    missingBytes = checked(missingBytes + state.Asset.EncodedByteLength);
            }

            var required = checked(missingBytes + RequiredFreeSpaceReserve);
            var available = _diskSpaceProvider.GetAvailableBytes(_assetsDirectory);
            if (available < required)
                throw new InsufficientAssetDiskSpaceException(required, available);

            foreach (var state in _states.Values.Where(x => !x.IsComplete))
                LoadBitset(state);
            _prepared = true;
            var missing = _states.Values.Where(x => !x.IsComplete).Select(x => x.Asset.Hash);
            return new(AssetNeedPager.CreatePages(_manifest.SessionId, missing, needRevision, snapshotId, pageSize), GetProgress());
        }
    }

    public AssetChunkReceipt Receive(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (!_prepared) throw new InvalidOperationException("Prepare must be called before receiving asset chunks.");
            AssetChunk chunk;
            try { chunk = AssetChunkCodec.Parse(frame, _validation); }
            catch (Exception exception) when (exception is ProtocolException or OverflowException)
            {
                throw new AssetTransferException("The asset chunk is invalid.", exception);
            }

            var hash = new CoreAssetHash(chunk.AssetHash.Value);
            var state = _states[hash];
            if (state.IsComplete)
                return new(hash, checked((int)chunk.ChunkIndex), true, false, GetProgress());

            var index = checked((int)chunk.ChunkIndex);
            if (state.Received![index])
                return new(hash, index, true, false, GetProgress());

            var partialPath = PartialPath(state.Asset);
            try
            {
                using (var stream = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                           1, FileOptions.RandomAccess | FileOptions.WriteThrough))
                {
                    if (stream.Length != state.Asset.EncodedByteLength)
                        stream.SetLength(state.Asset.EncodedByteLength);
                    stream.Position = checked((long)index * _transferChunkBytes);
                    stream.Write(chunk.Payload);
                    stream.Flush(flushToDisk: true);
                }
                state.Received[index] = true;
                state.ReceivedBytes += chunk.Payload.Length;
                SaveBitset(state);

                var completed = false;
                if (state.Received.Cast<bool>().All(x => x))
                {
                    if (!AssetCacheValidator.IsValid(partialPath, state.Asset.Hash, state.Asset.EncodedByteLength))
                    {
                        state.Received.SetAll(false);
                        state.ReceivedBytes = 0;
                        SaveBitset(state);
                        throw new AssetTransferException("The completed asset failed SHA-256 verification.");
                    }
                    File.Move(partialPath, CompletePath(state.Asset), overwrite: true);
                    File.Delete(BitsetPath(state.Asset));
                    state.IsComplete = true;
                    state.Received = null;
                    state.ReceivedBytes = 0;
                    completed = true;
                }
                return new(hash, index, false, completed, GetProgress());
            }
            catch (AssetTransferException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new AssetTransferException("The asset chunk could not be persisted.", exception);
            }
        }
    }

    public AssetTransferProgress Progress
    {
        get { lock (_gate) return GetProgress(); }
    }

    private AssetTransferProgress GetProgress()
    {
        long readyBytes = 0;
        var readyAssets = 0;
        foreach (var state in _states.Values)
        {
            if (state.IsComplete)
            {
                readyBytes += state.Asset.EncodedByteLength;
                readyAssets++;
            }
            else
            {
                readyBytes += state.ReceivedBytes;
            }
        }
        return new(readyBytes, _states.Values.Sum(x => x.Asset.EncodedByteLength), readyAssets, _states.Count);
    }

    private void LoadBitset(AssetState state)
    {
        var expectedBytes = checked((state.Asset.ChunkCount + 7) / 8);
        state.Received = new BitArray(state.Asset.ChunkCount);
        var path = BitsetPath(state.Asset);
        try
        {
            if (!File.Exists(path) || !File.Exists(PartialPath(state.Asset))) return;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != 8 + expectedBytes || !bytes.AsSpan(0, 4).SequenceEqual(BitsetMagic) ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4)) != state.Asset.ChunkCount)
                return;
            state.Received = new BitArray(bytes.AsSpan(8).ToArray()) { Length = state.Asset.ChunkCount };
            for (var index = 0; index < state.Asset.ChunkCount; index++)
            {
                if (state.Received[index])
                    state.ReceivedBytes += ExpectedChunkLength(state.Asset, index);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Received.SetAll(false);
            state.ReceivedBytes = 0;
        }
    }

    private void SaveBitset(AssetState state)
    {
        var bitBytes = new byte[(state.Asset.ChunkCount + 7) / 8];
        state.Received!.CopyTo(bitBytes, 0);
        var contents = new byte[8 + bitBytes.Length];
        BitsetMagic.CopyTo(contents);
        BinaryPrimitives.WriteInt32BigEndian(contents.AsSpan(4, 4), state.Asset.ChunkCount);
        bitBytes.CopyTo(contents, 8);
        var path = BitsetPath(state.Asset);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4_096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private int ExpectedChunkLength(AssetManifestEntry asset, int index) => index + 1 == asset.ChunkCount
        ? checked((int)(asset.EncodedByteLength - (long)index * _transferChunkBytes))
        : _transferChunkBytes;

    private string CompletePath(AssetManifestEntry asset) => Path.Combine(_assetsDirectory, $"{asset.Hash}.webp");
    private string PartialPath(AssetManifestEntry asset) => Path.Combine(_assetsDirectory, $"{asset.Hash}.partial");
    private string BitsetPath(AssetManifestEntry asset) => Path.Combine(_assetsDirectory, $"{asset.Hash}.partial.bits");

    private sealed class AssetState(AssetManifestEntry asset)
    {
        public AssetManifestEntry Asset { get; } = asset;
        public bool IsComplete { get; set; }
        public BitArray? Received { get; set; }
        public long ReceivedBytes { get; set; }
    }
}

public sealed class HostAssetChunkSource
{
    public IEnumerable<byte[]> EnumerateFrames(
        SessionAssetManifest manifest,
        string sessionDirectory,
        IEnumerable<CoreAssetHash> requestedHashes,
        int transferChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requestedHashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (transferChunkBytes <= 0 || transferChunkBytes != manifest.TransferChunkBytes)
            throw new AssetTransferException("The host transfer chunk size does not match the manifest.");

        var entries = manifest.Assets.ToDictionary(x => x.Hash);
        var lengths = entries.ToDictionary(x => new ProtocolAssetHash(x.Key.Value), x => x.Value.EncodedByteLength);
        var validation = new AssetChunkValidation(new SessionId(manifest.SessionId), lengths, transferChunkBytes);
        foreach (var hash in requestedHashes.Distinct())
        {
            if (!entries.TryGetValue(hash, out var asset))
                throw new AssetTransferException("An asset request contains a hash outside the active manifest.");
            var path = Path.Combine(sessionDirectory, "assets", $"{hash}.webp");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, transferChunkBytes,
                FileOptions.SequentialScan);
            if (stream.Length != asset.EncodedByteLength)
                throw new AssetTransferException("A host asset no longer matches its manifest length.");
            for (var index = 0; index < asset.ChunkCount; index++)
            {
                var length = index + 1 == asset.ChunkCount
                    ? checked((int)(asset.EncodedByteLength - (long)index * transferChunkBytes))
                    : transferChunkBytes;
                var payload = new byte[length];
                stream.ReadExactly(payload);
                yield return AssetChunkCodec.Write(new(
                    new SessionId(manifest.SessionId), new ProtocolAssetHash(hash.Value),
                    checked((uint)index), checked((uint)asset.ChunkCount), payload), validation);
            }
        }
    }
}
