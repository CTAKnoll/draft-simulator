using System.Text.Json;
using DraftSimulator.Core;
using DraftSimulator.Protocol;
using SkiaSharp;
using CoreAssetHash = DraftSimulator.Core.AssetHash;

namespace DraftSimulator.Infrastructure;

public sealed record AssetManifestEntry(
    CoreAssetHash Hash,
    long EncodedByteLength,
    int PixelWidth,
    int PixelHeight,
    string MimeType,
    int ChunkCount);

public sealed record SessionAssetManifest(Guid SessionId, int ProtocolVersion, int TransferChunkBytes, IReadOnlyList<AssetManifestEntry> Assets);
public sealed record PreparedDefinition(CardDefinitionId DefinitionId, CoreAssetHash AssetHash);
public sealed record AssetPreparationResult(
    SessionAssetManifest Manifest,
    IReadOnlyList<PreparedDefinition> Definitions,
    long TotalAssetBytes);
public sealed record HostSessionManifest(
    SessionAssetManifest AssetManifest,
    IReadOnlyList<PreparedDefinition> Definitions);

public sealed class AssetPreparationException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IDiskSpaceProvider
{
    long GetAvailableBytes(string path);
}

public sealed class DriveDiskSpaceProvider : IDiskSpaceProvider
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("The session volume could not be determined.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}

public interface IAssetPreparer
{
    AssetPreparationResult Prepare(Guid sessionId, IEnumerable<CardDefinition> usedDefinitions, string sessionDirectory,
        HostConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class AssetPreparer(IDiskSpaceProvider? diskSpaceProvider = null) : IAssetPreparer
{
    public const long RequiredFreeSpaceReserve = 64L * 1024 * 1024;
    private readonly IDiskSpaceProvider _diskSpaceProvider = diskSpaceProvider ?? new DriveDiskSpaceProvider();

    public AssetPreparationResult Prepare(
        Guid sessionId,
        IEnumerable<CardDefinition> usedDefinitions,
        string sessionDirectory,
        HostConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usedDefinitions);
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        var requested = usedDefinitions.GroupBy(x => x.Id).Select(x => x.First()).ToArray();
        var assetsDirectory = Path.Combine(sessionDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);

        var manifestByHash = new Dictionary<CoreAssetHash, AssetManifestEntry>();
        var prepared = new List<PreparedDefinition>(requested.Length);
        long totalBytes = 0;

        foreach (var definition in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(definition.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sourceLength = stream.Length;
                if (sourceLength > configuration.MaxSourceImageBytes)
                    throw new AssetPreparationException("A requested source image exceeds MaxSourceImageBytes.");

                using var codec = SKCodec.Create(stream) ?? throw new AssetPreparationException("A requested source image is invalid.");
                var sourceInfo = codec.Info;
                var pixels = checked((long)sourceInfo.Width * sourceInfo.Height);
                if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0 || pixels > configuration.MaxDecodedPixels)
                    throw new AssetPreparationException("A requested source image exceeds MaxDecodedPixels or has invalid dimensions.");

                using var source = new SKBitmap(new SKImageInfo(sourceInfo.Width, sourceInfo.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success)
                    throw new AssetPreparationException("A requested source image could not be fully decoded.");

                var scale = Math.Min(1d, (double)configuration.OutputMaxLongEdge / Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));
                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                    ?? throw new AssetPreparationException("The transformed image surface could not be allocated.");
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKPaint { IsAntialias = true });
                surface.Canvas.Flush();
                using var image = surface.Snapshot();
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, configuration.OutputWebPQuality)
                    ?? throw new AssetPreparationException("A requested source image could not be encoded as WebP.");
                var bytes = encoded.ToArray();
                var hash = CoreAssetHash.Compute(bytes);
                prepared.Add(new(definition.Id, hash));

                if (manifestByHash.ContainsKey(hash))
                    continue;

                totalBytes = checked(totalBytes + bytes.LongLength);
                if (totalBytes > configuration.MaxSessionAssetBytes)
                    throw new AssetPreparationException("Transformed assets exceed MaxSessionAssetBytes.");
                if (_diskSpaceProvider.GetAvailableBytes(assetsDirectory) < checked(bytes.LongLength + RequiredFreeSpaceReserve))
                    throw new AssetPreparationException("The session volume has insufficient free space.");

                var finalPath = Path.Combine(assetsDirectory, $"{hash}.webp");
                cancellationToken.ThrowIfCancellationRequested();
                if (!AssetCacheValidator.IsValid(finalPath, hash, bytes.LongLength))
                    WriteAtomically(finalPath, bytes);

                var chunkCount = checked((int)((bytes.LongLength + configuration.TransferChunkBytes - 1) / configuration.TransferChunkBytes));
                manifestByHash.Add(hash, new(hash, bytes.LongLength, width, height, "image/webp", chunkCount));
            }
            catch (AssetPreparationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException or ArgumentException)
            {
                throw new AssetPreparationException("A requested source image could not be prepared.", exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manifest = new SessionAssetManifest(sessionId, ProtocolConstants.Version, configuration.TransferChunkBytes, manifestByHash.Values.ToArray());
        var hostManifest = new HostSessionManifest(manifest, prepared);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(hostManifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        WriteAtomically(Path.Combine(sessionDirectory, "manifest.json"), manifestBytes);
        return new(manifest, prepared, totalBytes);
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65_536, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
