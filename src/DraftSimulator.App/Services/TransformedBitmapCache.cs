using Avalonia.Media.Imaging;

namespace DraftSimulator.App.Services;

public sealed class TransformedBitmapCache(long maximumBytes) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private bool _disposed;

    public async Task<BitmapLease> AcquireAsync(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(path, out var cached))
            {
                cached.References++;
                cached.LastAccess = Environment.TickCount64;
                return new(cached.Bitmap, () => Release(path));
            }
        }

        var bitmap = await Task.Run(() => new Bitmap(path));
        var bytes = checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
        lock (_gate)
        {
            if (_disposed) { bitmap.Dispose(); throw new ObjectDisposedException(nameof(TransformedBitmapCache)); }
            if (_entries.TryGetValue(path, out var raced))
            {
                bitmap.Dispose(); raced.References++; raced.LastAccess = Environment.TickCount64;
                return new(raced.Bitmap, () => Release(path));
            }

            Evict(bytes);
            if (bytes > maximumBytes || _bytes + bytes > maximumBytes)
                return new(bitmap, bitmap.Dispose);
            _entries.Add(path, new(bitmap, bytes));
            _bytes += bytes;
            return new(bitmap, () => Release(path));
        }
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry))
            {
                entry.References--;
                entry.LastAccess = Environment.TickCount64;
            }
        }
    }

    private void Evict(long requiredBytes)
    {
        foreach (var pair in _entries.Where(x => x.Value.References == 0).OrderBy(x => x.Value.LastAccess).ToArray())
        {
            if (_bytes + requiredBytes <= maximumBytes) break;
            _entries.Remove(pair.Key);
            _bytes -= pair.Value.Bytes;
            pair.Value.Bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Bitmap.Dispose();
            _entries.Clear(); _bytes = 0;
        }
    }

    private sealed class Entry(Bitmap bitmap, long bytes)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = bytes;
        public int References { get; set; } = 1;
        public long LastAccess { get; set; } = Environment.TickCount64;
    }
}

public sealed class BitmapLease(Bitmap bitmap, Action release) : IDisposable
{
    private Action? _release = release;
    public Bitmap Bitmap { get; } = bitmap;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
