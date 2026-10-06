using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdiabasLib;

namespace BmwMs43Logger.BoundedLiveProbe;

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
            ProbeOptions options = ProbeOptions.Parse(args);
            LiveTestPack pack = BoundedProfile.LoadAndValidatePack(options.PackPath);
            BoundedProfile.RunGuardSelfTests(pack);
            BoundedProfile.ValidateRuntime(pack, options.EcuPath, options.Port);

            if (options.Mode == ProbeMode.Preflight)
            {
                PrintPreflight(options, pack);
                return 0;
            }
            if (options.Mode == ProbeMode.DryRun)
            {
                return RunDry(options, pack);
            }

            return RunLive(options, pack, cancellation.Token);
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

    private static void PrintPreflight(ProbeOptions options, LiveTestPack pack)
    {
        Console.WriteLine("BMW-03/04 BOUNDED LIVE-DATA PREFLIGHT");
        Console.WriteLine("PROFILE_STATE=CONTROLLED_TEST_ONLY");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine("GUARD_TESTS=PASS");
        Console.WriteLine($"PORT={options.Port}");
        Console.WriteLine($"PACK={options.PackPath}");
        Console.WriteLine($"PACK_SEMANTIC_SHA256={BoundedProfile.ExpectedPackSha256}");
        Console.WriteLine($"PACK_JOBS={pack.Entries.Count}");
        Console.WriteLine("GROUPS=baseline(1);core(1);states(1);extended(10)");
        Console.WriteLine("PASSES_RANGE=1-3");
        Console.WriteLine("ALL_ARGUMENTS_EMPTY=True");
        Console.WriteLine("UNKNOWN_DISPATCH_POLICY=reject");
        Console.WriteLine($"SGBD_SHA256={pack.SgbdSha256}");
        Console.WriteLine($"BACKEND_SHA256={pack.BackendSha256}");
        foreach (PackEntry entry in pack.Entries)
        {
            Console.WriteLine(
                $"ALLOW={entry.Job}|ARG=<EMPTY>|RESULTS={entry.ResultNames.Count}|TIER={entry.Tier}");
        }
        Console.WriteLine("RESULT=PASS");
    }

    private static int RunLive(
        ProbeOptions options,
        LiveTestPack pack,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PackEntry> entries = BoundedProfile.GetGroupEntries(
            pack,
            options.Group!);
        var observations = new List<JobObservation>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Exception? failure = null;
        EdiabasNet? ediabas = null;

        try
        {
            ediabas = CreateBackend(options, cancellationToken);
            for (int pass = 1; pass <= options.Passes && failure is null; pass++)
            {
                foreach (PackEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BoundedProfile.EnsureExact(pack, entry);
                    Console.WriteLine($"RUNNING=pass:{pass};job:{entry.Job}");
                    (JobObservation observation, Exception? error) = ExecuteOne(
                        ediabas,
                        entry,
                        pass);
                    observations.Add(observation);
                    if (error is not null)
                    {
                        failure = error;
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            ediabas?.Dispose();
        }

        DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
        string outputPath = WriteReport(
            options,
            pack,
            startedAt,
            finishedAt,
            observations,
            failure,
            communicationAttempted: true);

        int passedJobs = observations.Count(item => item.Status == "PASS");
        int failedJobs = observations.Count(item => item.Status == "FAIL");
        Console.WriteLine("BMW-03/04 BOUNDED LIVE-DATA PROBE");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=True");
        Console.WriteLine($"SCENARIO={options.Scenario}");
        Console.WriteLine($"GROUP={options.Group}");
        Console.WriteLine($"PASSES_REQUESTED={options.Passes}");
        Console.WriteLine($"JOBS_ATTEMPTED={observations.Count}");
        Console.WriteLine($"JOBS_PASSED={passedJobs}");
        Console.WriteLine($"JOBS_FAILED={failedJobs}");
        Console.WriteLine($"REPORT={outputPath}");
        Console.WriteLine("COM_CLOSED=True");

        if (failure is not null)
        {
            Console.Error.WriteLine(
                $"LIVE_ERROR={failure.GetType().Name}: {failure.Message}");
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }

        Console.WriteLine("RESULT=PASS");
        return 0;
    }

    private static int RunDry(ProbeOptions options, LiveTestPack pack)
    {
        IReadOnlyList<PackEntry> entries = BoundedProfile.GetGroupEntries(
            pack,
            options.Group!);
        var observations = new List<JobObservation>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        for (int pass = 1; pass <= options.Passes; pass++)
        {
            foreach (PackEntry entry in entries)
            {
                BoundedProfile.EnsureExact(pack, entry);
                DateTimeOffset jobStartedAt = DateTimeOffset.UtcNow;
                IReadOnlyList<CapturedResult> results = entry.ResultNames
                    .Select(CreateSyntheticResult)
                    .ToArray();
                observations.Add(new JobObservation(
                    pass,
                    entry.Job,
                    jobStartedAt,
                    DateTimeOffset.UtcNow,
                    0.0,
                    "PASS",
                    null,
                    results));
            }
        }

        string outputPath = WriteReport(
            options,
            pack,
            startedAt,
            DateTimeOffset.UtcNow,
            observations,
            null,
            communicationAttempted: false);
        Console.WriteLine("BMW-03/04 BOUNDED LIVE-DATA DRY RUN");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine($"GROUP={options.Group}");
        Console.WriteLine($"PASSES={options.Passes}");
        Console.WriteLine($"JOBS_SIMULATED={observations.Count}");
        Console.WriteLine($"REPORT={outputPath}");
        Console.WriteLine("RESULT=PASS");
        return 0;
    }

    private static CapturedResult CreateSyntheticResult(string name)
    {
        if (name == "JOB_STATUS")
        {
            return new CapturedResult(name, true, "TypeS", "String", "OKAY");
        }
        if (name.EndsWith("_EINH", StringComparison.Ordinal))
        {
            return new CapturedResult(name, true, "TypeS", "String", "SIMULATED_UNIT");
        }
        return new CapturedResult(name, true, "TypeR", "Double", 0.0);
    }

    private static EdiabasNet CreateBackend(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        string config = string.Join(
            ';',
            $"EcuPath={options.EcuPath}",
            $"ObdComPort={options.Port}",
            "Interface=STD:OBD",
            "ApiTrace=0",
            "IfhTrace=0",
            "TraceBuffering=0",
            "IfhTraceBuffering=0",
            "RetryComm=1",
            "SystemResults=1");

        var obd = new EdInterfaceObd
        {
            ComPort = options.Port,
            IfhName = "STD:OBD",
            ApplicationName = "BmwMs43Logger.BoundedLiveProbe"
        };
        var ediabas = new EdiabasNet(config, isHosted: true)
        {
            EdInterfaceClass = obd,
            SgbdFileName = BoundedProfile.SgbdFileName
        };
        ediabas.AbortJobFunc = () => cancellationToken.IsCancellationRequested;
        return ediabas;
    }

    private static (JobObservation Observation, Exception? Error) ExecuteOne(
        EdiabasNet ediabas,
        PackEntry entry,
        int pass)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        try
        {
            ediabas.ArgString = entry.Argument;
            ediabas.ArgBinary = null;
            ediabas.ArgStringStd = string.Empty;
            ediabas.ArgBinaryStd = null;
            ediabas.ResultsRequests = string.Join(';', entry.ResultNames);
            ediabas.ExecuteJob(entry.Job);

            IReadOnlyList<CapturedResult> results = CaptureResults(
                ediabas.ResultSets,
                entry.ResultNames);
            bool jobOkay = results.Any(result =>
                result.Name == "JOB_STATUS" &&
                result.Present &&
                string.Equals(
                    result.Value?.ToString(),
                    "OKAY",
                    StringComparison.OrdinalIgnoreCase));
            if (!jobOkay)
            {
                throw new InvalidOperationException(
                    $"{entry.Job} did not return JOB_STATUS=OKAY.");
            }

            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            return (
                new JobObservation(
                    pass,
                    entry.Job,
                    startedAt,
                    finishedAt,
                    Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                    "PASS",
                    null,
                    results),
                null);
        }
        catch (Exception exception)
        {
            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            string detail = EdiabasNet.GetExceptionText(
                exception,
                includeTypes: true,
                includeStack: false);
            var wrapped = new InvalidOperationException(
                $"{entry.Job} failed ({ediabas.ErrorCodeLast}): {detail}",
                exception);
            return (
                new JobObservation(
                    pass,
                    entry.Job,
                    startedAt,
                    finishedAt,
                    Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                    "FAIL",
                    wrapped.Message,
                    []),
                wrapped);
        }
    }

    private static IReadOnlyList<CapturedResult> CaptureResults(
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> resultSets,
        IReadOnlyList<string> requestedNames)
    {
        List<Dictionary<string, EdiabasNet.ResultData>> dataSets = resultSets
            .Skip(1)
            .ToList();
        var captured = new List<CapturedResult>(requestedNames.Count);

        foreach (string name in requestedNames)
        {
            EdiabasNet.ResultData? found = null;
            for (int index = dataSets.Count - 1; index >= 0; index--)
            {
                if (dataSets[index].TryGetValue(
                        name,
                        out EdiabasNet.ResultData? candidate))
                {
                    found = candidate;
                    break;
                }
            }

            if (found is null)
            {
                captured.Add(new CapturedResult(
                    name,
                    false,
                    null,
                    null,
                    null));
                continue;
            }

            object? value = NormalizeValue(found.OpData);
            captured.Add(new CapturedResult(
                name,
                true,
                found.ResType.ToString(),
                value?.GetType().Name,
                value));
        }

        return captured;
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        string or bool or byte or sbyte or short or ushort or int or uint or
            long or ulong or float or double or decimal => value,
        IFormattable formattable =>
            formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string WriteReport(
        ProbeOptions options,
        LiveTestPack pack,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        IReadOnlyList<JobObservation> observations,
        Exception? failure,
        bool communicationAttempted)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string timestamp = startedAt.UtcDateTime.ToString(
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture);
        string fileName = $"bmw03_{options.Scenario}_{options.Group}_{timestamp}.json";
        string path = Path.Combine(options.OutputDirectory, fileName);
        var report = new ProbeReport(
            "bmw03.bounded_live_result.v1",
            options.Scenario!,
            options.Group!,
            options.Passes,
            startedAt,
            finishedAt,
            communicationAttempted,
            true,
            options.Port,
            pack.SgbdSha256,
            pack.BackendSha256,
            failure is null ? "PASS" : "FAIL",
            failure?.Message,
            observations);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
        return Path.GetFullPath(path);
    }
}

internal enum ProbeMode
{
    Preflight,
    DryRun,
    Live
}

internal sealed record ProbeOptions(
    ProbeMode Mode,
    string? Group,
    string? Scenario,
    int Passes,
    string OutputDirectory,
    string EcuPath,
    string Port,
    string PackPath)
{
    private static readonly string[] ValidGroups =
        ["baseline", "core", "states", "extended"];
    private static readonly string[] ValidScenarios = ["engine-off", "idle"];

    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        bool preflight = false;
        bool dryRun = false;
        bool live = false;
        bool confirmed = false;
        string? group = null;
        string? scenario = null;
        int passes = 1;
        string outputDirectory = Path.Combine("data", "private", "bmw03-live");
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
                case "--group" when index + 1 < args.Count:
                    group = args[++index].ToLowerInvariant();
                    break;
                case "--scenario" when index + 1 < args.Count:
                    scenario = args[++index].ToLowerInvariant();
                    break;
                case "--passes" when index + 1 < args.Count:
                    passes = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--output-dir" when index + 1 < args.Count:
                    outputDirectory = args[++index];
                    break;
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        int selectedModes = (preflight ? 1 : 0) +
                            (dryRun ? 1 : 0) +
                            (live ? 1 : 0);
        if (selectedModes != 1)
        {
            throw new ArgumentException(
                "Specify exactly one mode: --preflight, --dry-run or --live.");
        }
        if (passes is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passes),
                "Passes are bounded to 1-3.");
        }
        if (live && !confirmed)
        {
            throw new InvalidOperationException(
                "Live mode requires --confirm-read-only.");
        }
        if (live && (group is null || !ValidGroups.Contains(group)))
        {
            throw new ArgumentException(
                "Live mode requires --group baseline|core|states|extended.");
        }
        if (live && (scenario is null || !ValidScenarios.Contains(scenario)))
        {
            throw new ArgumentException(
                "Live mode requires --scenario engine-off|idle.");
        }
        if (preflight && (group is not null || scenario is not null))
        {
            throw new ArgumentException(
                "Preflight does not accept --group or --scenario.");
        }
        if (dryRun && (group is null || !ValidGroups.Contains(group)))
        {
            throw new ArgumentException(
                "Dry run requires --group baseline|core|states|extended.");
        }
        if (dryRun && scenario is not null)
        {
            throw new ArgumentException("Dry run does not accept --scenario.");
        }

        return new ProbeOptions(
            live ? ProbeMode.Live : dryRun ? ProbeMode.DryRun : ProbeMode.Preflight,
            group,
            dryRun ? "simulated" : scenario,
            passes,
            Path.GetFullPath(outputDirectory),
            Path.GetFullPath(ecuPath),
            port.ToUpperInvariant(),
            Path.GetFullPath(Path.Combine(
                "config",
                "ms430ds0-bounded-live-test-pack.json")));
    }
}

internal static class BoundedProfile
{
    public const string SgbdFileName = "ms430ds0.prg";
    public const string ExpectedSgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    public const string ExpectedBackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    public const string ExpectedPackSha256 =
        "AB953E495EEBB294C17E1C6735D5B173C70E28BC4F8A19B19D15323BF97B8D55";

    private static readonly string[] ExpectedJobs =
    [
        "STATUS_MOTORDREHZAHL",
        "STATUS_MESSWERTEBLOCK",
        "STATUS_DIGITAL",
        "STATUS_LAMBDA_ADD_1",
        "STATUS_LAMBDA_ADD_2",
        "STATUS_LAMBDA_MUL_1",
        "STATUS_LAMBDA_MUL_2",
        "STATUS_VANOS_NW_LAGE_IST_SOLL_REF_EINLASS",
        "STATUS_VANOS_NW_LAGE_IST_SOLL_REF_AUSLASS",
        "STATUS_LS_VKAT_SIGNAL_1",
        "STATUS_LS_VKAT_SIGNAL_2",
        "STATUS_LS_NKAT_SIGNAL_1",
        "STATUS_LS_NKAT_SIGNAL_2"
    ];

    public static LiveTestPack LoadAndValidatePack(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Bounded live-test pack not found.", path);
        }
        string text = File.ReadAllText(path, Encoding.UTF8);
        string actualHash = GetNormalizedTextSha256(text);
        if (!string.Equals(
                actualHash,
                ExpectedPackSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bounded pack hash mismatch. Expected {ExpectedPackSha256}, actual {actualHash}.");
        }

        LiveTestPack? pack = JsonSerializer.Deserialize<LiveTestPack>(text);
        if (pack is null)
        {
            throw new InvalidOperationException("Bounded pack is empty.");
        }
        ValidatePack(pack);
        return pack;
    }

    public static void ValidateRuntime(
        LiveTestPack pack,
        string ecuPath,
        string port)
    {
        ValidatePack(pack);
        if (!string.Equals(port, "COM5", StringComparison.Ordinal) ||
            !string.Equals(port, pack.Port, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This controlled profile permits COM5 only, not {port}.");
        }
        if (ecuPath.Contains(';'))
        {
            throw new InvalidOperationException(
                "The ECU directory must not contain a semicolon.");
        }

        RequireHash(
            Path.Combine(ecuPath, SgbdFileName),
            ExpectedSgbdSha256,
            "SGBD");
        RequireHash(
            typeof(EdiabasNet).Assembly.Location,
            ExpectedBackendSha256,
            "EdiabasLib runtime assembly");
    }

    public static IReadOnlyList<PackEntry> GetGroupEntries(
        LiveTestPack pack,
        string group)
    {
        string[] jobs = group switch
        {
            "baseline" => [ExpectedJobs[0]],
            "core" => [ExpectedJobs[1]],
            "states" => [ExpectedJobs[2]],
            "extended" => ExpectedJobs.Skip(3).ToArray(),
            _ => throw new InvalidOperationException($"Unknown group: {group}")
        };
        return jobs.Select(job => Resolve(pack, job)).ToArray();
    }

    public static void EnsureExact(LiveTestPack pack, PackEntry candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        PackEntry expected = Resolve(pack, candidate.Job);
        bool exact = candidate.Argument == expected.Argument &&
                     candidate.Tier == expected.Tier &&
                     candidate.ReviewState == expected.ReviewState &&
                     candidate.ResultNames.SequenceEqual(
                         expected.ResultNames,
                         StringComparer.Ordinal);
        if (!exact)
        {
            throw new InvalidOperationException(
                "Dispatch tuple is not exactly allowlisted.");
        }
    }

    public static void RunGuardSelfTests(LiveTestPack pack)
    {
        PackEntry approved = Resolve(pack, ExpectedJobs[0]);
        EnsureExact(pack, approved);
        ExpectRejected(() => EnsureExact(
            pack,
            approved with { Job = "STEUERN_LL_DREHZAHL_VERSTELLEN" }));
        ExpectRejected(() => EnsureExact(pack, approved with { Argument = "750" }));
        ExpectRejected(() => EnsureExact(
            pack,
            approved with { ResultNames = ["JOB_STATUS"] }));
        ExpectRejected(() => Resolve(pack, "STATUS_NOT_REVIEWED"));

        foreach (PackEntry entry in pack.Entries)
        {
            EnsureExact(pack, entry);
        }
    }

    private static void ValidatePack(LiveTestPack pack)
    {
        RequireEqual(pack.SchemaVersion, "bmw03.bounded_live_test_pack.v1", "schema");
        RequireEqual(pack.ProfileState, "controlled_test_only", "profile state");
        RequireEqual(pack.SgbdFile, SgbdFileName, "SGBD file");
        RequireEqual(pack.SgbdSha256, ExpectedSgbdSha256, "SGBD hash");
        RequireEqual(pack.BackendSha256, ExpectedBackendSha256, "backend hash");
        RequireEqual(pack.Port, "COM5", "port");
        RequireEqual(pack.ArgumentPolicy, "exact_empty_string_only", "argument policy");
        RequireEqual(pack.UnknownDispatchPolicy, "reject", "unknown dispatch policy");
        if (pack.Entries.Count != ExpectedJobs.Length)
        {
            throw new InvalidOperationException(
                $"Expected {ExpectedJobs.Length} pack entries, found {pack.Entries.Count}.");
        }

        for (int index = 0; index < ExpectedJobs.Length; index++)
        {
            PackEntry entry = pack.Entries[index];
            RequireEqual(entry.Job, ExpectedJobs[index], $"job {index}");
            RequireEqual(entry.Argument, string.Empty, $"argument for {entry.Job}");
            if (entry.ResultNames.Count < 2 || entry.ResultNames[0] != "JOB_STATUS")
            {
                throw new InvalidOperationException(
                    $"Invalid result allowlist for {entry.Job}.");
            }
            if (entry.ResultNames.Distinct(StringComparer.Ordinal).Count() !=
                entry.ResultNames.Count)
            {
                throw new InvalidOperationException(
                    $"Duplicate result name in {entry.Job}.");
            }
            if (entry.Job.StartsWith("STEUERN_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("START_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("STOP_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("TEST_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"State-changing job entered the bounded pack: {entry.Job}");
            }
        }
    }

    private static PackEntry Resolve(LiveTestPack pack, string job)
    {
        PackEntry? entry = pack.Entries.FirstOrDefault(item =>
            string.Equals(item.Job, job, StringComparison.Ordinal));
        return entry ?? throw new InvalidOperationException(
            $"Job is not allowlisted: {job}");
    }

    private static string GetNormalizedTextSha256(string text)
    {
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void RequireHash(
        string path,
        string expected,
        string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found.", path);
        }
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        RequireEqual(actual, expected, $"{description} SHA256");
    }

    private static void RequireEqual(string actual, string expected, string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} mismatch. Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void ExpectRejected(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Guard accepted a prohibited dispatch tuple.");
    }
}

internal sealed record LiveTestPack(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("profile_state")] string ProfileState,
    [property: JsonPropertyName("sgbd_file")] string SgbdFile,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_file")] string BackendFile,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("port")] string Port,
    [property: JsonPropertyName("argument_policy")] string ArgumentPolicy,
    [property: JsonPropertyName("unknown_dispatch_policy")] string UnknownDispatchPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<PackEntry> Entries);

internal sealed record PackEntry(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("result_names")] IReadOnlyList<string> ResultNames,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("tier")] int Tier);

internal sealed record CapturedResult(
    string Name,
    bool Present,
    string? ResultType,
    string? ValueKind,
    object? Value);

internal sealed record JobObservation(
    int Pass,
    string Job,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    double DurationMs,
    string Status,
    string? Error,
    IReadOnlyList<CapturedResult> Results);

internal sealed record ProbeReport(
    string SchemaVersion,
    string Scenario,
    string Group,
    int PassesRequested,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    bool CommunicationAttempted,
    bool ComClosed,
    string Port,
    string SgbdSha256,
    string BackendSha256,
    string Result,
    string? Error,
    IReadOnlyList<JobObservation> Observations);
