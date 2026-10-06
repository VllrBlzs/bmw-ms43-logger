using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.SamplingProbe;

internal static class Program
{
    private static readonly ParameterId Rpm = new("engine.rpm");
    private static readonly ParameterId Speed = new("vehicle.speed");
    private static readonly ParameterId Coolant = new("engine.coolant.temperature");
    private static readonly ParameterId Oil = new("engine.oil.temperature");

    public static int Main(string[] args)
    {
        try
        {
            string outputPath = Parse(args);
            FixtureResult normal = RunNormalFixture();
            FixtureResult faults = RunFaultFixture();
            QueueResult queue = RunQueueFixture();
            WriteReport(outputPath, normal, faults, queue);

            Console.WriteLine("BMW-06 OFFLINE SAMPLING/QUALITY DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine("MULTI_JOB_WINDOWS=PASS");
            Console.WriteLine("SERIAL_NON_OVERLAP=PASS");
            Console.WriteLine("VALID_MISSING_TIMEOUT_STALE=PASS");
            Console.WriteLine("LATENCY_AND_AGE=PASS");
            Console.WriteLine("BOUNDED_QUEUE=PASS");
            Console.WriteLine($"NORMAL_SAMPLES={normal.Samples.Count}");
            Console.WriteLine($"FAULT_SAMPLES={faults.Samples.Count}");
            Console.WriteLine($"QUEUE_DROPPED={queue.Dropped}");
            Console.WriteLine($"REPORT={outputPath}");
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

    private static FixtureResult RunNormalFixture()
    {
        Guid epoch = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const long cycle = 1;
        DateTimeOffset start = DateTimeOffset.Parse("2026-10-06T18:00:00+00:00");
        long tick = 10 * Stopwatch.Frequency;
        long ms = Stopwatch.Frequency / 1000;
        SamplingRoute[] routes =
        [
            new(Rpm, 1, "STATUS_MOTORDREHZAHL"),
            new(Speed, 2, "STATUS_MESSWERTEBLOCK"),
            new(Coolant, 2, "STATUS_MESSWERTEBLOCK"),
            new(Oil, 2, "STATUS_MESSWERTEBLOCK")
        ];
        JobReadBatch[] jobs =
        [
            new(
                epoch,
                cycle,
                1,
                "STATUS_MOTORDREHZAHL",
                start,
                start.AddMilliseconds(200),
                tick,
                tick + 200 * ms,
                JobReadQuality.Complete,
                null,
                [new ParameterSample(
                    Rpm,
                    start.AddMilliseconds(150),
                    tick + 150 * ms,
                    750.0,
                    "1/min",
                    SampleQuality.Valid,
                    null)]),
            new(
                epoch,
                cycle,
                2,
                "STATUS_MESSWERTEBLOCK",
                start.AddMilliseconds(210),
                start.AddMilliseconds(430),
                tick + 210 * ms,
                tick + 430 * ms,
                JobReadQuality.Complete,
                null,
                [
                    new ParameterSample(
                        Speed,
                        start.AddMilliseconds(350),
                        tick + 350 * ms,
                        0.0,
                        "km/h",
                        SampleQuality.Valid,
                        null),
                    new ParameterSample(
                        Coolant,
                        start.AddMilliseconds(370),
                        tick + 370 * ms,
                        82.0,
                        "°C",
                        SampleQuality.Valid,
                        null)
                ])
        ];

        NormalizedCycle result = SamplingEngine.NormalizeCycle(
            epoch,
            cycle,
            routes,
            jobs,
            start.AddMilliseconds(450),
            tick + 450 * ms);
        Require(result.Samples.Count == 4, "Normal fixture sample count mismatch.");
        NormalizedSample rpm = result.Samples.Single(item => item.ParameterId == Rpm);
        NormalizedSample speed = result.Samples.Single(item => item.ParameterId == Speed);
        NormalizedSample oil = result.Samples.Single(item => item.ParameterId == Oil);
        Require(rpm.AgeAtCycleEndMs > speed.AgeAtCycleEndMs,
            "Separate jobs were falsely treated as simultaneous.");
        Require(oil.Quality == SampleQuality.Missing && oil.Value is null,
            "Missing result was not represented safely.");
        return new FixtureResult("normal", result.Samples);
    }

    private static FixtureResult RunFaultFixture()
    {
        Guid epoch = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const long cycle = 2;
        DateTimeOffset start = DateTimeOffset.Parse("2026-10-06T18:01:00+00:00");
        long tick = 20 * Stopwatch.Frequency;
        long ms = Stopwatch.Frequency / 1000;
        SamplingRoute[] routes =
        [
            new(Rpm, 1, "STATUS_MOTORDREHZAHL"),
            new(Speed, 2, "STATUS_MESSWERTEBLOCK"),
            new(Coolant, 2, "STATUS_MESSWERTEBLOCK")
        ];
        JobReadBatch[] jobs =
        [
            new(
                epoch,
                cycle,
                1,
                "STATUS_MOTORDREHZAHL",
                start,
                start.AddMilliseconds(180),
                tick,
                tick + 180 * ms,
                JobReadQuality.Complete,
                null,
                [new ParameterSample(
                    Rpm,
                    start.AddMilliseconds(140),
                    tick + 140 * ms,
                    748.0,
                    "1/min",
                    SampleQuality.Stale,
                    "SIM_STALE")]),
            new(
                epoch,
                cycle,
                2,
                "STATUS_MESSWERTEBLOCK",
                start.AddMilliseconds(190),
                start.AddMilliseconds(390),
                tick + 190 * ms,
                tick + 390 * ms,
                JobReadQuality.Timeout,
                "SIM_TIMEOUT",
                [])
        ];

        NormalizedCycle result = SamplingEngine.NormalizeCycle(
            epoch,
            cycle,
            routes,
            jobs,
            start.AddMilliseconds(410),
            tick + 410 * ms);
        Require(result.Samples.Single(item => item.ParameterId == Rpm).Quality ==
            SampleQuality.Stale, "Stale quality was lost.");
        foreach (NormalizedSample sample in result.Samples.Where(item => item.ParameterId != Rpm))
        {
            Require(sample.Quality == SampleQuality.Timeout && sample.Value is null,
                "Timeout did not fan out safely to the routed parameters.");
        }
        return new FixtureResult("faults", result.Samples);
    }

    private static QueueResult RunQueueFixture()
    {
        var queue = new BoundedDropOldestQueue<string>(3);
        for (int index = 1; index <= 5; index++)
        {
            queue.Enqueue($"cycle-{index}");
        }
        var remaining = new List<string>();
        while (queue.TryDequeue(out string? value))
        {
            remaining.Add(value!);
        }
        Require(queue.DroppedCount == 2, "Queue drop counter mismatch.");
        Require(remaining.SequenceEqual(["cycle-3", "cycle-4", "cycle-5"]),
            "Queue did not drop the oldest items deterministically.");
        return new QueueResult(3, queue.DroppedCount, remaining);
    }

    private static string Parse(IReadOnlyList<string> args)
    {
        bool dryRun = false;
        string output = Path.Combine(
            "artifacts",
            "bmw06",
            "bmw06-sampling-quality-dryrun.json");
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
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }
        if (!dryRun)
        {
            throw new ArgumentException("Specify --dry-run.");
        }
        return Path.GetFullPath(output);
    }

    private static void WriteReport(
        string path,
        FixtureResult normal,
        FixtureResult faults,
        QueueResult queue)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var report = new DryRunReport(
            "bmw06.sampling_quality_dryrun.v1",
            false,
            "none",
            normal,
            faults,
            queue);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record FixtureResult(
    string Name,
    IReadOnlyList<NormalizedSample> Samples);

internal sealed record QueueResult(
    int Capacity,
    long Dropped,
    IReadOnlyList<string> Remaining);

internal sealed record DryRunReport(
    string SchemaVersion,
    bool CommunicationAttempted,
    string InterfaceInstance,
    FixtureResult Normal,
    FixtureResult Faults,
    QueueResult Queue);
