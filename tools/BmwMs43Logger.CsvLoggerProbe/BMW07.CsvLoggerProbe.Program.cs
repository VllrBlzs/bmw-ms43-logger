using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.CsvLoggerProbe;

internal static class Program
{
    private const string SgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string BackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";

    private static readonly ParameterId Rpm = new("engine.rpm");
    private static readonly ParameterId Speed = new("vehicle.speed");
    private static readonly ParameterId Mode = new("engine.mode");

    public static int Main(string[] args)
    {
        try
        {
            string root = Parse(args);
            string runDirectory = CreateRunDirectory(root);
            FixtureResult normal = RunNormalFixture(runDirectory);
            FixtureResult fault = RunStorageFaultFixture(runDirectory);

            Console.WriteLine("BMW-07 OFFLINE CSV LOGGER DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine($"CSV_SCHEMA={SessionCsvLogger.CsvSchemaVersion}");
            Console.WriteLine($"METADATA_SCHEMA={SessionCsvLogger.MetadataSchemaVersion}");
            Console.WriteLine("CONTROLLED_STOP=PASS");
            Console.WriteLine("CSV_ROUNDTRIP=PASS");
            Console.WriteLine("METADATA_ROUNDTRIP=PASS");
            Console.WriteLine("QUALITY_COUNTS=PASS");
            Console.WriteLine("QUEUE_OVERFLOW_VISIBLE=PASS");
            Console.WriteLine("STORAGE_FAULT_VISIBLE=PASS");
            Console.WriteLine("PRIVACY_FIELDS=PASS");
            Console.WriteLine($"NORMAL_ROWS={normal.DataRows}");
            Console.WriteLine($"FAULT_PARTIAL_ROWS={fault.DataRows}");
            Console.WriteLine($"NORMAL_CSV={normal.CsvPath}");
            Console.WriteLine($"NORMAL_METADATA={normal.MetadataPath}");
            Console.WriteLine($"FAULT_CSV={fault.CsvPath}");
            Console.WriteLine($"FAULT_METADATA={fault.MetadataPath}");
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

    private static FixtureResult RunNormalFixture(string runDirectory)
    {
        string directory = Path.Combine(runDirectory, "normal");
        const string stem = "bmw07_normal_session";
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        Guid epoch = Guid.Parse("77777777-7777-7777-7777-777777777701");
        SessionLogDescriptor descriptor = CreateDescriptor("bmw07.normal.fixture");

        SessionLogMetadata metadata;
        using (var logger = new SessionCsvLogger(
                   descriptor,
                   new FileSessionLogStorage(directory, stem),
                   startedAtUtc,
                   startedTicks))
        {
            logger.AppendCycle(CreateCycle(
                epoch,
                1,
                startedAtUtc,
                startedTicks,
                rpmValue: 750.0,
                speedValue: 0.0,
                modeValue: "idle, \"stable\"",
                rpmQuality: SampleQuality.Valid,
                speedQuality: SampleQuality.Valid,
                modeQuality: SampleQuality.Valid));
            logger.AppendCycle(CreateCycle(
                epoch,
                2,
                startedAtUtc,
                startedTicks,
                rpmValue: 752.0,
                speedValue: null,
                modeValue: "idle",
                rpmQuality: SampleQuality.Valid,
                speedQuality: SampleQuality.Missing,
                modeQuality: SampleQuality.Valid));
            logger.AppendCycle(CreateCycle(
                epoch,
                3,
                startedAtUtc,
                startedTicks,
                rpmValue: 752.0,
                speedValue: null,
                modeValue: null,
                rpmQuality: SampleQuality.Stale,
                speedQuality: SampleQuality.Timeout,
                modeQuality: SampleQuality.Error));
            logger.RecordQueueDropped(2);
            metadata = logger.Complete(
                startedAtUtc.AddMilliseconds(700),
                startedTicks + TicksFromMilliseconds(700),
                SessionStopReason.Requested);
        }

        string csvPath = Path.Combine(directory, stem + ".csv");
        string metadataPath = Path.Combine(directory, stem + ".meta.json");
        int dataRows = ValidateNormalCsv(csvPath);
        ValidateNormalMetadata(metadataPath, csvPath, directory, metadata);
        return new FixtureResult(csvPath, metadataPath, dataRows);
    }

    private static FixtureResult RunStorageFaultFixture(string runDirectory)
    {
        string directory = Path.Combine(runDirectory, "storage-fault");
        const string stem = "bmw07_storage_fault_session";
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        Guid epoch = Guid.Parse("77777777-7777-7777-7777-777777777702");
        SessionLogDescriptor descriptor = CreateDescriptor("bmw07.storage_fault.fixture");
        var storage = new FailOnCsvWriteStorage(
            new FileSessionLogStorage(directory, stem),
            failOnWriteNumber: 4);

        bool expectedFailure = false;
        using (var logger = new SessionCsvLogger(
                   descriptor,
                   storage,
                   startedAtUtc,
                   startedTicks))
        {
            try
            {
                logger.AppendCycle(CreateCycle(
                    epoch,
                    1,
                    startedAtUtc,
                    startedTicks,
                    rpmValue: 748.0,
                    speedValue: 0.0,
                    modeValue: "idle",
                    rpmQuality: SampleQuality.Valid,
                    speedQuality: SampleQuality.Valid,
                    modeQuality: SampleQuality.Valid));
            }
            catch (SessionLogWriteException exception)
                when (exception.InnerException is IOException)
            {
                expectedFailure = true;
            }
        }
        Require(expectedFailure, "Injected storage failure was not surfaced.");

        string csvPath = Path.Combine(directory, stem + ".csv");
        string metadataPath = Path.Combine(directory, stem + ".meta.json");
        int dataRows = ValidateFaultCsv(csvPath);
        ValidateFaultMetadata(metadataPath, csvPath, directory);
        return new FixtureResult(csvPath, metadataPath, dataRows);
    }

    private static SessionLogDescriptor CreateDescriptor(string sessionId)
    {
        string catalogHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("bmw07-dryrun-catalog-v1")));
        return new SessionLogDescriptor(
            sessionId,
            "bmw.e39.525i.m54b25.ms43.ms430ds0",
            SgbdSha256,
            BackendSha256,
            "bmw07.dryrun.catalog.v1",
            catalogHash,
            RequestedHz: 5.0,
            SelectedParameters: [Rpm, Speed, Mode]);
    }

    private static NormalizedCycle CreateCycle(
        Guid epoch,
        long cycleId,
        DateTimeOffset sessionStartUtc,
        long sessionStartTicks,
        object? rpmValue,
        object? speedValue,
        object? modeValue,
        SampleQuality rpmQuality,
        SampleQuality speedQuality,
        SampleQuality modeQuality)
    {
        int cycleOffsetMs = checked((int)cycleId * 200);
        DateTimeOffset completedAtUtc = sessionStartUtc.AddMilliseconds(cycleOffsetMs);
        long completedTicks = sessionStartTicks + TicksFromMilliseconds(cycleOffsetMs);
        NormalizedSample[] samples =
        [
            CreateSample(
                epoch,
                cycleId,
                Rpm,
                1,
                "STATUS_MOTORDREHZAHL",
                completedAtUtc,
                completedTicks,
                rpmValue,
                "1/min",
                rpmQuality),
            CreateSample(
                epoch,
                cycleId,
                Speed,
                2,
                "STATUS_MESSWERTEBLOCK",
                completedAtUtc,
                completedTicks,
                speedValue,
                "km/h",
                speedQuality),
            CreateSample(
                epoch,
                cycleId,
                Mode,
                2,
                "STATUS_MESSWERTEBLOCK",
                completedAtUtc,
                completedTicks,
                modeValue,
                null,
                modeQuality)
        ];
        return new NormalizedCycle(
            epoch,
            cycleId,
            completedAtUtc,
            completedTicks,
            samples);
    }

    private static NormalizedSample CreateSample(
        Guid epoch,
        long cycleId,
        ParameterId parameterId,
        int jobSequence,
        string job,
        DateTimeOffset cycleCompletedAtUtc,
        long cycleCompletedTicks,
        object? value,
        string? unit,
        SampleQuality quality)
    {
        bool hasAcquiredValue = quality is SampleQuality.Valid or SampleQuality.Stale;
        int jobDurationMs = jobSequence == 1 ? 80 : 90;
        int jobFinishBeforeCycleMs = jobSequence == 1 ? 100 : 10;
        DateTimeOffset jobFinishedAtUtc =
            cycleCompletedAtUtc.AddMilliseconds(-jobFinishBeforeCycleMs);
        DateTimeOffset jobStartedAtUtc =
            jobFinishedAtUtc.AddMilliseconds(-jobDurationMs);
        DateTimeOffset? acquiredAtUtc = hasAcquiredValue
            ? jobFinishedAtUtc.AddMilliseconds(-20)
            : null;
        long? acquiredTicks = hasAcquiredValue
            ? cycleCompletedTicks - TicksFromMilliseconds(jobFinishBeforeCycleMs + 20)
            : null;
        string? errorCode = quality switch
        {
            SampleQuality.Valid => null,
            SampleQuality.Stale => "SIM_STALE",
            SampleQuality.Missing => "RESULT_NOT_RETURNED",
            SampleQuality.Timeout => "SIM_TIMEOUT",
            SampleQuality.Error => "SIM_ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(quality))
        };
        return new NormalizedSample(
            epoch,
            cycleId,
            parameterId,
            jobSequence,
            job,
            jobStartedAtUtc,
            jobFinishedAtUtc,
            acquiredAtUtc,
            acquiredTicks,
            value,
            hasAcquiredValue ? unit : null,
            quality,
            errorCode,
            jobDurationMs,
            hasAcquiredValue ? jobDurationMs - 20 : null,
            hasAcquiredValue
                ? jobFinishBeforeCycleMs + 20
                : jobFinishBeforeCycleMs);
    }

    private static int ValidateNormalCsv(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Require(lines.Length == 10, "Normal CSV must contain one header and nine data rows.");
        string[] header = ParseCsvLine(lines[0]);
        Require(header.Length == 21, "CSV header column count mismatch.");
        Require(string.Join(',', header) == SessionCsvLogger.CsvHeader,
            "CSV header round-trip mismatch.");

        string[][] rows = lines.Skip(1).Select(ParseCsvLine).ToArray();
        Require(rows.All(row => row.Length == header.Length),
            "CSV data column count mismatch.");
        Require(rows.All(row => row[0] == SessionCsvLogger.CsvSchemaVersion),
            "CSV schema marker mismatch.");
        Require(rows.Select(row => row[3]).Distinct().SequenceEqual(["1", "2", "3"]),
            "Cycle IDs were not preserved.");
        Require(rows.Any(row => row[13] == "idle, \"stable\""),
            "Quoted CSV value did not round-trip.");

        var qualityCounts = rows
            .GroupBy(row => row[16], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Require(qualityCounts["valid"] == 5, "Valid count mismatch.");
        Require(qualityCounts["missing"] == 1, "Missing count mismatch.");
        Require(qualityCounts["stale"] == 1, "Stale count mismatch.");
        Require(qualityCounts["timeout"] == 1, "Timeout count mismatch.");
        Require(qualityCounts["error"] == 1, "Error count mismatch.");
        return rows.Length;
    }

    private static void ValidateNormalMetadata(
        string metadataPath,
        string csvPath,
        string outputDirectory,
        SessionLogMetadata returnedMetadata)
    {
        string json = File.ReadAllText(metadataPath, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("schema_version").GetString() ==
            SessionCsvLogger.MetadataSchemaVersion, "Metadata schema mismatch.");
        Require(root.GetProperty("csv_schema_version").GetString() ==
            SessionCsvLogger.CsvSchemaVersion, "Metadata CSV schema mismatch.");
        Require(root.GetProperty("status").GetString() == "completed",
            "Completed status missing.");
        Require(root.GetProperty("stop_reason").GetString() == "requested",
            "Requested stop reason missing.");
        Require(root.GetProperty("cycles_written").GetInt64() == 3,
            "Metadata cycle count mismatch.");
        Require(root.GetProperty("samples_written").GetInt64() == 9,
            "Metadata sample count mismatch.");
        Require(root.GetProperty("queue_dropped").GetInt64() == 2,
            "Queue drop count is not visible.");
        Require(root.GetProperty("write_errors").GetInt64() == 0,
            "Unexpected normal-session write error.");
        JsonElement qualityCounts = root.GetProperty("quality_counts");
        Require(qualityCounts.GetProperty("valid").GetInt64() == 5,
            "Metadata valid count mismatch.");
        Require(qualityCounts.GetProperty("missing").GetInt64() == 1,
            "Metadata missing count mismatch.");
        Require(qualityCounts.GetProperty("stale").GetInt64() == 1,
            "Metadata stale count mismatch.");
        Require(qualityCounts.GetProperty("timeout").GetInt64() == 1,
            "Metadata timeout count mismatch.");
        Require(qualityCounts.GetProperty("error").GetInt64() == 1,
            "Metadata error count mismatch.");
        Require(Math.Abs(root.GetProperty("effective_hz").GetDouble() - 5.0) < 0.0001,
            "Effective Hz mismatch.");
        Require(root.GetProperty("csv_sha256").GetString() == HashFile(csvPath),
            "CSV hash mismatch.");
        Require(returnedMetadata.CsvSha256 == HashFile(csvPath),
            "Returned metadata differs from persisted metadata.");
        ValidatePrivacy(root, json, outputDirectory);
    }

    private static int ValidateFaultCsv(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Require(lines.Length == 3,
            "Fault CSV must contain a header and two successfully written rows.");
        Require(lines.All(line => ParseCsvLine(line).Length == 21),
            "Fault CSV is not parseable after controlled failure.");
        return lines.Length - 1;
    }

    private static void ValidateFaultMetadata(
        string metadataPath,
        string csvPath,
        string outputDirectory)
    {
        string json = File.ReadAllText(metadataPath, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("status").GetString() == "faulted",
            "Fault status missing.");
        Require(root.GetProperty("stop_reason").GetString() == "storage_error",
            "Storage fault reason missing.");
        Require(root.GetProperty("fault_code").GetString() == "CSV_WRITE_FAILED",
            "Storage fault code missing.");
        Require(root.GetProperty("cycles_written").GetInt64() == 0,
            "Partial cycle was incorrectly counted as complete.");
        Require(root.GetProperty("samples_written").GetInt64() == 2,
            "Successful partial-row count mismatch.");
        Require(root.GetProperty("incomplete_cycle_id").GetInt64() == 1,
            "Incomplete cycle is not visible.");
        Require(root.GetProperty("write_errors").GetInt64() == 1,
            "Write error count mismatch.");
        Require(root.GetProperty("csv_sha256").GetString() == HashFile(csvPath),
            "Fault CSV hash mismatch.");
        ValidatePrivacy(root, json, outputDirectory);
    }

    private static void ValidatePrivacy(
        JsonElement metadata,
        string rawJson,
        string outputDirectory)
    {
        JsonElement privacy = metadata.GetProperty("privacy");
        Require(!privacy.GetProperty("vin_recorded").GetBoolean(),
            "VIN privacy flag failed.");
        Require(!privacy.GetProperty("absolute_paths_recorded").GetBoolean(),
            "Absolute-path privacy flag failed.");
        Require(!privacy.GetProperty("licensed_descriptors_embedded").GetBoolean(),
            "Licensed-descriptor privacy flag failed.");
        Require(!rawJson.Contains(
                Path.GetFullPath(outputDirectory),
                StringComparison.OrdinalIgnoreCase),
            "Metadata leaked an absolute output path.");
        Require(Path.GetFileName(metadata.GetProperty("csv_file_name").GetString()) ==
            metadata.GetProperty("csv_file_name").GetString(),
            "CSV metadata contains a path instead of a basename.");
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (character == '"' && current.Length == 0)
            {
                quoted = true;
            }
            else
            {
                current.Append(character);
            }
        }
        Require(!quoted, "CSV row contains an unterminated quote.");
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static string Parse(IReadOnlyList<string> args)
    {
        bool dryRun = false;
        string output = Path.Combine("artifacts", "bmw07");
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

    private static string CreateRunDirectory(string root)
    {
        string name = "bmw07_dryrun_" +
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) +
            "_" + Guid.NewGuid().ToString("N")[..6];
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static long TicksFromMilliseconds(int milliseconds) =>
        checked((long)Math.Round(
            milliseconds * Stopwatch.Frequency / 1000.0,
            MidpointRounding.AwayFromZero));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
    string CsvPath,
    string MetadataPath,
    int DataRows);

internal sealed class FailOnCsvWriteStorage : ISessionLogStorage
{
    private readonly ISessionLogStorage _inner;
    private readonly int _failOnWriteNumber;
    private int _writeNumber;

    public FailOnCsvWriteStorage(
        ISessionLogStorage inner,
        int failOnWriteNumber)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (failOnWriteNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failOnWriteNumber));
        }
        _inner = inner;
        _failOnWriteNumber = failOnWriteNumber;
    }

    public string CsvFileName => _inner.CsvFileName;
    public string MetadataFileName => _inner.MetadataFileName;

    public void WriteCsvLine(string line)
    {
        _writeNumber++;
        if (_writeNumber == _failOnWriteNumber)
        {
            throw new IOException("SIMULATED_DISK_FULL");
        }
        _inner.WriteCsvLine(line);
    }

    public void CompleteCsv() => _inner.CompleteCsv();
    public string GetCsvSha256() => _inner.GetCsvSha256();
    public void WriteMetadata(string json) => _inner.WriteMetadata(json);
    public void Dispose() => _inner.Dispose();
}
