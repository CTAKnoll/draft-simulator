using System.Globalization;
using System.Security.Cryptography;

namespace DraftSimulator.Infrastructure;

public sealed class OfflineDebugProfile : IDisposable
{
    private readonly FileStream _instanceLock;
    private int _disposed;

    private OfflineDebugProfile(string name, string sharedDirectory, LocalAppDataPaths paths,
        SteamPeerId peerId, FileStream instanceLock)
    {
        Name = name;
        SharedLobbyDirectory = sharedDirectory;
        Paths = paths;
        PeerId = peerId;
        _instanceLock = instanceLock;
    }

    public string Name { get; }
    public string SharedLobbyDirectory { get; }
    public LocalAppDataPaths Paths { get; }
    public SteamPeerId PeerId { get; }

    public static OfflineDebugProfile Open(string localAppDataDirectory, string profileName)
    {
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
            throw new ArgumentException("A local application data directory is required.", nameof(localAppDataDirectory));
        if (!IsValidName(profileName))
            throw new ArgumentException("Offline profile names must contain 1-32 ASCII letters, numbers, underscores, or hyphens.", nameof(profileName));

        var root = Path.Combine(Path.GetFullPath(localAppDataDirectory), "DraftSimulator", "OfflineDebug");
        var sharedDirectory = Path.Combine(root, "Lobbies");
        var profileRoot = Path.Combine(root, "Profiles", profileName);
        Directory.CreateDirectory(sharedDirectory);
        Directory.CreateDirectory(profileRoot);

        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(profileRoot, ".instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Offline profile '{profileName}' is already running.", exception);
        }

        try
        {
            var peerId = LoadOrCreatePeerId(Path.Combine(profileRoot, "peer-id.txt"));
            return new OfflineDebugProfile(profileName, sharedDirectory, LocalAppDataPaths.FromRoot(profileRoot), peerId, instanceLock);
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    private static bool IsValidName(string? value) => value is { Length: >= 1 and <= 32 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static SteamPeerId LoadOrCreatePeerId(string path)
    {
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var existing) && existing != 0)
                return new SteamPeerId(existing);
            throw new InvalidDataException("The offline profile peer identity is invalid.");
        }

        ulong value;
        do value = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong))); while (value == 0);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value.ToString(CultureInfo.InvariantCulture));
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value == 0)
                    throw new InvalidDataException("The offline profile peer identity is invalid.");
            }
            return new SteamPeerId(value);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _instanceLock.Dispose();
    }
}
