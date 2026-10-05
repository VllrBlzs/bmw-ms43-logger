using System.Diagnostics;
using System.Globalization;
using System.Text;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.Poc;

internal static class Program
{
    private static readonly ParameterId RpmId = new("engine.rpm");

    public static async Task<int> Main(string[] args)
    {
        try
        {
            DryRunOptions options = DryRunOptions.Parse(args);
            ReadOnlyProfileGuard.RunSelfTests();

            await using var source = new FakeRpmAcquisition(options.InjectFaults);
            using var csv = new CsvSampleWriter(options.OutputPath);
            var counts = Enum.GetValues<SampleQuality>()
                .ToDictionary(quality => quality, _ => 0);

            await source.ConnectAsync();
            var elapsed = Stopwatch.StartNew();
            int unsafeNullSubstitutions = 0;

            while (elapsed.Elapsed < options.Duration)
            {
                AcquisitionBatch batch = await source.ReadAsync([RpmId]);
                csv.Write(batch);

                foreach (ParameterSample sample in batch.Samples)
                {
                    counts[sample.Quality]++;
                    if ((sample.Quality is SampleQuality.Missing or
                         SampleQuality.Timeout or SampleQuality.Error) &&
                        sample.Value is not null)
                    {
                        unsafeNullSubstitutions++;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            await source.DisconnectAsync();
            Validate(options, counts, unsafeNullSubstitutions);

            int total = counts.Values.Sum();
            double effectiveHz = total / elapsed.Elapsed.TotalSeconds;
            Console.WriteLine("MODE=FAKE");
            Console.WriteLine($"FAULT_INJECTION={options.InjectFaults}");
            Console.WriteLine("GUARD_TESTS=PASS");
            Console.WriteLine($"DURATION_SECONDS={elapsed.Elapsed.TotalSeconds:F2}");
            Console.WriteLine($"SAMPLES={total}");
            foreach ((SampleQuality quality, int count) in counts)
            {
                Console.WriteLine($"{quality.ToString().ToUpperInvariant()}={count}");
            }
            Console.WriteLine($"EFFECTIVE_HZ={effectiveHz:F2}");
            Console.WriteLine($"CSV={options.OutputPath}");
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

    private static void Validate(
        DryRunOptions options,
        IReadOnlyDictionary<SampleQuality, int> counts,
        int unsafeNullSubstitutions)
    {
        if (unsafeNullSubstitutions != 0)
        {
            throw new InvalidOperationException(
                "Missing/timeout/error samples contained a value.");
        }

        int nonValid = counts
            .Where(pair => pair.Key != SampleQuality.Valid)
            .Sum(pair => pair.Value);

        if (!options.InjectFaults && nonValid != 0)
        {
            throw new InvalidOperationException("Normal fake run produced invalid samples.");
        }

        if (options.InjectFaults &&
            (counts[SampleQuality.Missing] == 0 ||
             counts[SampleQuality.Timeout] == 0 ||
             counts[SampleQuality.Stale] == 0 ||
             counts[SampleQuality.Error] == 0))
        {
            throw new InvalidOperationException(
                "Fault run did not exercise every required quality state.");
        }
    }
}

internal sealed record DryRunOptions(
    TimeSpan Duration,
    string OutputPath,
    bool InjectFaults)
{
    public static DryRunOptions Parse(IReadOnlyList<string> args)
    {
        int durationSeconds = 12;
        string? outputPath = null;
        bool injectFaults = false;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--duration-seconds" when index + 1 < args.Count:
                    durationSeconds = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
                case "--faults":
                    injectFaults = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
            }
        }

        if (durationSeconds is < 10 or > 3600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Duration must be between 10 and 3600 seconds.");
        }

        outputPath ??= Path.Combine(
            "data",
            "private",
            $"bmw02a_dryrun_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");

        return new DryRunOptions(
            TimeSpan.FromSeconds(durationSeconds),
            Path.GetFullPath(outputPath),
            injectFaults);
    }
}

internal sealed record ReviewedDispatch(
    string Job,
    string Argument,
    IReadOnlyList<string> ResultNames,
    string SgbdSha256,
    string BackendSha256);

internal static class ReadOnlyProfileGuard
{
    private const string SgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string BackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";

    private static readonly string[] RpmResults =
    {
        "STAT_MOTORDREHZAHL_WERT",
        "STATUS_MOTORDREHZAHL_WERT",
        "STAT_MOTORDREHZAHL_EINH"
    };

    public static ReviewedDispatch Resolve(ParameterId parameterId)
    {
        ArgumentNullException.ThrowIfNull(parameterId);
        if (parameterId.Value != "engine.rpm")
        {
            throw new InvalidOperationException(
                $"Parameter is not allowlisted: {parameterId.Value}");
        }

        return new ReviewedDispatch(
            "STATUS_MOTORDREHZAHL",
            string.Empty,
            RpmResults,
            SgbdSha256,
            BackendSha256);
    }

    public static void EnsureExact(ReviewedDispatch candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ReviewedDispatch expected = Resolve(new ParameterId("engine.rpm"));

        bool exact = candidate.Job == expected.Job &&
                     candidate.Argument == expected.Argument &&
                     candidate.ResultNames.SequenceEqual(
                         expected.ResultNames,
                         StringComparer.Ordinal) &&
                     candidate.SgbdSha256 == expected.SgbdSha256 &&
                     candidate.BackendSha256 == expected.BackendSha256;

        if (!exact)
        {
            throw new InvalidOperationException("Dispatch tuple is not exactly allowlisted.");
        }
    }

    public static void RunSelfTests()
    {
        ReviewedDispatch approved = Resolve(new ParameterId("engine.rpm"));
        EnsureExact(approved);

        ExpectRejected(approved with { Job = "STEUERN_LL_DREHZAHL_VERSTELLEN" });
        ExpectRejected(approved with { Argument = "750" });
        ExpectRejected(approved with { ResultNames = ["JOB_STATUS"] });
        ExpectRejected(approved with { SgbdSha256 = new string('0', 64) });
        ExpectRejected(approved with { BackendSha256 = new string('0', 64) });
    }

    private static void ExpectRejected(ReviewedDispatch candidate)
    {
        try
        {
            EnsureExact(candidate);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Guard accepted a prohibited dispatch tuple.");
    }
}

internal sealed class FakeRpmAcquisition : IReadOnlyAcquisition
{
    private static readonly ParameterId RpmId = new("engine.rpm");
    private readonly Guid _epochId = Guid.NewGuid();
    private readonly bool _injectFaults;
    private bool _connected;
    private long _cycleId;

    public FakeRpmAcquisition(bool injectFaults)
    {
        _injectFaults = injectFaults;
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connected)
        {
            throw new InvalidOperationException("Fake acquisition is already connected.");
        }

        _connected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AcquisitionBatch> ReadAsync(
        IReadOnlyCollection<ParameterId> parameterIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
        {
            throw new InvalidOperationException("Fake acquisition is not connected.");
        }

        if (parameterIds.Count != 1 || !parameterIds.Contains(RpmId))
        {
            throw new InvalidOperationException("Fake source supports only engine.rpm.");
        }

        long cycleId = ++_cycleId;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        (object? value, SampleQuality quality, string? errorCode) = CreateValue(cycleId);
        DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
        var sample = new ParameterSample(
            RpmId,
            finishedAt,
            Stopwatch.GetTimestamp(),
            value,
            "1/min",
            quality,
            errorCode);
        var batch = new AcquisitionBatch(
            _epochId,
            cycleId,
            startedAt,
            finishedAt,
            [sample]);
        return new ValueTask<AcquisitionBatch>(batch);
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }

    private (object? Value, SampleQuality Quality, string? ErrorCode) CreateValue(long cycleId)
    {
        double rpm = 720.0 + 18.0 * Math.Sin(cycleId / 7.0);
        if (!_injectFaults)
        {
            return (rpm, SampleQuality.Valid, null);
        }

        if (cycleId % 41 == 0)
        {
            return (null, SampleQuality.Error, "SIM_ERROR");
        }
        if (cycleId % 37 == 0)
        {
            return (null, SampleQuality.Missing, "SIM_MISSING");
        }
        if (cycleId % 29 == 0)
        {
            return (rpm, SampleQuality.Stale, "SIM_STALE");
        }
        if (cycleId % 17 == 0)
        {
            return (null, SampleQuality.Timeout, "SIM_TIMEOUT");
        }

        return (rpm, SampleQuality.Valid, null);
    }
}

internal sealed class CsvSampleWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvSampleWriter(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        _writer.WriteLine(
            "epoch_id,cycle_id,started_utc,finished_utc,acquired_utc," +
            "monotonic_ticks,parameter_id,value,unit,quality,error_code");
    }

    public void Write(AcquisitionBatch batch)
    {
        foreach (ParameterSample sample in batch.Samples)
        {
            string[] fields =
            {
                batch.EpochId.ToString("D"),
                batch.CycleId.ToString(CultureInfo.InvariantCulture),
                batch.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                batch.FinishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.AcquiredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.MonotonicTicks.ToString(CultureInfo.InvariantCulture),
                sample.ParameterId.Value,
                FormatValue(sample.Value),
                sample.Unit ?? string.Empty,
                sample.Quality.ToString(),
                sample.ErrorCode ?? string.Empty
            };
            _writer.WriteLine(string.Join(',', fields.Select(Escape)));
        }

        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable =>
            formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') &&
            !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
