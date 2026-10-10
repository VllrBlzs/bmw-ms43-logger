using BmwMs43Logger.Core;
using BmwMs43Logger.Ediabas;

namespace BmwMs43Logger.App;

internal sealed record AppRuntimeOptions(
    LoggerRuntimeMode Mode,
    string RepositoryRoot,
    string EcuPath,
    string Port,
    string CatalogPath,
    string PackPath)
{
    public static AppRuntimeOptions Parse(IReadOnlyList<string> args)
    {
        bool simulate = false;
        bool live = false;
        bool confirmed = false;
        string? repositoryRoot = null;
        string ecuPath = @"C:\EDIABAS\ECU";
        string port = "COM5";
        string? catalogPath = null;
        string? packPath = null;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--simulate":
                    simulate = true;
                    break;
                case "--live":
                    live = true;
                    break;
                case "--confirm-read-only":
                    confirmed = true;
                    break;
                case "--repo-root" when index + 1 < args.Count:
                    repositoryRoot = args[++index];
                    break;
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = args[++index].ToUpperInvariant();
                    break;
                case "--catalog" when index + 1 < args.Count:
                    catalogPath = args[++index];
                    break;
                case "--pack" when index + 1 < args.Count:
                    packPath = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        if (simulate && live)
        {
            throw new ArgumentException("Choose either --simulate or --live, not both.");
        }
        LoggerRuntimeMode mode = live
            ? LoggerRuntimeMode.LiveReadOnly
            : LoggerRuntimeMode.Simulation;
        if (mode == LoggerRuntimeMode.LiveReadOnly && !confirmed)
        {
            throw new InvalidOperationException(
                "Live mode requires --confirm-read-only.");
        }
        if (mode == LoggerRuntimeMode.Simulation && confirmed)
        {
            throw new ArgumentException(
                "--confirm-read-only is accepted only with --live.");
        }
        if (!string.Equals(port, "COM5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pinned BMW-14 live profile permits COM5 only.");
        }

        string root = FindRepositoryRoot(repositoryRoot);
        return new AppRuntimeOptions(
            mode,
            root,
            Path.GetFullPath(ecuPath),
            port,
            Path.GetFullPath(catalogPath ?? Path.Combine(
                root,
                "config",
                "ms430ds0-parameter-catalog.draft.json")),
            Path.GetFullPath(packPath ?? Path.Combine(
                root,
                "config",
                "ms430ds0-bounded-live-test-pack.json")));
    }

    public ILoggerRuntime CreateRuntime() => Mode switch
    {
        LoggerRuntimeMode.Simulation => new SimulatedRuntimeAdapter(),
        LoggerRuntimeMode.LiveReadOnly => new EdiabasLoggerRuntime(
            new Ms43LiveRuntimeOptions(
                EcuPath,
                Port,
                CatalogPath,
                PackPath)),
        _ => throw new ArgumentOutOfRangeException(nameof(Mode))
    };

    private static string FindRepositoryRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        string current = Path.GetFullPath(Environment.CurrentDirectory);
        DirectoryInfo? directory = new(current);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BmwMs43Logger.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "config")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root not found. Start from the BMW folder or pass --repo-root.");
    }
}
