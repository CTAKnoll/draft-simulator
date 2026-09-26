namespace DraftSimulator.Infrastructure;

public sealed record SessionCleanupResult(IReadOnlyList<string> FailedDirectories)
{
    public bool Succeeded => FailedDirectories.Count == 0;
}

public sealed class SessionCacheManager(string sessionsDirectory)
{
    public SessionCleanupResult DeleteAbandonedSessions(Guid? sessionToPreserve = null)
    {
        if (!Directory.Exists(sessionsDirectory))
            return new([]);

        var preserve = sessionToPreserve?.ToString("D");
        var failures = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(sessionsDirectory))
        {
            if (preserve is not null && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(directory), preserve))
                continue;
            TryDelete(directory, failures);
        }
        return new(failures);
    }

    public SessionCleanupResult DeleteCompletedSession(Guid sessionId)
    {
        var failures = new List<string>();
        TryDelete(Path.Combine(sessionsDirectory, sessionId.ToString("D")), failures);
        return new(failures);
    }

    private static void TryDelete(string directory, List<string> failures)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(directory);
        }
    }
}
