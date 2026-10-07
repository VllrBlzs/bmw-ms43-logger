using System.Diagnostics;
using System.Globalization;
using System.Text;
using BmwMs43Logger.Core;
using EdiabasLib;

namespace BmwMs43Logger.StationaryLogger;

internal static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            StationaryOptions options = StationaryOptions.Parse(args);
            ControlledProfile profile = ControlledProfile.Load(
                options.CatalogPath,
                options.PackPath);
            profile.RunGuardSelfTests();
            profile.ValidateRuntime(
                options.EcuPath,
                options.Port,
                typeof(EdiabasNet).Assembly.Location);

            if (options.Mode == StationaryMode.Preflight)
            {
                PrintPreflight(options, profile);
                return 0;
            }

            PollingPlan plan = profile.BuildPlan(options.Selection);
            SessionRunResult result = RunSession(
                options,
                profile,
                plan,
                cancellation.Token);
            PrintRunSummary(options, plan, result);

            if (result.Cancelled)
            {
                return 130;
            }
            if (options.FaultCycle.HasValue)
            {
                bool expected = result.FailureCode == "SIMULATED_TIMEOUT" &&
                                result.Metadata.Status == "faulted";
                Console.WriteLine($"EXPECTED_FAULT_HANDLING={(expected ? "PASS" : "FAIL")}");
                Console.WriteLine($"RESULT={(expected ? "PASS" : "FAIL")}");
                return expected ? 0 : 1;
            }

            bool passed = result.FailureCode is null &&
                          result.Metadata.Status == "completed";
            Console.WriteLine($"RESULT={(passed ? "PASS" : "FAIL")}");
            return passed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("RESULT=CANCELLED");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static SessionRunResult RunSession(
        StationaryOptions options,
        ControlledProfile profile,
        PollingPlan plan,
        CancellationToken cancellationToken)
    {
        DateTimeOffset sessionStartedAtUtc = DateTimeOffset.UtcNow;
        long sessionStartedTicks = Stopwatch.GetTimestamp();
        string runDirectory = CreateRunDirectory(options, sessionStartedAtUtc);
        const string fileStem = "session";
        string sessionId = string.Join(
            ".",
            "bmw08",
            options.Mode.ToString().ToLowerInvariant(),
            options.VehicleState,
            options.Selection,
            sessionStartedAtUtc.UtcDateTime.ToString(
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture));
        SessionLogDescriptor descriptor = profile.CreateSessionDescriptor(
            sessionId,
            options.RequestedHz,
            plan);
        var metrics = new RunMetrics(plan.Routes.Select(route => route.ParameterId));
        var routes = plan.Routes.Select(route => new SamplingRoute(
            route.ParameterId,
            route.JobSequence,
            plan.Jobs.Single(job => job.Sequence == route.JobSequence).Job)).ToArray();
        var queue = new BoundedDropOldestQueue<NormalizedCycle>(capacity: 64);
        SessionLogMetadata? metadata = null;
        ICycleSource? source = null;
        string? failureCode = null;
        string? failureMessage = null;
        bool cancelled = false;
        bool comClosed = false;
        bool communicationAttempted = options.Mode == StationaryMode.Live;
        string interfaceInstance = options.Mode == StationaryMode.Live
            ? "creation_pending"
            : "none";

        using (var logger = new SessionCsvLogger(
                   descriptor,
                   new FileSessionLogStorage(runDirectory, fileStem),
                   sessionStartedAtUtc,
                   sessionStartedTicks))
        {
            try
            {
                ICycleSource activeSource = options.Mode == StationaryMode.Live
                    ? new EdiabasCycleSource(
                        options,
                        profile,
                        plan,
                        cancellationToken)
                    : new FakeCycleSource(
                        sessionStartedAtUtc,
                        sessionStartedTicks,
                        options.RequestedHz,
                        options.FaultCycle);
                source = activeSource;
                communicationAttempted = activeSource.CommunicationAttempted;
                interfaceInstance = activeSource.InterfaceInstance;
                Console.WriteLine(
                    $"LOGGING_STARTED_UTC={sessionStartedAtUtc:O}|" +
                    $"MODE={options.Mode.ToString().ToUpperInvariant()}|" +
                    $"SELECTION={options.Selection}|" +
                    $"DURATION_SECONDS={options.DurationSeconds}");

                Guid epochId = Guid.NewGuid();
                long cycleId = 0;
                double nextProgressAtSeconds = 30.0;
                long dryCycleLimit = Math.Max(
                    1,
                    (long)Math.Ceiling(
                        options.DurationSeconds * options.RequestedHz));

                while (ShouldRunNextCycle(
                           options,
                           cycleId,
                           dryCycleLimit,
                           sessionStartedTicks))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cycleId++;
                    CycleExecution execution = activeSource.ReadCycle(
                        epochId,
                        cycleId,
                        plan);
                    NormalizedCycle normalized = SamplingEngine.NormalizeCycle(
                        epochId,
                        cycleId,
                        routes,
                        execution.JobBatches,
                        execution.CompletedAtUtc,
                        execution.CompletedMonotonicTicks);
                    queue.Enqueue(normalized);
                    while (queue.TryDequeue(out NormalizedCycle? queuedCycle))
                    {
                        logger.AppendCycle(queuedCycle!);
                        metrics.Observe(execution, queuedCycle!);
                    }

                    if (execution.FailureCode is not null)
                    {
                        failureCode = execution.FailureCode;
                        failureMessage = execution.FailureMessage;
                        logger.RecordQueueDropped(queue.DroppedCount);
                        metadata = logger.Fail(
                            execution.CompletedAtUtc,
                            execution.CompletedMonotonicTicks,
                            SessionStopReason.AcquisitionError,
                            failureCode);
                        break;
                    }

                    if (options.Mode == StationaryMode.Live)
                    {
                        double elapsedSeconds =
                            Stopwatch.GetElapsedTime(sessionStartedTicks).TotalSeconds;
                        if (elapsedSeconds >= nextProgressAtSeconds)
                        {
                            Console.WriteLine(
                                $"PROGRESS_SECONDS={(int)elapsedSeconds}|" +
                                $"CYCLES={cycleId}");
                            while (nextProgressAtSeconds <= elapsedSeconds)
                            {
                                nextProgressAtSeconds += 30.0;
                            }
                        }
                        WaitForNextCycle(
                            sessionStartedTicks,
                            cycleId,
                            options.RequestedHz,
                            cancellationToken);
                    }
                }

                if (metadata is null)
                {
                    logger.RecordQueueDropped(queue.DroppedCount);
                    (DateTimeOffset finishedAtUtc, long finishedTicks) =
                        GetSuccessfulFinish(options, sessionStartedAtUtc, sessionStartedTicks);
                    metadata = logger.Complete(
                        finishedAtUtc,
                        finishedTicks,
                        SessionStopReason.Requested);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                failureCode = "CANCELLED";
                failureMessage = "User requested cancellation.";
                logger.RecordQueueDropped(queue.DroppedCount);
                (DateTimeOffset failedAtUtc, long failedTicks) =
                    GetSafeFailureFinish(options, sessionStartedAtUtc, sessionStartedTicks);
                metadata = logger.Fail(
                    failedAtUtc,
                    failedTicks,
                    SessionStopReason.AcquisitionError,
                    failureCode);
            }
            catch (SessionLogWriteException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failureCode = "PIPELINE_ERROR";
                failureMessage =
                    $"Pipeline failed with {exception.GetType().Name}.";
                logger.RecordQueueDropped(queue.DroppedCount);
                (DateTimeOffset failedAtUtc, long failedTicks) =
                    GetSafeFailureFinish(options, sessionStartedAtUtc, sessionStartedTicks);
                metadata = logger.Fail(
                    failedAtUtc,
                    failedTicks,
                    SessionStopReason.AcquisitionError,
                    failureCode);
            }
            finally
            {
                try
                {
                    source?.Dispose();
                    comClosed = true;
                }
                catch (Exception exception)
                {
                    comClosed = false;
                    failureCode ??= "INTERFACE_DISPOSE_ERROR";
                    failureMessage ??=
                        $"Interface disposal failed with {exception.GetType().Name}.";
                }
            }
        }

        if (metadata is null)
        {
            throw new InvalidOperationException("Session metadata was not finalized.");
        }
        AcceptanceReport report = metrics.BuildReport(
            options,
            profile,
            plan,
            metadata,
            communicationAttempted,
            interfaceInstance,
            comClosed,
            failureCode,
            failureMessage);
        string reportPath = AcceptanceReportWriter.Write(
            Path.Combine(runDirectory, "session.acceptance.json"),
            report);
        return new SessionRunResult(
            metadata,
            report,
            Path.GetFullPath(Path.Combine(runDirectory, metadata.CsvFileName)),
            Path.GetFullPath(Path.Combine(runDirectory, metadata.MetadataFileName)),
            reportPath,
            failureCode,
            cancelled);
    }

    private static bool ShouldRunNextCycle(
        StationaryOptions options,
        long cyclesCompleted,
        long dryCycleLimit,
        long sessionStartedTicks)
    {
        if (options.Mode == StationaryMode.DryRun)
        {
            return cyclesCompleted < dryCycleLimit;
        }
        if (cyclesCompleted == 0)
        {
            return true;
        }
        return Stopwatch.GetElapsedTime(sessionStartedTicks).TotalSeconds <
               options.DurationSeconds;
    }

    private static void WaitForNextCycle(
        long sessionStartedTicks,
        long completedCycles,
        double requestedHz,
        CancellationToken cancellationToken)
    {
        double targetSeconds = completedCycles / requestedHz;
        double remainingSeconds = targetSeconds -
            Stopwatch.GetElapsedTime(sessionStartedTicks).TotalSeconds;
        if (remainingSeconds <= 0)
        {
            return;
        }
        Task.Delay(TimeSpan.FromSeconds(remainingSeconds), cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static (DateTimeOffset FinishedAtUtc, long FinishedTicks) GetSuccessfulFinish(
        StationaryOptions options,
        DateTimeOffset sessionStartedAtUtc,
        long sessionStartedTicks)
    {
        if (options.Mode == StationaryMode.DryRun)
        {
            return (
                sessionStartedAtUtc.AddSeconds(options.DurationSeconds),
                sessionStartedTicks + checked((long)Math.Round(
                    (double)options.DurationSeconds * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero)));
        }
        return (DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    }

    private static (DateTimeOffset FinishedAtUtc, long FinishedTicks) GetSafeFailureFinish(
        StationaryOptions options,
        DateTimeOffset sessionStartedAtUtc,
        long sessionStartedTicks)
    {
        if (options.Mode == StationaryMode.DryRun)
        {
            return (
                sessionStartedAtUtc.AddSeconds(options.DurationSeconds),
                sessionStartedTicks + checked((long)Math.Round(
                    (double)options.DurationSeconds * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero)));
        }
        return (
            DateTimeOffset.UtcNow,
            Math.Max(Stopwatch.GetTimestamp(), sessionStartedTicks));
    }

    private static string CreateRunDirectory(
        StationaryOptions options,
        DateTimeOffset startedAtUtc)
    {
        string name = string.Join(
            "_",
            "bmw08",
            options.Mode.ToString().ToLowerInvariant(),
            options.VehicleState,
            options.Selection,
            startedAtUtc.UtcDateTime.ToString(
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("N")[..6]);
        string path = Path.Combine(options.OutputDirectory, name);
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static void PrintPreflight(
        StationaryOptions options,
        ControlledProfile profile)
    {
        Console.WriteLine("BMW-04/05/08 INTEGRATED PREFLIGHT");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine("GUARD_TESTS=PASS");
        Console.WriteLine($"PORT={options.Port}");
        Console.WriteLine($"CATALOG_ENTRIES={profile.Catalog.Entries.Count}");
        Console.WriteLine($"CATALOG_SHA256={ControlledProfile.ExpectedCatalogSha256}");
        Console.WriteLine($"PACK_JOBS={profile.Pack.Entries.Count}");
        Console.WriteLine($"PACK_SHA256={ControlledProfile.ExpectedPackSha256}");
        Console.WriteLine("ALL_ARGUMENTS_EMPTY=True");
        Console.WriteLine("UNKNOWN_DISPATCH_POLICY=reject");
        Console.WriteLine($"EXECUTION_POLICY={PollingPlanner.SerialExecutionPolicy}");
        foreach (string selection in StationaryOptions.ValidSelections)
        {
            PollingPlan plan = profile.BuildPlan(selection);
            Console.WriteLine(
                $"PLAN={selection}|CHANNELS={plan.SelectedParameterCount}|" +
                $"JOBS={plan.Jobs.Count}|" +
                $"RESULT_REQUESTS={plan.Jobs.Sum(job => job.ResultNames.Count)}");
        }
        Console.WriteLine("LIVE_DURATION_SECONDS=10-1200");
        Console.WriteLine("REQUESTED_HZ_RANGE=0.2-5.0");
        Console.WriteLine($"SGBD_SHA256={ControlledProfile.ExpectedSgbdSha256}");
        Console.WriteLine($"BACKEND_SHA256={ControlledProfile.ExpectedBackendSha256}");
        Console.WriteLine("RESULT=PASS");
    }

    private static void PrintRunSummary(
        StationaryOptions options,
        PollingPlan plan,
        SessionRunResult result)
    {
        AcceptanceReport report = result.Report;
        Console.WriteLine("BMW-04/05/08 INTEGRATED STATIONARY LOGGER");
        Console.WriteLine($"MODE={options.Mode.ToString().ToUpperInvariant()}");
        Console.WriteLine($"COMMUNICATION_ATTEMPTED={report.CommunicationAttempted}");
        Console.WriteLine($"INTERFACE_INSTANCE={report.InterfaceInstance}");
        Console.WriteLine($"VEHICLE_STATE={options.VehicleState}");
        Console.WriteLine($"SELECTION={options.Selection}");
        Console.WriteLine($"PLANNED_CHANNELS={plan.SelectedParameterCount}");
        Console.WriteLine($"PLANNED_JOBS={plan.Jobs.Count}");
        Console.WriteLine($"CYCLES={report.CyclesObserved}");
        Console.WriteLine($"SAMPLES={report.SamplesObserved}");
        foreach ((string quality, long count) in report.QualityCounts)
        {
            Console.WriteLine($"QUALITY_{quality.ToUpperInvariant()}={count}");
        }
        Console.WriteLine($"QUEUE_DROPPED={report.QueueDropped}");
        Console.WriteLine($"WRITE_ERRORS={report.WriteErrors}");
        Console.WriteLine($"CYCLE_MEDIAN_MS={Format(report.CycleMedianMs)}");
        Console.WriteLine($"CYCLE_P95_MS={Format(report.CycleP95Ms)}");
        Console.WriteLine($"EFFECTIVE_HZ={Format(report.EffectiveHz)}");
        Console.WriteLine($"CSV={result.CsvPath}");
        Console.WriteLine($"METADATA={result.MetadataPath}");
        Console.WriteLine($"REPORT={result.ReportPath}");
        Console.WriteLine($"COM_CLOSED={report.ComClosed}");
        Console.WriteLine($"SESSION_RESULT={report.Result}");
        if (result.FailureCode is not null)
        {
            Console.WriteLine($"FAILURE_CODE={result.FailureCode}");
        }
    }

    private static string Format(double? value) => value.HasValue
        ? value.Value.ToString("F2", CultureInfo.InvariantCulture)
        : "N/A";
}

internal enum StationaryMode
{
    Preflight,
    DryRun,
    Live
}

internal sealed record StationaryOptions(
    StationaryMode Mode,
    string Selection,
    string VehicleState,
    int DurationSeconds,
    double RequestedHz,
    long? FaultCycle,
    string OutputDirectory,
    string EcuPath,
    string Port,
    string CatalogPath,
    string PackPath)
{
    public static readonly string[] ValidSelections =
        ["rpm_only", "core4", "batch8", "core16", "all28"];

    public static StationaryOptions Parse(IReadOnlyList<string> args)
    {
        bool preflight = false;
        bool dryRun = false;
        bool live = false;
        bool confirmed = false;
        bool outputSpecified = false;
        string selection = "all28";
        string vehicleState = "simulated";
        int durationSeconds = 6;
        double requestedHz = 5.0;
        long? faultCycle = null;
        string outputDirectory = Path.Combine("artifacts", "bmw08");
        string ecuPath = @"C:\EDIABAS\ECU";
        string port = "COM5";

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--preflight":
                    preflight = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--live":
                    live = true;
                    break;
                case "--confirm-read-only":
                    confirmed = true;
                    break;
                case "--selection" when index + 1 < args.Count:
                    selection = args[++index].ToLowerInvariant();
                    break;
                case "--vehicle-state" when index + 1 < args.Count:
                    vehicleState = args[++index].ToLowerInvariant();
                    break;
                case "--duration-seconds" when index + 1 < args.Count:
                    durationSeconds = int.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--requested-hz" when index + 1 < args.Count:
                    requestedHz = double.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--inject-fault-cycle" when index + 1 < args.Count:
                    faultCycle = long.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--output-dir" when index + 1 < args.Count:
                    outputDirectory = args[++index];
                    outputSpecified = true;
                    break;
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = args[++index].ToUpperInvariant();
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        int modeCount = (preflight ? 1 : 0) +
                        (dryRun ? 1 : 0) +
                        (live ? 1 : 0);
        if (modeCount != 1)
        {
            throw new ArgumentException(
                "Specify exactly one mode: --preflight, --dry-run or --live.");
        }
        StationaryMode mode = live
            ? StationaryMode.Live
            : dryRun
                ? StationaryMode.DryRun
                : StationaryMode.Preflight;
        if (!ValidSelections.Contains(selection, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown selection: {selection}");
        }
        if (!double.IsFinite(requestedHz) || requestedHz is < 0.2 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedHz),
                "Requested Hz is bounded to 0.2-5.0.");
        }
        if (mode == StationaryMode.Live)
        {
            if (!confirmed)
            {
                throw new InvalidOperationException(
                    "Live mode requires --confirm-read-only.");
            }
            if (vehicleState is not ("engine-off" or "idle"))
            {
                throw new ArgumentException(
                    "Live mode requires --vehicle-state engine-off|idle.");
            }
            if (durationSeconds is < 10 or > 1200)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(durationSeconds),
                    "Live duration is bounded to 10-1200 seconds.");
            }
            if (faultCycle.HasValue)
            {
                throw new ArgumentException(
                    "Fault injection is forbidden in live mode.");
            }
            if (!outputSpecified)
            {
                outputDirectory = Path.Combine("data", "private", "bmw08-live");
            }
        }
        else if (mode == StationaryMode.DryRun)
        {
            if (vehicleState != "simulated")
            {
                throw new ArgumentException(
                    "Dry run vehicle state must remain simulated.");
            }
            if (durationSeconds is < 1 or > 300)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(durationSeconds),
                    "Dry-run duration is bounded to 1-300 seconds.");
            }
            long cycles = Math.Max(1, (long)Math.Ceiling(durationSeconds * requestedHz));
            if (faultCycle.HasValue &&
                (faultCycle.Value < 1 || faultCycle.Value > cycles))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(faultCycle),
                    "Injected fault cycle must fall inside the dry run.");
            }
        }
        else if (confirmed || faultCycle.HasValue)
        {
            throw new ArgumentException(
                "Preflight does not accept confirmation or fault injection.");
        }

        return new StationaryOptions(
            mode,
            selection,
            vehicleState,
            durationSeconds,
            requestedHz,
            faultCycle,
            Path.GetFullPath(outputDirectory),
            Path.GetFullPath(ecuPath),
            port,
            Path.GetFullPath(Path.Combine(
                "config",
                "ms430ds0-parameter-catalog.draft.json")),
            Path.GetFullPath(Path.Combine(
                "config",
                "ms430ds0-bounded-live-test-pack.json")));
    }
}

internal sealed record SessionRunResult(
    SessionLogMetadata Metadata,
    AcceptanceReport Report,
    string CsvPath,
    string MetadataPath,
    string ReportPath,
    string? FailureCode,
    bool Cancelled);
