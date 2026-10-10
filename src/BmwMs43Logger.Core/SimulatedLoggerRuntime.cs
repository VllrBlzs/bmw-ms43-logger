using System.Diagnostics;
using System.Globalization;

namespace BmwMs43Logger.Core;

public sealed record SimulatedLoggerProgress(
    long CyclesWritten,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<string, double> LatestValues,
    NormalizedCycle Cycle);

public sealed record SimulatedLoggerSessionResult(
    string RunDirectory,
    string CsvPath,
    string MetadataPath,
    long CyclesWritten,
    long SamplesWritten,
    double DurationSeconds);

public delegate void SimulatedLoggerProgressEventHandler(
    object? sender,
    SimulatedLoggerProgress progress);

public delegate void SimulatedLoggerFaultEventHandler(
    object? sender,
    string safeError);

public sealed class SimulatedLoggerRuntime : IAsyncDisposable
{
    private const string SgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string BackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    private const string CatalogSha256 =
        "5ABF17EE1730546CE2FE4A4D950650E76B4EB7005AAF830068A413CF4903693C";

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task<SimulatedLoggerSessionResult>? _runTask;
    private bool _connected;
    private bool _disposed;

    public event SimulatedLoggerProgressEventHandler? ProgressChanged;
    public event SimulatedLoggerFaultEventHandler? Faulted;

    public static IReadOnlyList<ParameterId> Parameters =>
        Ms43ParameterCatalog.ApprovedParameters;

    public bool IsConnected => _connected;
    public bool IsRunning => _runTask is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected)
            {
                throw new InvalidOperationException("The simulated runtime is already connected.");
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<string> StartAsync(
        string outputDirectory,
        double requestedHz,
        CancellationToken cancellationToken = default) =>
        await StartAsync(
                outputDirectory,
                requestedHz,
                Parameters,
                cancellationToken)
            .ConfigureAwait(false);

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
            if (!_connected)
            {
                throw new InvalidOperationException("Connect before starting a session.");
            }
            if (_runTask is not null)
            {
                throw new InvalidOperationException("A simulated session is already running.");
            }

            _runCancellation = new CancellationTokenSource();
            _runTask = RunSessionAsync(
                Path.GetFullPath(outputDirectory),
                requestedHz,
                selection,
                started,
                _runCancellation.Token);
        }
        finally
        {
            _lifecycle.Release();
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => started.TrySetCanceled(cancellationToken));
        return await started.Task.ConfigureAwait(false);
    }

    public async Task<SimulatedLoggerSessionResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        Task<SimulatedLoggerSessionResult> task;
        CancellationTokenSource cancellation;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            task = _runTask ?? throw new InvalidOperationException(
                "No simulated session is running.");
            cancellation = _runCancellation ?? throw new InvalidOperationException(
                "The running session has no cancellation source.");
            cancellation.Cancel();
        }
        finally
        {
            _lifecycle.Release();
        }

        SimulatedLoggerSessionResult result = await task.WaitAsync(cancellationToken)
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
            if (!_connected)
            {
                throw new InvalidOperationException("The simulated runtime is disconnected.");
            }
            if (_runTask is not null)
            {
                throw new InvalidOperationException("Stop logging before disconnecting.");
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            _connected = false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<SimulatedLoggerSessionResult>? task = null;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_runTask is not null)
            {
                _runCancellation!.Cancel();
                task = _runTask;
            }
            _connected = false;
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
                // Disposal preserves the original fault already raised by the runtime.
            }
        }
        _runCancellation?.Dispose();
        _lifecycle.Dispose();
    }

    private async Task<SimulatedLoggerSessionResult> RunSessionAsync(
        string outputRoot,
        double requestedHz,
        IReadOnlyList<ParameterId> selectedParameters,
        TaskCompletionSource<string> started,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        string runDirectory = Path.Combine(
            outputRoot,
            "bmw14_simulated_" +
            startedAtUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) +
            "_" + Guid.NewGuid().ToString("N")[..6]);
        string sessionId = "bmw14.simulated." +
            startedAtUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var descriptor = new SessionLogDescriptor(
            sessionId,
            "bmw.e39.525i.m54b25.ms43.ms430ds0.simulated",
            SgbdSha256,
            BackendSha256,
            "0.5.0-bmw14-runtime-contract",
            CatalogSha256,
            requestedHz,
            selectedParameters);
        long cycles = 0;
        Guid epoch = Guid.NewGuid();
        SessionCsvLogger? logger = null;

        try
        {
            logger = new SessionCsvLogger(
                descriptor,
                new FileSessionLogStorage(runDirectory, "session"),
                startedAtUtc,
                startedTicks);
            started.TrySetResult(Path.GetFullPath(runDirectory));

            TimeSpan period = TimeSpan.FromSeconds(1.0 / requestedHz);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cycles++;
                DateTimeOffset completedAtUtc = DateTimeOffset.UtcNow;
                long completedTicks = Stopwatch.GetTimestamp();
                NormalizedCycle cycle = CreateCycle(
                    epoch,
                    cycles,
                    completedAtUtc,
                    completedTicks,
                    selectedParameters);
                logger.AppendCycle(cycle);
                RaiseProgress(cycles, completedAtUtc, cycle);
                await Task.Delay(period, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset finishedAtUtc = DateTimeOffset.UtcNow;
            long finishedTicks = Stopwatch.GetTimestamp();
            SessionLogMetadata metadata = logger!.Complete(
                finishedAtUtc,
                finishedTicks,
                SessionStopReason.Requested);
            string csvPath = Path.Combine(runDirectory, "session.csv");
            string metadataPath = Path.Combine(runDirectory, "session.meta.json");
            return new SimulatedLoggerSessionResult(
                Path.GetFullPath(runDirectory),
                Path.GetFullPath(csvPath),
                Path.GetFullPath(metadataPath),
                metadata.CyclesWritten,
                metadata.SamplesWritten,
                metadata.DurationSeconds);
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            string safeError = exception is SessionLogWriteException
                ? "SIMULATED_STORAGE_ERROR"
                : "SIMULATED_RUNTIME_ERROR";
            if (logger is not null)
            {
                try
                {
                    logger.Fail(
                        DateTimeOffset.UtcNow,
                        Math.Max(Stopwatch.GetTimestamp(), startedTicks),
                        SessionStopReason.AcquisitionError,
                        safeError);
                }
                catch
                {
                    // A storage failure may already have finalized fault metadata.
                }
            }
            Faulted?.Invoke(this, safeError);
            throw;
        }
        finally
        {
            logger?.Dispose();
        }
    }

    private async Task ClearCompletedRunAsync(Task<SimulatedLoggerSessionResult> completed)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_runTask, completed))
            {
                _runTask = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static NormalizedCycle CreateCycle(
        Guid epoch,
        long cycleId,
        DateTimeOffset completedAtUtc,
        long completedTicks,
        IReadOnlyList<ParameterId> selectedParameters)
    {
        var samples = new NormalizedSample[selectedParameters.Count];
        for (int index = 0; index < selectedParameters.Count; index++)
        {
            ParameterId id = selectedParameters[index];
            bool rpm = id.Value == "engine.rpm";
            double ageMs = rpm ? 45.0 : 4.0;
            long ageTicks = TicksFromMilliseconds(ageMs);
            samples[index] = new NormalizedSample(
                epoch,
                cycleId,
                id,
                rpm ? 1 : 2,
                rpm ? "SIMULATED_STATUS_MOTORDREHZAHL" : "SIMULATED_STATUS_MESSWERTEBLOCK",
                completedAtUtc.AddMilliseconds(-(ageMs + 35.0)),
                completedAtUtc.AddMilliseconds(-ageMs),
                completedAtUtc.AddMilliseconds(-ageMs),
                completedTicks - ageTicks,
                SyntheticValue(id.Value, cycleId),
                SyntheticUnit(id.Value),
                SampleQuality.Valid,
                null,
                35.0,
                35.0,
                ageMs);
        }
        return new NormalizedCycle(epoch, cycleId, completedAtUtc, completedTicks, samples);
    }

    private static double SyntheticValue(string id, long cycleId)
    {
        double wave = Math.Sin(cycleId / 8.0);
        return id switch
        {
            "engine.rpm" => Math.Round(800.0 + 24.0 * wave, 1),
            "vehicle.speed" => 0.0,
            "driver.pedal.angle" => Math.Round(Math.Max(0.0, wave * 4.0), 3),
            "throttle.angle" => Math.Round(1.2 + Math.Max(0.0, wave), 3),
            "intake.air.mass" => Math.Round(13.4 + wave, 3),
            "intake.air.temperature" => 42.0,
            "engine.coolant.temperature" => Math.Round(91.0 + wave * 0.5, 3),
            "engine.oil.temperature" => Math.Round(89.0 + wave * 0.25, 3),
            "fuel.injection.time" => Math.Round(2.1 + wave * 0.08, 4),
            "idle.control.duty" => Math.Round(39.0 + wave * 1.5, 3),
            "electrical.terminal15.voltage" => Math.Round(13.9 + wave * 0.03, 3),
            "lambda.bank1.integrator" => Math.Round(0.98 + wave * 0.02, 5),
            "lambda.bank2.integrator" => Math.Round(0.97 + wave * 0.02, 5),
            "lambda.precat.bank1.heater.duty" => 99.314,
            "lambda.precat.bank2.heater.duty" => 99.314,
            "lambda.postcat.bank1.heater.duty" => Math.Round(92.0 + wave * 2.0, 3),
            "lambda.postcat.bank2.heater.duty" => Math.Round(92.5 + wave * 2.0, 3),
            "engine.load" => Math.Round(96.0 + wave * 5.0, 3),
            "knock.cylinder2.mean.voltage" => Math.Round(0.20 + wave * 0.02, 5),
            "knock.cylinder5.mean.voltage" => Math.Round(0.21 + wave * 0.02, 5),
            "ambient.pressure" => 997.0,
            "electrical.battery.voltage" => Math.Round(13.78 + wave * 0.03, 3),
            _ => throw new InvalidOperationException($"No simulated value for {id}.")
        };
    }

    private static string SyntheticUnit(string id) => id switch
    {
        "engine.rpm" => "1/min",
        "vehicle.speed" => "km/h",
        "driver.pedal.angle" => "GradPWG",
        "throttle.angle" => "GradDK",
        "intake.air.mass" => "kg/h",
        "intake.air.temperature" or
        "engine.coolant.temperature" or
        "engine.oil.temperature" => "GradC",
        "fuel.injection.time" => "ms",
        "idle.control.duty" or
        "lambda.precat.bank1.heater.duty" or
        "lambda.precat.bank2.heater.duty" or
        "lambda.postcat.bank1.heater.duty" or
        "lambda.postcat.bank2.heater.duty" => "%",
        "electrical.terminal15.voltage" or
        "knock.cylinder2.mean.voltage" or
        "knock.cylinder5.mean.voltage" or
        "electrical.battery.voltage" => "V",
        "lambda.bank1.integrator" or "lambda.bank2.integrator" => "-",
        "engine.load" => "mg/HUB",
        "ambient.pressure" => "hPa",
        _ => throw new InvalidOperationException($"No simulated unit for {id}.")
    };

    private void RaiseProgress(
        long cycles,
        DateTimeOffset observedAtUtc,
        NormalizedCycle cycle)
    {
        var latest = cycle.Samples
            .ToDictionary(
                sample => sample.ParameterId.Value,
                sample => Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        ProgressChanged?.Invoke(
            this,
            new SimulatedLoggerProgress(cycles, observedAtUtc, latest, cycle));
    }

    private static long TicksFromMilliseconds(double milliseconds) =>
        checked((long)Math.Round(
            milliseconds * Stopwatch.Frequency / 1000.0,
            MidpointRounding.AwayFromZero));
}
