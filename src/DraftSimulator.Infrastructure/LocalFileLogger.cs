using System.Globalization;

namespace DraftSimulator.Infrastructure;

public interface ILocalLogger
{
    void Write(string eventCode, int? errorCode = null);
}

public sealed class NullLocalLogger : ILocalLogger
{
    public static NullLocalLogger Instance { get; } = new();
    private NullLocalLogger() { }
    public void Write(string eventCode, int? errorCode = null) { }
}

public sealed class RollingLocalFileLogger : ILocalLogger
{
    private const long MaximumFileBytes = 256 * 1024;
    private const int MaximumFiles = 3;
    private readonly object _gate = new();
    private readonly string _directory;

    public RollingLocalFileLogger(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public void Write(string eventCode, int? errorCode = null)
    {
        if (string.IsNullOrWhiteSpace(eventCode) || eventCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '.'))
            throw new ArgumentException("Log event codes may only contain ASCII letters, digits, underscores, and periods.", nameof(eventCode));

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var current = Path.Combine(_directory, "draft-simulator.log");
                if (File.Exists(current) && new FileInfo(current).Length >= MaximumFileBytes)
                    Rotate(current);
                var suffix = errorCode is null ? string.Empty : $" error={errorCode.Value.ToString(CultureInfo.InvariantCulture)}";
                File.AppendAllText(current, $"{DateTimeOffset.UtcNow:O} {eventCode}{suffix}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never disrupt application or networking behavior.
            }
        }
    }

    private void Rotate(string current)
    {
        File.Delete(Path.Combine(_directory, $"draft-simulator.{MaximumFiles - 1}.log"));
        for (var index = MaximumFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"draft-simulator.{index}.log");
            if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"draft-simulator.{index + 1}.log"));
        }
        File.Move(current, Path.Combine(_directory, "draft-simulator.1.log"));
    }
}
