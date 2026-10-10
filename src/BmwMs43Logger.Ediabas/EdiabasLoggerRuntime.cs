using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BmwMs43Logger.Core;
using EdiabasLib;

namespace BmwMs43Logger.Ediabas;

public sealed class EdiabasLoggerRuntime : ILoggerRuntime
{
    private readonly Ms43LiveRuntimeOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private EdiabasReadOnlyClient? _client;
    private Ms43LiveProfile? _profile;
    private CancellationTokenSource? _runCancellation;
    private Task<LoggerSessionResult>? _runTask;
    private bool _connected;
    private bool _disposed;

    public EdiabasLoggerRuntime(Ms43LiveRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public LoggerRuntimeIdentity Identity { get; } = new(
        LoggerRuntimeMode.LiveReadOnly,
        "Pinned MS43 / EDIABAS read-only",
        CommunicationCapable: true,
        InterfaceName: nameof(EdInterfaceObd));

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _connected;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _runTask is not null;
            }
        }
    }

    public event LoggerRuntimeProgressEventHandler? ProgressChanged;
    public event LoggerRuntimeFaultEventHandler? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateGate)
            {
                if (_connected || _client is not null || _runTask is not null)
                {
                    throw new InvalidOperationException(
                        "Disconnect the existing runtime before reconnecting.");
                }
            }

            Ms43LiveProfile profile = Ms43LiveProfile.Load(
                _options.CatalogPath,
                _options.PackPath);
            profile.RunGuardSelfTests();
            profile.ValidateRuntime(
                _options.EcuPath,
                _options.Port,
                typeof(EdiabasNet).Assembly.Location);

            EdiabasReadOnlyClient? client = null;
            try
            {
                client = new EdiabasReadOnlyClient(_options);
                PollingPlan handshakePlan = profile.BuildPlan(
                [
                    new ParameterId("engine.rpm")
                ]);
                LiveCycleExecution handshake = await ReadWithRetryAsync(
                        client,
                        Guid.NewGuid(),
                        cycleId: 1,
                        handshakePlan,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (handshake.FailureCode is not null)
                {
                    throw new InvalidOperationException(
                        $"BMW14_CONNECT_{handshake.FailureCode}");
                }

                lock (_stateGate)
                {
                    _profile = profile;
                    _client = client;
                    _connected = true;
                }
                client = null;
            }
            finally
            {
                client?.Dispose();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<string> StartAsync(
        string outputDirectory,
        double requestedHz,
        IEnumerable<ParameterId> selectedParameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!double.IsFinite(requestedHz) || requestedHz is < 0.2 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedHz),
                "Requested Hz is bounded to 0.2-5.0.");
        }
        ParameterId[] selection = Ms43ParameterCatalog
            .ValidateSessionSelection(selectedParameters)
            .ToArray();
        var started = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EdiabasReadOnlyClient client;
            Ms43LiveProfile profile;
            lock (_stateGate)
            {
                if (!_connected || _client is null || _profile is null)
                {
                    throw new InvalidOperationException(
                        "Connect the pinned live runtime before starting.");
                }
                if (_runTask is not null)
                {
                    throw new InvalidOperationException(
                        "A live logging session is already active or awaiting cleanup.");
                }
                client = _client;
                profile = _profile;
            }

            PollingPlan plan = profile.BuildPlan(selection);
            var runCancellation = new CancellationTokenSource();
            Guid epochId = Guid.NewGuid();
            Task<LoggerSessionResult> runTask = Task.Run(
                () => RunSessionAsync(
                    client,
                    profile,
                    plan,
                    Path.GetFullPath(outputDirectory),
                    requestedHz,
                    epochId,
                    started,
                    runCancellation.Token),
                CancellationToken.None);
            lock (_stateGate)
            {
                _runCancellation = runCancellation;
                _runTask = runTask;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => started.TrySetCanceled(cancellationToken));
        return await started.Task.ConfigureAwait(false);
    }

    public async Task<LoggerSessionResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        Task<LoggerSessionResult> task;
        CancellationTokenSource cancellation;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateGate)
            {
                task = _runTask ?? throw new InvalidOperationException(
                    "No live session is active or awaiting cleanup.");
                cancellation = _runCancellation ?? throw new InvalidOperationException(
                    "The live session has no cancellation source.");
            }
            cancellation.Cancel();
        }
        finally
        {
            _lifecycle.Release();
        }

        LoggerSessionResult result = await task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await ClearCompletedRunAsync(task).ConfigureAwait(false);
        return result;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<LoggerSessionResult>? completedTask;
            EdiabasReadOnlyClient? client;
            bool hadConnection;
            lock (_stateGate)
            {
                hadConnection = _connected || _client is not null ||
                                _profile is not null || _runTask is not null;
                completedTask = _runTask;
                if (completedTask is { IsCompleted: false })
                {
                    throw new InvalidOperationException(
                        "Stop logging before disconnecting.");
                }
                client = _client;
                _client = null;
                _profile = null;
                _connected = false;
                _runTask = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
            if (!hadConnection)
            {
                throw new InvalidOperationException("The live runtime is disconnected.");
            }
            if (completedTask is not null)
            {
                await completedTask.ConfigureAwait(false);
            }
            client?.Dispose();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<LoggerSessionResult>? task;
        EdiabasReadOnlyClient? client;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            lock (_stateGate)
            {
                _runCancellation?.Cancel();
                task = _runTask;
                client = _client;
                _runTask = null;
                _client = null;
                _profile = null;
                _connected = false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The runtime already published a safe fault code.
            }
        }
        client?.Dispose();
        _runCancellation?.Dispose();
        _runCancellation = null;
        _lifecycle.Dispose();
    }

    private async Task<LoggerSessionResult> RunSessionAsync(
        EdiabasReadOnlyClient client,
        Ms43LiveProfile profile,
        PollingPlan plan,
        string outputRoot,
        double requestedHz,
        Guid epochId,
        TaskCompletionSource<string> started,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        string runDirectory = Path.Combine(
            outputRoot,
            "bmw14_live_" +
            startedAtUtc.UtcDateTime.ToString(
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture) +
            "_" + Guid.NewGuid().ToString("N")[..6]);
        string sessionId = "bmw14.live." +
            startedAtUtc.UtcDateTime.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture);
        string csvPath = Path.Combine(runDirectory, "session.csv");
        string metadataPath = Path.Combine(runDirectory, "session.meta.json");
        SamplingRoute[] routes = plan.Routes
            .Select(route => new SamplingRoute(
                route.ParameterId,
                route.JobSequence,
                plan.Jobs.Single(job =>
                    job.Sequence == route.JobSequence).Job))
            .ToArray();
        SessionCsvLogger? logger = null;
        long cycles = 0;

        try
        {
            logger = new SessionCsvLogger(
                profile.CreateSessionDescriptor(sessionId, requestedHz, plan),
                new FileSessionLogStorage(runDirectory, "session"),
                startedAtUtc,
                startedTicks);
            started.TrySetResult(Path.GetFullPath(runDirectory));

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long cycleId = cycles + 1;
                LiveCycleExecution execution = await ReadWithRetryAsync(
                        client,
                        epochId,
                        cycleId,
                        plan,
                        cancellationToken)
                    .ConfigureAwait(false);
                NormalizedCycle normalized = SamplingEngine.NormalizeCycle(
                    epochId,
                    cycleId,
                    routes,
                    execution.JobBatches,
                    execution.CompletedAtUtc,
                    execution.CompletedMonotonicTicks);
                logger.AppendCycle(normalized);
                cycles++;
                RaiseProgress(cycles, execution.CompletedAtUtc, normalized);

                if (execution.FailureCode is not null)
                {
                    SessionLogMetadata failed = logger.Fail(
                        execution.CompletedAtUtc,
                        execution.CompletedMonotonicTicks,
                        SessionStopReason.AcquisitionError,
                        execution.FailureCode);
                    LoggerSessionResult result = ToResult(
                        runDirectory,
                        csvPath,
                        metadataPath,
                        epochId,
                        failed);
                    InvalidateConnection(client);
                    RaiseFault(new LoggerRuntimeFault(
                        execution.FailureCode,
                        SafeMessageFor(execution.FailureCode),
                        RequiresReconnect: true,
                        result.RunDirectory,
                        result.MetadataPath));
                    return result;
                }

                double targetSeconds = cycles / requestedHz;
                double remainingSeconds = targetSeconds -
                    Stopwatch.GetElapsedTime(startedTicks).TotalSeconds;
                if (remainingSeconds > 0)
                {
                    await Task.Delay(
                            TimeSpan.FromSeconds(remainingSeconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (logger is null)
            {
                started.TrySetCanceled(cancellationToken);
                throw;
            }
            try
            {
                SessionLogMetadata completed = logger.Complete(
                    DateTimeOffset.UtcNow,
                    Math.Max(Stopwatch.GetTimestamp(), startedTicks),
                    SessionStopReason.Requested);
                return ToResult(
                    runDirectory,
                    csvPath,
                    metadataPath,
                    epochId,
                    completed);
            }
            catch (SessionLogWriteException)
            {
                return HandleStorageFault(
                    client,
                    runDirectory,
                    csvPath,
                    metadataPath,
                    epochId,
                    cycles,
                    startedTicks);
            }
        }
        catch (SessionLogWriteException)
        {
            return HandleStorageFault(
                client,
                runDirectory,
                csvPath,
                metadataPath,
                epochId,
                cycles,
                startedTicks);
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            const string code = "LIVE_PIPELINE_ERROR";
            SessionLogMetadata? metadata = null;
            if (logger is not null)
            {
                try
                {
                    metadata = logger.Fail(
                        DateTimeOffset.UtcNow,
                        Math.Max(Stopwatch.GetTimestamp(), startedTicks),
                        SessionStopReason.AcquisitionError,
                        code);
                }
                catch
                {
                    // The fallback result remains explicit if metadata cannot be written.
                }
            }
            LoggerSessionResult result = metadata is not null
                ? ToResult(runDirectory, csvPath, metadataPath, epochId, metadata)
                : FaultFallback(
                    runDirectory,
                    csvPath,
                    metadataPath,
                    epochId,
                    cycles,
                    startedTicks,
                    code);
            InvalidateConnection(client);
            RaiseFault(new LoggerRuntimeFault(
                code,
                SafeMessageFor(code),
                RequiresReconnect: true,
                result.RunDirectory,
                File.Exists(metadataPath) ? result.MetadataPath : null));
            return result;
        }
        finally
        {
            logger?.Dispose();
        }
    }

    private static async Task<LiveCycleExecution> ReadWithRetryAsync(
        EdiabasReadOnlyClient client,
        Guid epochId,
        long cycleId,
        PollingPlan plan,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            LiveCycleExecution execution = client.ReadCycle(
                epochId,
                cycleId,
                plan,
                cancellationToken);
            if (execution.FailureCode is null ||
                !LoggerRecoveryPolicy.ShouldRetry(
                    execution.FailureCode,
                    attempt))
            {
                return execution;
            }
            await Task.Delay(
                    LoggerRecoveryPolicy.RetryDelay(attempt),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ClearCompletedRunAsync(Task<LoggerSessionResult> completed)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_runTask, completed))
                {
                    _runTask = null;
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void InvalidateConnection(EdiabasReadOnlyClient client)
    {
        bool dispose = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _connected = false;
                dispose = true;
            }
        }
        if (dispose)
        {
            client.Dispose();
        }
    }

    private LoggerSessionResult HandleStorageFault(
        EdiabasReadOnlyClient client,
        string runDirectory,
        string csvPath,
        string metadataPath,
        Guid epochId,
        long cycles,
        long startedTicks)
    {
        const string code = "CSV_WRITE_FAILED";
        SessionLogMetadata? metadata = TryReadMetadata(metadataPath);
        LoggerSessionResult result = metadata is not null
            ? ToResult(runDirectory, csvPath, metadataPath, epochId, metadata)
            : FaultFallback(
                runDirectory,
                csvPath,
                metadataPath,
                epochId,
                cycles,
                startedTicks,
                code);
        InvalidateConnection(client);
        RaiseFault(new LoggerRuntimeFault(
            code,
            SafeMessageFor(code),
            RequiresReconnect: true,
            result.RunDirectory,
            File.Exists(metadataPath) ? result.MetadataPath : null));
        return result;
    }

    private void RaiseProgress(
        long cycles,
        DateTimeOffset observedAtUtc,
        NormalizedCycle cycle)
    {
        IReadOnlyDictionary<string, double> latest = cycle.Samples
            .Where(sample =>
                sample.Quality == SampleQuality.Valid &&
                sample.Value is not null)
            .Select(sample => new
            {
                sample.ParameterId.Value,
                Numeric = TryConvertFinite(sample.Value!)
            })
            .Where(item => item.Numeric.HasValue)
            .ToDictionary(
                item => item.Value,
                item => item.Numeric!.Value,
                StringComparer.Ordinal);
        try
        {
            ProgressChanged?.Invoke(
                this,
                new LoggerRuntimeProgress(
                    cycles,
                    observedAtUtc,
                    latest,
                    cycle));
        }
        catch
        {
            // A display subscriber cannot terminate acquisition.
        }
    }

    private void RaiseFault(LoggerRuntimeFault fault)
    {
        try
        {
            Faulted?.Invoke(this, fault);
        }
        catch
        {
            // The session has already been finalized and the interface released.
        }
    }

    private static double? TryConvertFinite(object value)
    {
        try
        {
            double converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(converted) ? converted : null;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static LoggerSessionResult ToResult(
        string runDirectory,
        string csvPath,
        string metadataPath,
        Guid epochId,
        SessionLogMetadata metadata) =>
        new(
            Path.GetFullPath(runDirectory),
            Path.GetFullPath(csvPath),
            Path.GetFullPath(metadataPath),
            epochId,
            metadata.CyclesWritten,
            metadata.SamplesWritten,
            metadata.DurationSeconds,
            metadata.Status,
            metadata.FaultCode);

    private static LoggerSessionResult FaultFallback(
        string runDirectory,
        string csvPath,
        string metadataPath,
        Guid epochId,
        long cycles,
        long startedTicks,
        string code) =>
        new(
            Path.GetFullPath(runDirectory),
            Path.GetFullPath(csvPath),
            Path.GetFullPath(metadataPath),
            epochId,
            cycles,
            0,
            Stopwatch.GetElapsedTime(startedTicks).TotalSeconds,
            "faulted",
            code);

    private static SessionLogMetadata? TryReadMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SessionLogMetadata>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
        }
        catch
        {
            return null;
        }
    }

    private static string SafeMessageFor(string code) => code switch
    {
        "EDIABAS_TIMEOUT" =>
            "ECU communication timed out. Logging stopped and reconnect is required.",
        "EDIABAS_PORT_LOST" =>
            "COM5 or the diagnostic interface was lost. Logging stopped.",
        "EDIABAS_TRANSPORT_ERROR" =>
            "EDIABAS communication failed. Logging stopped and reconnect is required.",
        "CSV_WRITE_FAILED" =>
            "CSV storage failed. Logging stopped; inspect the fault metadata.",
        _ => "The live logging pipeline stopped safely. Reconnect before retrying."
    };
}
