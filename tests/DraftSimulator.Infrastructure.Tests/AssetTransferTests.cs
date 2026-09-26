using System.Buffers.Binary;
using System.Security.Cryptography;
using DraftSimulator.Infrastructure;
using DraftSimulator.Protocol;
using CoreAssetHash = DraftSimulator.Core.AssetHash;
using ProtocolAssetHash = DraftSimulator.Protocol.AssetHash;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class AssetTransferTests
{
    private const int ChunkSize = 4;

    [Fact]
    public void ManifestPagesAccumulateOutOfOrderAndApplyAtomically()
    {
        var manifest = Manifest(Enumerable.Range(0, 5).Select(index => new byte[] { (byte)index }).ToArray());
        var pages = AssetManifestPager.CreatePages(manifest, 7, new SnapshotId(Guid.NewGuid()), 2);
        var accumulator = new AssetManifestAccumulator();

        Assert.False(accumulator.Add(pages[2]).IsComplete);
        Assert.False(accumulator.Add(pages[0]).IsComplete);
        var result = accumulator.Add(pages[1]);

        Assert.True(result.IsComplete);
        Assert.Equal(7, result.Revision);
        Assert.Equal(manifest.SessionId, result.Manifest!.SessionId);
        Assert.Equal(manifest.ProtocolVersion, result.Manifest.ProtocolVersion);
        Assert.Equal(manifest.Assets, result.Manifest.Assets);
        Assert.True(accumulator.Add(pages[1]).IsComplete);
        Assert.Throws<AssetTransferException>(() => accumulator.Add(pages[1] with
        {
            Assets = [pages[1].Assets[0] with { PixelWidth = 99 }]
        }));
    }

    [Fact]
    public void NewManifestRevisionDiscardsIncompleteOlderPages()
    {
        var manifest = Manifest([1], [2], [3]);
        var accumulator = new AssetManifestAccumulator();
        var oldPages = AssetManifestPager.CreatePages(manifest, 1, pageSize: 2);
        var newPages = AssetManifestPager.CreatePages(manifest, 2, pageSize: 2);

        Assert.False(accumulator.Add(oldPages[0]).IsComplete);
        Assert.False(accumulator.Add(newPages[1]).IsComplete);
        Assert.True(accumulator.Add(newPages[0]).IsComplete);
        Assert.Throws<AssetTransferException>(() => accumulator.Add(oldPages[1]));
    }

    [Fact]
    public void ManifestRejectsChunkCountThatDoesNotMatchAuthoritativeChunkSize()
    {
        var manifest = Manifest(Enumerable.Range(0, 9).Select(x => (byte)x).ToArray());
        var page = AssetManifestPager.CreatePages(manifest, 1).Single();

        Assert.Throws<AssetTransferException>(() => new AssetManifestAccumulator().Add(page with
        {
            Assets = [page.Assets[0] with { ChunkCount = 2 }]
        }));
    }

    [Fact]
    public void NeedPagesAreBoundedAndCacheHitNeedsNothing()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 9).Select(x => (byte)x).ToArray();
        var manifest = Manifest(bytes);
        WriteComplete(temporary.Path, manifest.Assets[0], bytes);
        var engine = Engine(manifest, temporary.Path);

        var plan = engine.Prepare(3, pageSize: 1);

        Assert.Single(plan.NeedPages);
        Assert.Empty(plan.NeedPages[0].Hashes);
        Assert.True(plan.Progress.IsComplete);
        Assert.Equal(bytes.Length, plan.Progress.BytesReady);

        var many = Enumerable.Range(0, 401).Select(index => Hash(BitConverter.GetBytes(index))).ToArray();
        var pages = AssetNeedPager.CreatePages(manifest.SessionId, many, 4);
        Assert.Equal([200, 200, 1], pages.Select(x => x.Hashes.Length));
    }

    [Fact]
    public void ReceivesOutOfOrderChunksIgnoresDuplicatesAndCompletesAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 10).Select(x => (byte)x).ToArray();
        var manifest = Manifest(bytes);
        WriteHostAsset(temporary.Path, manifest.Assets[0], bytes);
        var frames = new HostAssetChunkSource().EnumerateFrames(manifest, temporary.Path,
            [manifest.Assets[0].Hash], ChunkSize).ToArray();
        File.Delete(CompletePath(temporary.Path, manifest.Assets[0]));
        var engine = Engine(manifest, temporary.Path);
        engine.Prepare(1);

        Assert.False(engine.Receive(frames[2]).AssetCompleted);
        Assert.False(engine.Receive(frames[0]).AssetCompleted);
        var duplicate = engine.Receive(frames[0]);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(6, duplicate.Progress.BytesReady);
        var completed = engine.Receive(frames[1]);

        Assert.True(completed.AssetCompleted);
        Assert.True(completed.Progress.IsComplete);
        Assert.Equal(bytes, File.ReadAllBytes(CompletePath(temporary.Path, manifest.Assets[0])));
        Assert.False(File.Exists(PartialPath(temporary.Path, manifest.Assets[0])));
        Assert.False(File.Exists(BitsetPath(temporary.Path, manifest.Assets[0])));
    }

    [Fact]
    public void ReopensPartialStateAndOnlyCountsPersistedChunksOnce()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 10).Select(x => (byte)x).ToArray();
        var manifest = Manifest(bytes);
        WriteHostAsset(temporary.Path, manifest.Assets[0], bytes);
        var frames = new HostAssetChunkSource().EnumerateFrames(manifest, temporary.Path,
            [manifest.Assets[0].Hash], ChunkSize).ToArray();
        File.Delete(CompletePath(temporary.Path, manifest.Assets[0]));
        var first = Engine(manifest, temporary.Path);
        first.Prepare(1);
        first.Receive(frames[1]);

        var reconnected = Engine(manifest, temporary.Path);
        var plan = reconnected.Prepare(2);

        Assert.Equal(4, plan.Progress.BytesReady);
        Assert.Single(plan.NeedPages[0].Hashes);
        Assert.True(reconnected.Receive(frames[1]).WasDuplicate);
        reconnected.Receive(frames[2]);
        Assert.True(reconnected.Receive(frames[0]).Progress.IsComplete);
    }

    [Fact]
    public void RejectsWrongSessionHashIndexAndPayloadLengthWithoutChangingPartialFile()
    {
        using var temporary = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 6).Select(x => (byte)x).ToArray();
        var manifest = Manifest(bytes);
        var valid = Frame(manifest, 0, bytes[..4]);
        var malformed = new List<byte[]>();

        var wrongSession = valid.ToArray();
        wrongSession[8] ^= 1;
        malformed.Add(wrongSession);
        var wrongHash = valid.ToArray();
        wrongHash[24] ^= 1;
        malformed.Add(wrongHash);
        var wrongIndex = valid.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(wrongIndex.AsSpan(56, 4), 2);
        malformed.Add(wrongIndex);
        malformed.Add(valid[..^1]);

        var engine = Engine(manifest, temporary.Path);
        engine.Prepare(1);
        foreach (var frame in malformed)
            Assert.Throws<AssetTransferException>(() => engine.Receive(frame));
        Assert.False(File.Exists(PartialPath(temporary.Path, manifest.Assets[0])));
    }

    [Fact]
    public void HashFailureKeepsPartialAndResetsResumeMapForCleanRetry()
    {
        using var temporary = new TemporaryDirectory();
        var expected = Enumerable.Range(0, 6).Select(x => (byte)x).ToArray();
        var manifest = Manifest(expected);
        var engine = Engine(manifest, temporary.Path);
        engine.Prepare(1);

        engine.Receive(Frame(manifest, 0, [9, 9, 9, 9]));
        Assert.Throws<AssetTransferException>(() => engine.Receive(Frame(manifest, 1, expected[4..])));

        Assert.True(File.Exists(PartialPath(temporary.Path, manifest.Assets[0])));
        Assert.False(File.Exists(CompletePath(temporary.Path, manifest.Assets[0])));
        var retry = Engine(manifest, temporary.Path).Prepare(2);
        Assert.Equal(0, retry.Progress.BytesReady);
    }

    [Fact]
    public void InsufficientDiskIsReportedBeforeTransferAndExistingCompleteAssetIsPreserved()
    {
        using var temporary = new TemporaryDirectory();
        var completeBytes = new byte[] { 1, 2, 3 };
        var missingBytes = new byte[] { 4, 5, 6, 7 };
        var manifest = Manifest(completeBytes, missingBytes);
        WriteComplete(temporary.Path, manifest.Assets[0], completeBytes);
        var engine = Engine(manifest, temporary.Path,
            new FixedDiskSpace(ClientAssetTransferEngine.RequiredFreeSpaceReserve + missingBytes.Length - 1));

        var exception = Assert.Throws<InsufficientAssetDiskSpaceException>(() => engine.Prepare(1));

        Assert.Equal(ClientAssetTransferEngine.RequiredFreeSpaceReserve + missingBytes.Length, exception.RequiredBytes);
        Assert.Equal(completeBytes, File.ReadAllBytes(CompletePath(temporary.Path, manifest.Assets[0])));
        Assert.False(File.Exists(PartialPath(temporary.Path, manifest.Assets[1])));
    }

    private static ClientAssetTransferEngine Engine(SessionAssetManifest manifest, string directory, IDiskSpaceProvider? disk = null) =>
        new(manifest, directory, disk ?? new FixedDiskSpace(long.MaxValue));

    private static SessionAssetManifest Manifest(params byte[][] assets) => new(
        Guid.NewGuid(), ProtocolConstants.Version, ChunkSize,
        assets.Select(bytes =>
        {
            var hash = Hash(bytes);
            return new AssetManifestEntry(hash, bytes.Length, 10, 20, "image/webp",
                (bytes.Length + ChunkSize - 1) / ChunkSize);
        }).ToArray());

    private static CoreAssetHash Hash(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static byte[] Frame(SessionAssetManifest manifest, int index, byte[] payload)
    {
        var asset = manifest.Assets[0];
        var validation = new AssetChunkValidation(new SessionId(manifest.SessionId),
            new Dictionary<ProtocolAssetHash, long> { [new(asset.Hash.Value)] = asset.EncodedByteLength }, ChunkSize);
        return AssetChunkCodec.Write(new(new SessionId(manifest.SessionId), new(asset.Hash.Value),
            (uint)index, (uint)asset.ChunkCount, payload), validation);
    }

    private static void WriteHostAsset(string directory, AssetManifestEntry asset, byte[] bytes) =>
        WriteComplete(directory, asset, bytes);

    private static void WriteComplete(string directory, AssetManifestEntry asset, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        File.WriteAllBytes(CompletePath(directory, asset), bytes);
    }

    private static string CompletePath(string directory, AssetManifestEntry asset) =>
        Path.Combine(directory, "assets", $"{asset.Hash}.webp");

    private static string PartialPath(string directory, AssetManifestEntry asset) =>
        Path.Combine(directory, "assets", $"{asset.Hash}.partial");

    private static string BitsetPath(string directory, AssetManifestEntry asset) =>
        Path.Combine(directory, "assets", $"{asset.Hash}.partial.bits");

    private sealed class FixedDiskSpace(long bytes) : IDiskSpaceProvider
    {
        public long GetAvailableBytes(string path) => bytes;
    }
}
