using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.PollingPlannerProbe;

internal static class Program
{
    private const string ExpectedSgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string ExpectedBackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    private const string ExpectedPackSha256 =
        "AB953E495EEBB294C17E1C6735D5B173C70E28BC4F8A19B19D15323BF97B8D55";

    public static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            CatalogDocument catalog = LoadCatalog(options.CatalogPath);
            PackDocument pack = LoadPack(options.PackPath);
            ValidateDocuments(catalog, pack);

            IReadOnlyList<PollingParameterDefinition> definitions = catalog.Entries
                .Select(ToDefinition)
                .ToArray();
            IReadOnlyList<ReviewedJobDefinition> reviewedJobs = pack.Entries
                .Select(entry => new ReviewedJobDefinition(
                    entry.Job,
                    entry.Argument,
                    entry.ResultNames))
                .ToArray();

            RunSelfTests(definitions, reviewedJobs);
            IReadOnlyList<ScenarioResult> scenarios = RunScenarios(
                definitions,
                reviewedJobs);
            WriteReport(options, catalog, scenarios);
            PrintSummary(options, scenarios);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static CatalogDocument LoadCatalog(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<CatalogDocument>(text) ??
               throw new InvalidOperationException("Catalog is empty.");
    }

    private static PackDocument LoadPack(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        string hash = NormalizedTextSha256(text);
        if (!string.Equals(hash, ExpectedPackSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bounded pack hash mismatch. Expected {ExpectedPackSha256}, actual {hash}.");
        }
        return JsonSerializer.Deserialize<PackDocument>(text) ??
               throw new InvalidOperationException("Bounded pack is empty.");
    }

    private static void ValidateDocuments(
        CatalogDocument catalog,
        PackDocument pack)
    {
        RequireEqual(catalog.SchemaVersion, "bmw04.parameter_catalog.v1", "catalog schema");
        RequireEqual(catalog.SgbdSha256, ExpectedSgbdSha256, "catalog SGBD");
        RequireEqual(catalog.BackendSha256, ExpectedBackendSha256, "catalog backend");
        RequireEqual(catalog.UnknownParameterPolicy, "reject", "unknown parameter policy");
        if (catalog.Entries.Count != 28)
        {
            throw new InvalidOperationException(
                $"Expected 28 catalog entries, found {catalog.Entries.Count}.");
        }

        RequireEqual(pack.SchemaVersion, "bmw03.bounded_live_test_pack.v1", "pack schema");
        RequireEqual(pack.SgbdSha256, ExpectedSgbdSha256, "pack SGBD");
        RequireEqual(pack.BackendSha256, ExpectedBackendSha256, "pack backend");
        RequireEqual(pack.ArgumentPolicy, "exact_empty_string_only", "argument policy");
        RequireEqual(pack.UnknownDispatchPolicy, "reject", "unknown dispatch policy");
        if (pack.Entries.Count != 13 || pack.Entries.Any(entry => entry.Argument != ""))
        {
            throw new InvalidOperationException("Unexpected bounded job pack.");
        }
    }

    private static PollingParameterDefinition ToDefinition(CatalogEntry entry)
    {
        return new PollingParameterDefinition(
            new ParameterId(entry.ParameterId),
            entry.Source.Job,
            entry.Source.Argument,
            entry.Source.ValueResult,
            entry.Source.UnitResult,
            entry.SupportStatus);
    }

    private static void RunSelfTests(
        IReadOnlyList<PollingParameterDefinition> definitions,
        IReadOnlyList<ReviewedJobDefinition> reviewedJobs)
    {
        ParameterId rpm = definitions[0].ParameterId;
        PollingPlan approved = PollingPlanner.Build(
            definitions,
            reviewedJobs,
            [rpm],
            PollingPlanMode.RuntimeApprovedOnly);
        Require(approved.Jobs.Count == 1, "RPM plan must contain one job.");
        Require(approved.Routes.Count == 1, "RPM plan must contain one route.");
        Require(
            approved.ExecutionPolicy == PollingPlanner.SerialExecutionPolicy,
            "Planner must require one serial worker.");

        PollingPlan duplicate = PollingPlanner.Build(
            definitions,
            reviewedJobs,
            [rpm, rpm],
            PollingPlanMode.RuntimeApprovedOnly);
        Require(duplicate.Routes.Count == 1, "Duplicate selection was not removed.");

        ExpectRejected(() => PollingPlanner.Build(
            definitions,
            reviewedJobs,
            [definitions[1].ParameterId],
            PollingPlanMode.RuntimeApprovedOnly));
        ExpectRejected(() => PollingPlanner.Build(
            definitions,
            reviewedJobs,
            [new ParameterId("unknown.parameter")],
            PollingPlanMode.ControlledTest));

        PollingPlan all = PollingPlanner.Build(
            definitions,
            reviewedJobs,
            definitions.Select(item => item.ParameterId).ToArray(),
            PollingPlanMode.ControlledTest);
        Require(all.Routes.Count == 28, "All-channel plan must contain 28 routes.");
        Require(all.Jobs.Count == 2, "All-channel plan must collapse to two jobs.");
        Require(
            all.Jobs.Select(job => (job.Job, job.Argument)).Distinct().Count() == 2,
            "Job tuples were not deduplicated.");

        ReviewedJobDefinition measurementBlock = reviewedJobs.Single(job =>
            job.Job == "STATUS_MESSWERTEBLOCK");
        var brokenJobs = reviewedJobs
            .Where(job => job.Job != measurementBlock.Job)
            .Append(measurementBlock with
            {
                ResultNames = measurementBlock.ResultNames
                    .Where(name => name != definitions[1].UnitResult)
                    .ToArray()
            })
            .ToArray();
        ExpectRejected(() => PollingPlanner.Build(
            definitions,
            brokenJobs,
            [definitions[1].ParameterId],
            PollingPlanMode.ControlledTest));
    }

    private static IReadOnlyList<ScenarioResult> RunScenarios(
        IReadOnlyList<PollingParameterDefinition> definitions,
        IReadOnlyList<ReviewedJobDefinition> reviewedJobs)
    {
        var scenarios = new[]
        {
            new Scenario("rpm_only", definitions.Take(1).ToArray(), PollingPlanMode.RuntimeApprovedOnly),
            new Scenario("core4", definitions.Take(4).ToArray(), PollingPlanMode.ControlledTest),
            new Scenario("batch8", definitions.Skip(1).Take(8).ToArray(), PollingPlanMode.ControlledTest),
            new Scenario("core16", definitions.Take(16).ToArray(), PollingPlanMode.ControlledTest),
            new Scenario("all28", definitions.ToArray(), PollingPlanMode.ControlledTest)
        };

        return scenarios.Select(scenario =>
        {
            PollingPlan plan = PollingPlanner.Build(
                definitions,
                reviewedJobs,
                scenario.Parameters.Select(item => item.ParameterId).ToArray(),
                scenario.Mode);
            const int cycles = 100;
            double[] cycleTimes = Enumerable.Range(0, cycles)
                .Select(cycle => VirtualCycleMilliseconds(plan, cycle))
                .Order()
                .ToArray();
            double median = Percentile(cycleTimes, 0.50);
            double p95 = Percentile(cycleTimes, 0.95);
            return new ScenarioResult(
                scenario.Name,
                scenario.Mode.ToString(),
                plan.SelectedParameterCount,
                plan.Jobs.Count,
                plan.Jobs.Sum(job => job.ResultNames.Count),
                cycles,
                median,
                p95,
                1000.0 / median,
                1,
                plan.Jobs.Select(job => new ScenarioJob(
                    job.Sequence,
                    job.Job,
                    job.Argument,
                    job.ResultNames.Count,
                    job.ParameterIds.Count)).ToArray());
        }).ToArray();
    }

    private static double VirtualCycleMilliseconds(PollingPlan plan, int cycle)
    {
        return plan.Jobs.Sum(job =>
        {
            double baseMilliseconds = job.Job == "STATUS_MOTORDREHZAHL" ? 190.0 : 205.0;
            double resultCost = job.ResultNames.Count * 0.25;
            int stableCode = job.Job.Aggregate(0, (sum, character) => sum + character);
            double deterministicJitter = ((stableCode + cycle * 17) % 21 - 10) * 0.4;
            return baseMilliseconds + resultCost + deterministicJitter;
        });
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static void WriteReport(
        Options options,
        CatalogDocument catalog,
        IReadOnlyList<ScenarioResult> scenarios)
    {
        string? directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var report = new BenchmarkReport(
            "bmw05.offline_planner_benchmark.v1",
            "deterministic_virtual_cost_model_not_wall_clock",
            DateTimeOffset.UtcNow,
            false,
            "none",
            PollingPlanner.SerialExecutionPolicy,
            catalog.CatalogVersion,
            catalog.SgbdSha256,
            catalog.BackendSha256,
            scenarios);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        File.WriteAllText(
            options.OutputPath,
            JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void PrintSummary(
        Options options,
        IReadOnlyList<ScenarioResult> scenarios)
    {
        Console.WriteLine("BMW-05 OFFLINE POLLING PLANNER DRY RUN");
        Console.WriteLine("BENCHMARK_KIND=SIMULATED_VIRTUAL_COST_MODEL");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine("PLANNER_SELF_TESTS=PASS");
        Console.WriteLine($"EXECUTION_POLICY={PollingPlanner.SerialExecutionPolicy}");
        foreach (ScenarioResult scenario in scenarios)
        {
            Console.WriteLine(
                $"SCENARIO={scenario.Name}|CHANNELS={scenario.Channels}|" +
                $"JOBS={scenario.JobsPerCycle}|RESULT_REQUESTS={scenario.ResultRequestsPerCycle}|" +
                $"VIRTUAL_MEDIAN_MS={scenario.VirtualMedianMs.ToString("F2", CultureInfo.InvariantCulture)}|" +
                $"VIRTUAL_HZ={scenario.VirtualHz.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        Console.WriteLine($"REPORT={options.OutputPath}");
        Console.WriteLine("REAL_CAR_BENCHMARK_REQUIRED=True");
        Console.WriteLine("RESULT=PASS");
    }

    private static string NormalizedTextSha256(string text)
    {
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void RequireEqual(string actual, string expected, string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} mismatch. Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
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
        throw new InvalidOperationException("Negative planner test was accepted.");
    }
}

internal sealed record Options(string CatalogPath, string PackPath, string OutputPath)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        bool dryRun = false;
        string output = Path.Combine(
            "artifacts",
            "bmw05",
            "bmw05-offline-planner-benchmark.json");
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--output" when index + 1 < args.Count:
                    output = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }
        if (!dryRun)
        {
            throw new ArgumentException("Specify --dry-run.");
        }
        return new Options(
            Path.GetFullPath(Path.Combine(
                "config",
                "ms430ds0-parameter-catalog.draft.json")),
            Path.GetFullPath(Path.Combine(
                "config",
                "ms430ds0-bounded-live-test-pack.json")),
            Path.GetFullPath(output));
    }
}

internal sealed record CatalogDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("catalog_version")] string CatalogVersion,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("unknown_parameter_policy")] string UnknownParameterPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<CatalogEntry> Entries);

internal sealed record CatalogEntry(
    [property: JsonPropertyName("parameter_id")] string ParameterId,
    [property: JsonPropertyName("support_status")] string SupportStatus,
    [property: JsonPropertyName("source")] CatalogSource Source);

internal sealed record CatalogSource(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("value_result")] string ValueResult,
    [property: JsonPropertyName("unit_result")] string UnitResult);

internal sealed record PackDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("argument_policy")] string ArgumentPolicy,
    [property: JsonPropertyName("unknown_dispatch_policy")] string UnknownDispatchPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<PackEntry> Entries);

internal sealed record PackEntry(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("result_names")] IReadOnlyList<string> ResultNames);

internal sealed record Scenario(
    string Name,
    IReadOnlyList<PollingParameterDefinition> Parameters,
    PollingPlanMode Mode);

internal sealed record ScenarioJob(
    int Sequence,
    string Job,
    string Argument,
    int ResultRequests,
    int Parameters);

internal sealed record ScenarioResult(
    string Name,
    string Mode,
    int Channels,
    int JobsPerCycle,
    int ResultRequestsPerCycle,
    int Cycles,
    double VirtualMedianMs,
    double VirtualP95Ms,
    double VirtualHz,
    int MaxConcurrency,
    IReadOnlyList<ScenarioJob> Jobs);

internal sealed record BenchmarkReport(
    string SchemaVersion,
    string BenchmarkKind,
    DateTimeOffset GeneratedUtc,
    bool CommunicationAttempted,
    string InterfaceInstance,
    string ExecutionPolicy,
    string CatalogVersion,
    string SgbdSha256,
    string BackendSha256,
    IReadOnlyList<ScenarioResult> Scenarios);
