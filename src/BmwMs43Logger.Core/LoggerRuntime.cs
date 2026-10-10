namespace BmwMs43Logger.Core;

public enum LoggerRuntimeMode
{
    Simulation,
    LiveReadOnly
}

public sealed record LoggerRuntimeIdentity(
    LoggerRuntimeMode Mode,
    string DisplayName,
    bool CommunicationCapable,
    string InterfaceName);

public sealed record LoggerRuntimeProgress(
    long CyclesWritten,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<string, double> LatestValues,
    NormalizedCycle Cycle);

public sealed record LoggerRuntimeFault(
    string Code,
    string SafeMessage,
    bool RequiresReconnect,
    string? RunDirectory,
    string? MetadataPath);

public sealed record LoggerSessionResult(
    string RunDirectory,
    string CsvPath,
    string MetadataPath,
    Guid EpochId,
    long CyclesWritten,
    long SamplesWritten,
    double DurationSeconds,
    string Status,
    string? FaultCode)
{
    public bool Completed => string.Equals(
        Status,
        "completed",
        StringComparison.Ordinal);
}

public delegate void LoggerRuntimeProgressEventHandler(
    object? sender,
    LoggerRuntimeProgress progress);

public delegate void LoggerRuntimeFaultEventHandler(
    object? sender,
    LoggerRuntimeFault fault);

public interface ILoggerRuntime : IAsyncDisposable
{
    LoggerRuntimeIdentity Identity { get; }
    bool IsConnected { get; }
    bool IsRunning { get; }

    event LoggerRuntimeProgressEventHandler? ProgressChanged;
    event LoggerRuntimeFaultEventHandler? Faulted;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<string> StartAsync(
        string outputDirectory,
        double requestedHz,
        IEnumerable<ParameterId> selectedParameters,
        CancellationToken cancellationToken = default);

    Task<LoggerSessionResult> StopAsync(
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public static class LoggerRecoveryPolicy
{
    public const int MaximumTransportAttempts = 2;
    public const int RetryDelayMilliseconds = 100;

    private static readonly HashSet<string> RetryableCodes = new(
        [
            "EDIABAS_TIMEOUT",
            "EDIABAS_PORT_LOST",
            "EDIABAS_TRANSPORT_ERROR"
        ],
        StringComparer.Ordinal);

    public static bool ShouldRetry(string code, int completedAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (completedAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }
        return completedAttempts < MaximumTransportAttempts &&
               RetryableCodes.Contains(code);
    }

    public static TimeSpan RetryDelay(int completedAttempts)
    {
        if (completedAttempts < 1 ||
            completedAttempts >= MaximumTransportAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }
        return TimeSpan.FromMilliseconds(RetryDelayMilliseconds);
    }
}
