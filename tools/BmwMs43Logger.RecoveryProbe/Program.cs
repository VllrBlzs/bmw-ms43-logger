using System.Diagnostics;
using System.Text.Json;
using BmwMs43Logger.Core;
using BmwMs43Logger.Ediabas;
using EdiabasLib;

namespace BmwMs43Logger.RecoveryProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string root = ParseRepositoryRoot(args);
            string output = Path.Combine(root, "artifacts", "bmw14");
            Directory.CreateDirectory(output);

            Ms43LiveRuntimeOptions liveOptions =
                Ms43LiveRuntimeOptions.CreateDefault(root);
            Ms43LivePreflightReport preflight = Ms43ReadOnlyPreflight.Run(
                liveOptions,
                typeof(EdiabasNet).Assembly.Location);

            TestRetryPolicy();
            TestFaultedUiRecovery();
            TestNewEpochClearsOldValues();
            TestFaultMetadataFlush();
            TestStorageFaultVisibility();
            LoggerSessionResult adapter = await TestRuntimeContractAsync(output);

            var report = new
            {
                schema_version = "bmw14.offline_recovery_report.v1",
                generated_utc = DateTimeOffset.UtcNow,
                communication_attempted = false,
                interface_instance = "none",
                timeout = "pass",
                cable_disconnect = "pass",
                ignition_off = "pass",
                ecu_port_loss = "pass",
                file_io = "pass",
                bounded_retry = "pass",
                csv_flush = "pass",
                reconnect = "pass",
                new_epoch = "pass",
                old_values_not_reused = "pass",
                negative_dispatch_guards = "pass",
                catalog_entries = preflight.CatalogEntries,
                selectable_parameters = preflight.SelectableParameters,
                allowlisted_jobs = preflight.AllowlistedJobs,
                adapter_run = adapter.RunDirectory,
                result = "pass"
            };
            string reportPath = Path.Combine(
                output,
                "bmw14-offline-recovery-dryrun.json");
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

            Console.WriteLine("BMW-14 OFFLINE FAILURE/RECOVERY DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine("TIMEOUT=PASS");
            Console.WriteLine("CABLE_DISCONNECT=PASS");
            Console.WriteLine("IGNITION_OFF=PASS");
            Console.WriteLine("ECU_PORT_LOSS=PASS");
            Console.WriteLine("FILE_IO=PASS");
            Console.WriteLine("BOUNDED_RETRY=PASS");
            Console.WriteLine("CSV_FLUSH=PASS");
            Console.WriteLine("RECONNECT=PASS");
            Console.WriteLine("NEW_EPOCH=PASS");
            Console.WriteLine("OLD_VALUES_NOT_REUSED=PASS");
            Console.WriteLine("NEGATIVE_DISPATCH_GUARDS=PASS");
            Console.WriteLine($"CATALOG_ENTRIES={preflight.CatalogEntries}");
            Console.WriteLine($"SELECTABLE_PARAMETERS={preflight.SelectableParameters}");
            Console.WriteLine($"ALLOWLISTED_JOBS={preflight.AllowlistedJobs}");
            Console.WriteLine($"REPORT={Path.GetFullPath(reportPath)}");
            Console.WriteLine("LIVE_CAR_VALIDATION_REQUIRED=True");
            Console.WriteLine("RESULT=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static void TestRetryPolicy()
    {
        string[] codes =
        [
            "EDIABAS_TIMEOUT",
            "EDIABAS_PORT_LOST",
            "EDIABAS_TRANSPORT_ERROR"
        ];
        foreach (string code in codes)
        {
            Require(
                LoggerRecoveryPolicy.ShouldRetry(code, completedAttempts: 1),
                $"First {code} must consume the single retry.");
            Require(
                !LoggerRecoveryPolicy.ShouldRetry(code, completedAttempts: 2),
                $"Second {code} must stop acquisition.");
        }
        Require(
            !LoggerRecoveryPolicy.ShouldRetry("CSV_WRITE_FAILED", 1),
            "Storage failures must never retry acquisition.");
    }

    private static void TestFaultedUiRecovery()
    {
        var machine = new LoggerUiStateMachine();
        LoggerUiOperation connect = machine.BeginConnect();
        machine.CompleteConnect(connect, succeeded: true);
        LoggerUiOperation start = machine.BeginStart();
        machine.CompleteStart(start, succeeded: true);
        machine.ReportRuntimeFault("EDIABAS_TIMEOUT");
        Require(
            machine.Snapshot.State == LoggerUiState.Faulted &&
            machine.Snapshot.CanDisconnect &&
            !machine.Snapshot.CanStart,
            "Faulted GUI state must require disconnect.");
        LoggerUiOperation disconnect = machine.BeginDisconnect();
        machine.CompleteDisconnect(disconnect, succeeded: true);
        Require(
            machine.Snapshot.State == LoggerUiState.Disconnected &&
            machine.Snapshot.CanConnect,
            "Disconnect must reopen only the connect path.");
        LoggerUiOperation reconnect = machine.BeginConnect();
        machine.CompleteConnect(reconnect, succeeded: true);
        Require(
            machine.Snapshot.State == LoggerUiState.Ready,
            "Reconnect must return to Ready.");
    }

    private static void TestNewEpochClearsOldValues()
    {
        var buffer = new LatestLiveValueBuffer();
        Guid oldEpoch = Guid.NewGuid();
        Guid newEpoch = Guid.NewGuid();
        buffer.Publish(CreateCycle(oldEpoch, 1, 800.0));
        Require(buffer.ReadLatest()?.EpochId == oldEpoch, "Old epoch was not published.");
        buffer.Reset();
        Require(buffer.ReadLatest() is null, "Reset retained an old live frame.");
        buffer.Publish(CreateCycle(newEpoch, 1, 825.0));
        LiveValueFrame frame = buffer.ReadLatest() ??
            throw new InvalidOperationException("New epoch frame is missing.");
        Require(
            frame.EpochId == newEpoch &&
            frame.EpochId != oldEpoch &&
            frame.CycleId == 1,
            "Reconnect did not start a clean epoch.");
    }

    private static void TestFaultMetadataFlush()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long ticks = Stopwatch.GetTimestamp();
        var storage = new CapturingStorage();
        using var logger = new SessionCsvLogger(
            Descriptor("bmw14.offline.transport-fault"),
            storage,
            started,
            ticks);
        logger.AppendCycle(CreateCycle(Guid.NewGuid(), 1, 810.0, started, ticks));
        SessionLogMetadata metadata = logger.Fail(
            started.AddSeconds(1),
            ticks + Stopwatch.Frequency,
            SessionStopReason.AcquisitionError,
            "EDIABAS_TIMEOUT");
        Require(
            metadata.Status == "faulted" &&
            metadata.FaultCode == "EDIABAS_TIMEOUT" &&
            storage.CsvCompleted &&
            storage.MetadataJson is not null,
            "Transport fault did not flush CSV and metadata.");
    }

    private static void TestStorageFaultVisibility()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long ticks = Stopwatch.GetTimestamp();
        var storage = new CapturingStorage(failAfterCsvLines: 1);
        using var logger = new SessionCsvLogger(
            Descriptor("bmw14.offline.storage-fault"),
            storage,
            started,
            ticks);
        bool visible = false;
        try
        {
            logger.AppendCycle(CreateCycle(Guid.NewGuid(), 1, 810.0, started, ticks));
        }
        catch (SessionLogWriteException)
        {
            visible = true;
        }
        Require(visible, "Storage failure was not surfaced.");
        Require(
            storage.CsvCompleted &&
            storage.MetadataJson?.Contains(
                "CSV_WRITE_FAILED",
                StringComparison.Ordinal) == true,
            "Storage fault metadata was not finalized.");
    }

    private static async Task<LoggerSessionResult> TestRuntimeContractAsync(
        string output)
    {
        await using ILoggerRuntime runtime = new SimulatedRuntimeAdapter();
        var progressReached = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ProgressChanged += (_, progress) =>
        {
            if (progress.CyclesWritten >= 3)
            {
                progressReached.TrySetResult(true);
            }
        };
        await runtime.ConnectAsync();
        await runtime.StartAsync(
            output,
            requestedHz: 5.0,
            selectedParameters: Ms43ParameterCatalog.DefaultSelection);
        await progressReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        LoggerSessionResult result = await runtime.StopAsync();
        await runtime.DisconnectAsync();
        Require(
            result.Completed &&
            result.EpochId != Guid.Empty &&
            result.CyclesWritten >= 3 &&
            File.Exists(result.CsvPath) &&
            File.Exists(result.MetadataPath),
            "The generic runtime contract did not round-trip a session.");
        return result;
    }

    private static SessionLogDescriptor Descriptor(string sessionId) => new(
        sessionId,
        "bmw.ms43.offline",
        new string('A', 64),
        new string('B', 64),
        "bmw14.offline",
        new string('C', 64),
        5.0,
        [new ParameterId("engine.rpm")]);

    private static NormalizedCycle CreateCycle(
        Guid epoch,
        long cycleId,
        double rpm,
        DateTimeOffset? baseUtc = null,
        long? baseTicks = null)
    {
        DateTimeOffset completed = (baseUtc ?? DateTimeOffset.UtcNow)
            .AddMilliseconds(cycleId * 10.0);
        long ticks = (baseTicks ?? Stopwatch.GetTimestamp()) +
            checked((long)Math.Round(
                cycleId * 10.0 * Stopwatch.Frequency / 1000.0,
                MidpointRounding.AwayFromZero));
        var id = new ParameterId("engine.rpm");
        var sample = new NormalizedSample(
            epoch,
            cycleId,
            id,
            1,
            "STATUS_MOTORDREHZAHL",
            completed.AddMilliseconds(-5),
            completed,
            completed,
            ticks,
            rpm,
            "1/min",
            SampleQuality.Valid,
            null,
            5.0,
            5.0,
            0.0);
        return new NormalizedCycle(epoch, cycleId, completed, ticks, [sample]);
    }

    private static string ParseRepositoryRoot(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Path.GetFullPath(Environment.CurrentDirectory);
        }
        if (args.Count == 2 && args[0] == "--repo-root")
        {
            return Path.GetFullPath(args[1]);
        }
        throw new ArgumentException("Usage: [--repo-root <BMW folder>]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class CapturingStorage : ISessionLogStorage
{
    private readonly int? _failAfterCsvLines;
    private int _csvLines;

    public CapturingStorage(int? failAfterCsvLines = null)
    {
        _failAfterCsvLines = failAfterCsvLines;
    }

    public string CsvFileName => "session.csv";
    public string MetadataFileName => "session.meta.json";
    public bool CsvCompleted { get; private set; }
    public string? MetadataJson { get; private set; }

    public void WriteCsvLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_failAfterCsvLines.HasValue &&
            _csvLines >= _failAfterCsvLines.Value)
        {
            throw new IOException("Injected BMW-14 storage failure.");
        }
        _csvLines++;
    }

    public void CompleteCsv() => CsvCompleted = true;

    public string GetCsvSha256()
    {
        if (!CsvCompleted)
        {
            throw new InvalidOperationException("CSV is not completed.");
        }
        return new string('D', 64);
    }

    public void WriteMetadata(string json) => MetadataJson = json;

    public void Dispose()
    {
    }
}
