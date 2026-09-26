using SkiaSharp;

namespace DraftSimulator.Infrastructure.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"draft-simulator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class ImageTestData
{
    public static void Write(string path, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(25, 100, 200, 255));
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        using var output = File.Create(path);
        data.SaveTo(output);
    }
}
