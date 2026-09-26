namespace DraftSimulator.Infrastructure;

public sealed class LocalAppDataPaths
{
    public LocalAppDataPaths(string? localAppData = null) : this(localAppData, useExactRoot: false) { }

    private LocalAppDataPaths(string? path, bool useExactRoot)
    {
        var basePath = path ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("The local application data directory is unavailable.");

        Root = useExactRoot ? Path.GetFullPath(basePath) : Path.Combine(basePath, "DraftSimulator");
    }

    public static LocalAppDataPaths FromRoot(string root) => new(root, useExactRoot: true);

    public string Root { get; }
    public string HostConfigurationFile => Path.Combine(Root, "host.ini");
    public string ClientStateFile => Path.Combine(Root, "client-state.json");
    public string ScryfallDirectory => Path.Combine(Root, "Scryfall");
    public string LogsDirectory => Path.Combine(Root, "Logs");
    public string SessionsDirectory => Path.Combine(Root, "Sessions");
    public string GetSessionDirectory(Guid sessionId) => Path.Combine(SessionsDirectory, sessionId.ToString("D"));
}
