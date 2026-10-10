namespace BmwMs43Logger.Core;

public sealed class SimulatedRuntimeAdapter : ILoggerRuntime
{
    private readonly SimulatedLoggerRuntime _inner;
    private Guid _epochId;
    private bool _disposed;

    public SimulatedRuntimeAdapter()
        : this(new SimulatedLoggerRuntime())
    {
    }

    internal SimulatedRuntimeAdapter(SimulatedLoggerRuntime inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.ProgressChanged += OnInnerProgress;
        _inner.Faulted += OnInnerFault;
    }

    public LoggerRuntimeIdentity Identity { get; } = new(
        LoggerRuntimeMode.Simulation,
        "Offline simulation",
        CommunicationCapable: false,
        InterfaceName: "None");

    public bool IsConnected => _inner.IsConnected;
    public bool IsRunning => _inner.IsRunning;

    public event LoggerRuntimeProgressEventHandler? ProgressChanged;
    public event LoggerRuntimeFaultEventHandler? Faulted;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(cancellationToken);

    public Task<string> StartAsync(
        string outputDirectory,
        double requestedHz,
        IEnumerable<ParameterId> selectedParameters,
        CancellationToken cancellationToken = default)
    {
        _epochId = Guid.Empty;
        return _inner.StartAsync(
            outputDirectory,
            requestedHz,
            selectedParameters,
            cancellationToken);
    }

    public async Task<LoggerSessionResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        SimulatedLoggerSessionResult result = await _inner
            .StopAsync(cancellationToken)
            .ConfigureAwait(false);
        return new LoggerSessionResult(
            result.RunDirectory,
            result.CsvPath,
            result.MetadataPath,
            _epochId,
            result.CyclesWritten,
            result.SamplesWritten,
            result.DurationSeconds,
            "completed",
            null);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _inner.DisconnectAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _inner.ProgressChanged -= OnInnerProgress;
        _inner.Faulted -= OnInnerFault;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void OnInnerProgress(
        object? sender,
        SimulatedLoggerProgress progress)
    {
        _epochId = progress.Cycle.EpochId;
        ProgressChanged?.Invoke(
            this,
            new LoggerRuntimeProgress(
                progress.CyclesWritten,
                progress.ObservedAtUtc,
                progress.LatestValues,
                progress.Cycle));
    }

    private void OnInnerFault(object? sender, string safeError) =>
        Faulted?.Invoke(
            this,
            new LoggerRuntimeFault(
                safeError,
                safeError,
                RequiresReconnect: true,
                RunDirectory: null,
                MetadataPath: null));
}
