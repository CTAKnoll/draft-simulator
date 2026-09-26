namespace DraftSimulator.App;

public sealed record StartupOptions(bool Offline, string? Profile)
{
    public static StartupOptions Parse(IEnumerable<string> arguments, bool offlineArtifact = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = arguments.ToArray();
        var offline = false;
        string? profile = null;
        for (var index = 0; index < values.Length; index++)
        {
            switch (values[index])
            {
                case "--offline":
                    offline = true;
                    break;
                case "--profile" when index + 1 < values.Length:
                    profile = values[++index];
                    break;
                case "--profile":
                    throw new ArgumentException("--profile requires a value.");
            }
        }

        if (!offline && profile is not null && !offlineArtifact)
            throw new ArgumentException("--profile may only be used with --offline.");
        if (offlineArtifact)
            offline = true;
        if (offline && string.IsNullOrWhiteSpace(profile))
        {
            if (offlineArtifact)
                profile = "host";
            else
            throw new ArgumentException("Offline mode requires --profile <name>.");
        }
#if !DEBUG
        if (offline)
            throw new InvalidOperationException("Offline mode is available only in Debug builds.");
#endif
        return new StartupOptions(offline, profile);
    }
}
