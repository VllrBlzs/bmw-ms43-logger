using System.Text.Json;
using BmwMs43Logger.Core;
using BmwMs43Logger.Ediabas;
using EdiabasLib;

namespace BmwMs43Logger.LiveAcceptanceProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            AcceptanceOptions options = AcceptanceOptions.Parse(args);
            Ms43LiveRuntimeOptions runtimeOptions = new(
                options.EcuPath,
                options.Port,
                options.CatalogPath,
                options.PackPath);

            if (options.Mode == AcceptanceMode.Preflight)
            {
                Ms43LivePreflightReport report = Ms43ReadOnlyPreflight.Run(
                    runtimeOptions,
                    typeof(EdiabasNet).Assembly.Location);
                PrintPreflight(report);
                return 0;
            }

            await using var runtime = new EdiabasLoggerRuntime(runtimeOptions);
            var faulted = new TaskCompletionSource<LoggerRuntimeFault>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Guid epoch = Guid.Empty;
            long cycles = 0;
            runtime.ProgressChanged += (_, progress) =>
            {
                epoch = progress.Cycle.EpochId;
                cycles = progress.CyclesWritten;
                if (cycles == 1 || cycles % 25 == 0)
                {
                    Console.WriteLine(
                        $"PROGRESS_CYCLES={cycles}|EPOCH={epoch:D}");
                }
            };
            runtime.Faulted += (_, fault) => faulted.TrySetResult(fault);

            await runtime.ConnectAsync(cancellation.Token);
            Console.WriteLine("LIVE_HANDSHAKE=PASS");
            Console.WriteLine("DISPATCH_POLICY=PINNED_STATUS_EMPTY_ARGUMENT_ONLY");
            IReadOnlyList<ParameterId> selected = options.Selection == "approved22"
                ? Ms43ParameterCatalog.ApprovedParameters
                : Ms43ParameterCatalog.DefaultSelection;
            string runDirectory = await runtime.StartAsync(
                options.OutputDirectory,
                options.RequestedHz,
                selected,
                cancellation.Token);
            Console.WriteLine($"RUN_DIRECTORY={runDirectory}");
            Console.WriteLine($"ACTION={options.Action.ToUpperInvariant()}");

            LoggerSessionResult result;
            LoggerRuntimeFault? observedFault = null;
            if (options.Mode == AcceptanceMode.ExpectLoss)
            {
                Console.WriteLine(
                    "ACTION_NOW=Perform the named disconnect/ignition action; do not use INPA concurrently.");
                try
                {
                    observedFault = await faulted.Task.WaitAsync(
                        TimeSpan.FromSeconds(options.DurationSeconds),
                        cancellation.Token);
                }
                catch (TimeoutException)
                {
                    result = await runtime.StopAsync(cancellation.Token);
                    await runtime.DisconnectAsync(cancellation.Token);
                    WritePhaseReport(
                        options,
                        result,
                        null,
                        cycles,
                        "fail_no_transport_loss");
                    Console.WriteLine("EXPECTED_LOSS_OBSERVED=False");
                    Console.WriteLine("RESULT=FAIL");
                    return 1;
                }
                result = await runtime.StopAsync(cancellation.Token);
            }
            else
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.DurationSeconds),
                    cancellation.Token);
                result = await runtime.StopAsync(cancellation.Token);
            }

            await runtime.DisconnectAsync(cancellation.Token);
            bool passed = options.Mode switch
            {
                AcceptanceMode.Capture =>
                    result.Completed && result.CyclesWritten > 0 &&
                    epoch != Guid.Empty,
                AcceptanceMode.ExpectLoss =>
                    !result.Completed && observedFault is not null &&
                    IsTransportFault(result.FaultCode) &&
                    observedFault.RequiresReconnect,
                _ => false
            };
            string decision = passed ? "pass" : "fail";
            string phaseReport = WritePhaseReport(
                options,
                result,
                observedFault,
                cycles,
                decision);

            Console.WriteLine($"EPOCH={result.EpochId:D}");
            Console.WriteLine($"CYCLES={result.CyclesWritten}");
            Console.WriteLine($"SAMPLES={result.SamplesWritten}");
            Console.WriteLine($"SESSION_STATUS={result.Status.ToUpperInvariant()}");
            Console.WriteLine($"FAULT_CODE={result.FaultCode ?? "NONE"}");
            Console.WriteLine($"CSV={result.CsvPath}");
            Console.WriteLine($"METADATA={result.MetadataPath}");
            Console.WriteLine($"PHASE_REPORT={phaseReport}");
            Console.WriteLine("INTERFACE_RELEASED=True");
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
            Console.CancelKeyPress -= handler;
        }
    }

    private static void PrintPreflight(Ms43LivePreflightReport report)
    {
        Console.WriteLine("BMW-14 PINNED LIVE RUNTIME PREFLIGHT");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine($"PORT={report.Port}");
        Console.WriteLine($"CATALOG_ENTRIES={report.CatalogEntries}");
        Console.WriteLine($"SELECTABLE_PARAMETERS={report.SelectableParameters}");
        Console.WriteLine($"ALLOWLISTED_JOBS={report.AllowlistedJobs}");
        Console.WriteLine("ALL_ARGUMENTS_EMPTY=True");
        Console.WriteLine("UNKNOWN_DISPATCH_POLICY=reject");
        Console.WriteLine($"SGBD_SHA256={report.SgbdSha256}");
        Console.WriteLine($"BACKEND_SHA256={report.BackendSha256}");
        Console.WriteLine("RESULT=PASS");
    }

    private static bool IsTransportFault(string? code) => code is
        "EDIABAS_TIMEOUT" or
        "EDIABAS_PORT_LOST" or
        "EDIABAS_TRANSPORT_ERROR";

    private static string WritePhaseReport(
        AcceptanceOptions options,
        LoggerSessionResult result,
        LoggerRuntimeFault? fault,
        long observedCycles,
        string decision)
    {
        string path = Path.Combine(
            result.RunDirectory,
            "bmw14.phase.acceptance.json");
        var report = new
        {
            schema_version = "bmw14.live_phase_acceptance.v1",
            generated_utc = DateTimeOffset.UtcNow,
            phase = options.Action,
            mode = options.Mode.ToString().ToLowerInvariant(),
            dispatch_policy = "pinned_status_empty_argument_only",
            maximum_transport_attempts = LoggerRecoveryPolicy.MaximumTransportAttempts,
            epoch_id = result.EpochId,
            observed_cycles = observedCycles,
            session_status = result.Status,
            fault_code = result.FaultCode,
            requires_reconnect = fault?.RequiresReconnect ?? false,
            interface_released = true,
            csv = Path.GetFileName(result.CsvPath),
            metadata = Path.GetFileName(result.MetadataPath),
            result = decision
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        return Path.GetFullPath(path);
    }
}

internal enum AcceptanceMode
{
    Preflight,
    Capture,
    ExpectLoss
}

internal sealed record AcceptanceOptions(
    AcceptanceMode Mode,
    string Action,
    int DurationSeconds,
    double RequestedHz,
    string Selection,
    string OutputDirectory,
    string EcuPath,
    string Port,
    string CatalogPath,
    string PackPath)
{
    public static AcceptanceOptions Parse(IReadOnlyList<string> args)
    {
        bool preflight = false;
        bool capture = false;
        bool expectLoss = false;
        bool confirmed = false;
        string action = "baseline";
        int duration = 15;
        double hz = 5.0;
        string selection = "default4";
        string output = Path.Combine("data", "private", "bmw14-live-acceptance");
        string ecuPath = @"C:\EDIABAS\ECU";
        string port = "COM5";
        string catalog = Path.Combine(
            "config",
            "ms430ds0-parameter-catalog.draft.json");
        string pack = Path.Combine(
            "config",
            "ms430ds0-bounded-live-test-pack.json");

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--preflight":
                    preflight = true;
                    break;
                case "--capture":
                    capture = true;
                    break;
                case "--expect-loss":
                    expectLoss = true;
                    break;
                case "--confirm-read-only":
                    confirmed = true;
                    break;
                case "--action" when index + 1 < args.Count:
                    action = args[++index].ToLowerInvariant();
                    break;
                case "--duration-seconds" when index + 1 < args.Count:
                    duration = int.Parse(args[++index]);
                    break;
                case "--requested-hz" when index + 1 < args.Count:
                    hz = double.Parse(
                        args[++index],
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--selection" when index + 1 < args.Count:
                    selection = args[++index].ToLowerInvariant();
                    break;
                case "--output-dir" when index + 1 < args.Count:
                    output = args[++index];
                    break;
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = args[++index].ToUpperInvariant();
                    break;
                case "--catalog" when index + 1 < args.Count:
                    catalog = args[++index];
                    break;
                case "--pack" when index + 1 < args.Count:
                    pack = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        int modes = (preflight ? 1 : 0) +
                    (capture ? 1 : 0) +
                    (expectLoss ? 1 : 0);
        if (modes != 1)
        {
            throw new ArgumentException(
                "Choose exactly one: --preflight, --capture or --expect-loss.");
        }
        AcceptanceMode mode = preflight
            ? AcceptanceMode.Preflight
            : capture
                ? AcceptanceMode.Capture
                : AcceptanceMode.ExpectLoss;
        if (mode != AcceptanceMode.Preflight && !confirmed)
        {
            throw new InvalidOperationException(
                "Live acceptance requires --confirm-read-only.");
        }
        if (mode == AcceptanceMode.Preflight && confirmed)
        {
            throw new ArgumentException(
                "Preflight must not receive --confirm-read-only.");
        }
        if (duration is < 5 or > 1200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration is bounded to 5-1200 seconds.");
        }
        if (!double.IsFinite(hz) || hz is < 0.2 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(nameof(hz));
        }
        if (selection is not ("default4" or "approved22"))
        {
            throw new ArgumentException("Selection must be default4 or approved22.");
        }
        if (!string.Equals(port, "COM5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only COM5 is permitted.");
        }
        if (mode == AcceptanceMode.ExpectLoss &&
            action is not ("cable_disconnect" or "ignition_off"))
        {
            throw new ArgumentException(
                "Expected-loss action must be cable_disconnect or ignition_off.");
        }

        return new AcceptanceOptions(
            mode,
            action,
            duration,
            hz,
            selection,
            Path.GetFullPath(output),
            Path.GetFullPath(ecuPath),
            port,
            Path.GetFullPath(catalog),
            Path.GetFullPath(pack));
    }
}
