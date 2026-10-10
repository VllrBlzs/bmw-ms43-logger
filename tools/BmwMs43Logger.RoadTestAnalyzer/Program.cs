using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.RoadTestAnalyzer;

internal static class Program
{
    private const string ExpectedSgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string ExpectedBackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    private const string ExpectedCatalogSha256 =
        "5ABF17EE1730546CE2FE4A4D950650E76B4EB7005AAF830068A413CF4903693C";

    public static int Main(string[] args)
    {
        try
        {
            AnalyzerOptions options = AnalyzerOptions.Parse(args);
            RoadTestAnalysis analysis = Analyze(options);
            string reportPath = Path.Combine(
                options.RunDirectory,
                "bmw13-road-test-acceptance.json");
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(analysis, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }));

            Console.WriteLine("BMW-13 ROAD-TEST SESSION ACCEPTANCE");
            Console.WriteLine(
                $"ANALYSIS_MODE={(analysis.OfficialRoadTest ? "OFFICIAL" : "REPLAY_FIXTURE")}");
            Console.WriteLine($"SESSION_STATUS={analysis.SessionStatus.ToUpperInvariant()}");
            Console.WriteLine($"DURATION_SECONDS={analysis.DurationSeconds:F2}");
            Console.WriteLine($"EPOCHS={analysis.Epochs}");
            Console.WriteLine($"CYCLES={analysis.Cycles}");
            Console.WriteLine($"ROWS={analysis.Rows}");
            Console.WriteLine($"PARAMETERS={analysis.Parameters}");
            Console.WriteLine($"VALID_RATIO={analysis.ValidRatio:F6}");
            Console.WriteLine($"EFFECTIVE_HZ={analysis.EffectiveHz:F3}");
            Console.WriteLine($"MAX_SPEED_KMH={Format(analysis.MaxSpeedKmh)}");
            Console.WriteLine($"RPM_RANGE={Format(analysis.RpmRange)}");
            Console.WriteLine($"CSV_SHA256_MATCH={Pass(analysis.CsvSha256Match)}");
            Console.WriteLine($"SCHEMA_AND_ROUNDTRIP={Pass(analysis.SchemaAndRoundtrip)}");
            Console.WriteLine($"COVERAGE={Pass(analysis.CoveragePass)}");
            Console.WriteLine($"CADENCE={Pass(analysis.CadencePass)}");
            Console.WriteLine($"ROAD_DYNAMICS={Pass(analysis.RoadDynamicsPass)}");
            Console.WriteLine($"PRIVACY={Pass(analysis.PrivacyPass)}");
            foreach ((string quality, long count) in analysis.QualityCounts)
            {
                Console.WriteLine($"QUALITY_{quality.ToUpperInvariant()}={count}");
            }
            Console.WriteLine($"REPORT={Path.GetFullPath(reportPath)}");
            Console.WriteLine($"RESULT={(analysis.Result == "pass" ? "PASS" : "FAIL")}");
            return analysis.Result == "pass" ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static RoadTestAnalysis Analyze(AnalyzerOptions options)
    {
        string metadataPath = Path.Combine(options.RunDirectory, "session.meta.json");
        string csvPath = Path.Combine(options.RunDirectory, "session.csv");
        if (!File.Exists(metadataPath) || !File.Exists(csvPath))
        {
            throw new FileNotFoundException(
                "The run directory must contain session.csv and session.meta.json.");
        }

        SessionLogMetadata metadata = JsonSerializer.Deserialize<SessionLogMetadata>(
            File.ReadAllText(metadataPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? throw new InvalidOperationException("Session metadata is empty.");
        string actualHash;
        using (FileStream stream = File.OpenRead(csvPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        var epochs = new HashSet<Guid>();
        var parameters = new HashSet<string>(StringComparer.Ordinal);
        var qualityCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long rows = 0;
        long cycles = 0;
        long valid = 0;
        long? activeCycle = null;
        var activeCycleParameters = new HashSet<string>(StringComparer.Ordinal);
        bool cycleShapePass = true;
        string? sessionId = null;
        double? maxSpeed = null;
        double? minRpm = null;
        double? maxRpm = null;

        using var reader = new StreamReader(csvPath);
        string header = reader.ReadLine() ??
            throw new InvalidOperationException("CSV is empty.");
        bool headerPass = string.Equals(
            header,
            SessionCsvLogger.CsvHeader,
            StringComparison.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] fields = ParseCsv(line);
            if (fields.Length != 21)
            {
                throw new InvalidOperationException(
                    $"CSV row {rows + 2} has {fields.Length} fields instead of 21.");
            }
            if (fields[0] != SessionCsvLogger.CsvSchemaVersion)
            {
                throw new InvalidOperationException("CSV schema changed inside the file.");
            }
            sessionId ??= fields[1];
            if (sessionId != fields[1])
            {
                throw new InvalidOperationException("Multiple session IDs are present.");
            }
            epochs.Add(Guid.Parse(fields[2]));
            long cycleId = long.Parse(fields[3], CultureInfo.InvariantCulture);
            string parameter = fields[6];
            string quality = fields[16];

            if (activeCycle != cycleId)
            {
                if (activeCycle.HasValue)
                {
                    cycleShapePass &=
                        activeCycleParameters.Count == metadata.SelectedParameterIds.Count;
                    if (cycleId != activeCycle.Value + 1)
                    {
                        cycleShapePass = false;
                    }
                }
                else if (cycleId != 1)
                {
                    cycleShapePass = false;
                }
                activeCycle = cycleId;
                activeCycleParameters.Clear();
                cycles++;
            }
            if (!activeCycleParameters.Add(parameter))
            {
                cycleShapePass = false;
            }
            parameters.Add(parameter);
            qualityCounts.TryGetValue(quality, out long qualityCount);
            qualityCounts[quality] = qualityCount + 1;
            rows++;

            if (quality == "valid")
            {
                valid++;
                if (string.IsNullOrWhiteSpace(fields[11]))
                {
                    cycleShapePass = false;
                }
                if (parameter == "vehicle.speed" &&
                    TryFinite(fields[13], out double speed))
                {
                    maxSpeed = !maxSpeed.HasValue
                        ? speed
                        : Math.Max(maxSpeed.Value, speed);
                }
                if (parameter == "engine.rpm" &&
                    TryFinite(fields[13], out double rpm))
                {
                    minRpm = !minRpm.HasValue ? rpm : Math.Min(minRpm.Value, rpm);
                    maxRpm = !maxRpm.HasValue ? rpm : Math.Max(maxRpm.Value, rpm);
                }
            }
        }
        if (activeCycle.HasValue)
        {
            cycleShapePass &=
                activeCycleParameters.Count == metadata.SelectedParameterIds.Count;
        }

        var metadataParameters = new HashSet<string>(
            metadata.SelectedParameterIds,
            StringComparer.Ordinal);
        var approvedParameters = new HashSet<string>(
            Ms43ParameterCatalog.ApprovedParameters.Select(id => id.Value),
            StringComparer.Ordinal);
        double validRatio = rows == 0 ? 0.0 : valid / (double)rows;
        double effectiveHz = metadata.EffectiveHz ?? 0.0;
        double? rpmRange = minRpm.HasValue && maxRpm.HasValue
            ? maxRpm.Value - minRpm.Value
            : null;

        bool hashPass = string.Equals(
            actualHash,
            metadata.CsvSha256,
            StringComparison.Ordinal);
        bool schemaPass = headerPass &&
                          metadata.SchemaVersion == SessionCsvLogger.MetadataSchemaVersion &&
                          metadata.CsvSchemaVersion == SessionCsvLogger.CsvSchemaVersion &&
                          metadata.SessionId == sessionId &&
                          metadata.CyclesWritten == cycles &&
                          metadata.SamplesWritten == rows &&
                          metadata.Status == "completed" &&
                          metadata.StopReason == "requested" &&
                          metadata.FaultCode is null &&
                          epochs.Count == 1 &&
                          cycleShapePass &&
                          (options.FixtureMode || IsPinnedLiveSession(metadata));
        bool coveragePass =
            metadata.SelectedParameterIds.Count == options.ExpectedParameters &&
            parameters.SetEquals(metadataParameters) &&
            (options.FixtureMode ||
             metadataParameters.IsSubsetOf(approvedParameters)) &&
            rows == cycles * metadata.SelectedParameterIds.Count &&
            validRatio >= options.MinimumValidRatio;
        bool cadencePass =
            metadata.DurationSeconds >= options.MinimumDurationSeconds &&
            effectiveHz >= options.MinimumEffectiveHz &&
            metadata.QueueDropped == 0 &&
            metadata.WriteErrors == 0;
        bool dynamicsPass =
            maxSpeed.HasValue && maxSpeed.Value >= options.MinimumMovingSpeedKmh &&
            rpmRange.HasValue && rpmRange.Value >= options.MinimumRpmRange;
        bool privacyPass =
            !metadata.Privacy.VinRecorded &&
            !metadata.Privacy.AbsolutePathsRecorded &&
            !metadata.Privacy.LicensedDescriptorsEmbedded;
        bool passed = hashPass && schemaPass && coveragePass && cadencePass &&
                      dynamicsPass && privacyPass;

        return new RoadTestAnalysis(
            "bmw13.road_test_acceptance.v1",
            DateTimeOffset.UtcNow,
            metadata.SessionId,
            metadata.Status,
            metadata.DurationSeconds,
            epochs.Count,
            cycles,
            rows,
            parameters.Count,
            validRatio,
            effectiveHz,
            maxSpeed,
            rpmRange,
            hashPass,
            schemaPass,
            coveragePass,
            cadencePass,
            dynamicsPass,
            privacyPass,
            qualityCounts,
            !options.FixtureMode,
            passed ? "pass" : "fail");
    }

    private static string[] ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        if (quoted)
        {
            throw new InvalidOperationException("CSV contains an unterminated quote.");
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static bool IsPinnedLiveSession(SessionLogMetadata metadata) =>
        metadata.SessionId.StartsWith("bmw14.live.", StringComparison.Ordinal) &&
        metadata.EcuProfileId == "bmw.e39.525i.m54b25.ms43.ms430ds0" &&
        metadata.SgbdSha256 == ExpectedSgbdSha256 &&
        metadata.BackendSha256 == ExpectedBackendSha256 &&
        metadata.CatalogVersion == "0.1.0-draft" &&
        metadata.CatalogSha256 == ExpectedCatalogSha256;

    private static bool TryFinite(string text, out double value) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && double.IsFinite(value);

    private static string Pass(bool value) => value ? "PASS" : "FAIL";

    private static string Format(double? value) => value.HasValue
        ? value.Value.ToString("F3", CultureInfo.InvariantCulture)
        : "N/A";
}

internal sealed record AnalyzerOptions(
    string RunDirectory,
    int ExpectedParameters,
    double MinimumDurationSeconds,
    double MinimumEffectiveHz,
    double MinimumValidRatio,
    double MinimumMovingSpeedKmh,
    double MinimumRpmRange,
    bool FixtureMode)
{
    public static AnalyzerOptions Parse(IReadOnlyList<string> args)
    {
        string? runDirectory = null;
        int expectedParameters = 22;
        double minimumDuration = 840.0;
        double minimumHz = 1.5;
        double minimumValidRatio = 0.98;
        double minimumSpeed = 5.0;
        double minimumRpmRange = 300.0;
        bool fixtureMode = false;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--run-directory" when index + 1 < args.Count:
                    runDirectory = args[++index];
                    break;
                case "--expected-parameters" when index + 1 < args.Count:
                    expectedParameters = int.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--minimum-seconds" when index + 1 < args.Count:
                    minimumDuration = double.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--minimum-hz" when index + 1 < args.Count:
                    minimumHz = double.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--fixture":
                    fixtureMode = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            throw new ArgumentException("--run-directory is required.");
        }
        return new AnalyzerOptions(
            Path.GetFullPath(runDirectory),
            expectedParameters,
            minimumDuration,
            minimumHz,
            minimumValidRatio,
            minimumSpeed,
            minimumRpmRange,
            fixtureMode);
    }
}

internal sealed record RoadTestAnalysis(
    string SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string SessionId,
    string SessionStatus,
    double DurationSeconds,
    int Epochs,
    long Cycles,
    long Rows,
    int Parameters,
    double ValidRatio,
    double EffectiveHz,
    double? MaxSpeedKmh,
    double? RpmRange,
    bool CsvSha256Match,
    bool SchemaAndRoundtrip,
    bool CoveragePass,
    bool CadencePass,
    bool RoadDynamicsPass,
    bool PrivacyPass,
    IReadOnlyDictionary<string, long> QualityCounts,
    bool OfficialRoadTest,
    string Result);
