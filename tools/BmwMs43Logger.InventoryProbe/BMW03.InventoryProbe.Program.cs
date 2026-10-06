using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdiabasLib;

namespace BmwMs43Logger.InventoryProbe;

internal static class Program
{
    private const string SchemaVersion = "bmw03.inventory.v1";
    private const string ClassificationVersion = "bmw03.conservative.v1";
    private const string PrgFileName = "ms430ds0.prg";
    private const string ExpectedPrgSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string ExpectedAssemblySha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    private const int MaximumJobRecords = 4096;
    private const int MaximumRowsPerVirtualJob = 512;
    private const int MaximumFieldsPerRow = 256;
    private const int MaximumFieldCharacters = 1024;

    private static readonly HashSet<string> AllowedVirtualJobs =
        new(StringComparer.Ordinal)
        {
            "_JOBS",
            "_JOBCOMMENTS",
            "_ARGUMENTS",
            "_RESULTS"
        };

    private static readonly string[] MutationRiskTokens =
    {
        "STEUERN",
        "ANSTEUERN",
        "STELLEN",
        "VERSTELLEN",
        "LOESCHEN",
        "LOSCHEN",
        "RESET",
        "RUECKSETZEN",
        "SETZEN",
        "SCHREIBEN",
        "PROGRAMMIEREN",
        "PROGRAM",
        "CODIEREN",
        "CODIER",
        "AKTIVIEREN",
        "DEAKTIVIEREN",
        "INITIALISIEREN",
        "INITIALISIER",
        "ADAPTION",
        "ADAPTATION",
        "ABGLEICH",
        "KALIBRIEREN",
        "KALIBRIER",
        "JUSTIEREN",
        "JUSTIER",
        "LERNEN",
        "ENTSPERREN",
        "SPERREN",
        "STARTEN",
        "STOPPEN",
        "ROUTINE",
        "AKTIVTEST",
        "AKTIV_TEST"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            Run(args);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static void Run(IReadOnlyList<string> args)
    {
        ProbeOptions options = ProbeOptions.Parse(args);
        RunClassifierSelfTests();

        string prgPath = Path.Combine(options.EcuPath, PrgFileName);
        RequireFileHash(prgPath, ExpectedPrgSha256, "SGBD");

        string assemblyPath = typeof(EdiabasNet).Assembly.Location;
        RequireFileHash(
            assemblyPath,
            ExpectedAssemblySha256,
            "EdiabasLib runtime assembly");

        string config = string.Join(
            ';',
            $"EcuPath={options.EcuPath}",
            "ApiTrace=0",
            "IfhTrace=0",
            "TraceBuffering=0",
            "IfhTraceBuffering=0");

        using var ediabas = new EdiabasNet(config, isHosted: true)
        {
            NoInitForVJobs = true,
            SgbdFileName = PrgFileName
        };

        RequireNoInterface(ediabas);
        string[] jobs = ExtractJobNames(
            ExecuteVirtualJob(ediabas, "_JOBS", string.Empty));

        if (jobs.Length is 0 or > MaximumJobRecords)
        {
            throw new InvalidOperationException(
                $"Unexpected offline job count: {jobs.Length}.");
        }

        var inventory = new List<JobInventoryItem>(jobs.Length);
        foreach (string job in jobs)
        {
            inventory.Add(InspectJob(ediabas, job));
        }

        RequireNoInterface(ediabas);
        ValidateInventory(inventory);
        Directory.CreateDirectory(options.OutputPath);

        DateTimeOffset generatedUtc = DateTimeOffset.UtcNow;
        var document = new InventoryDocument(
            SchemaVersion,
            ClassificationVersion,
            generatedUtc,
            CommunicationAttempted: false,
            InterfaceInstance: "none",
            PrgFileName,
            ExpectedPrgSha256,
            Path.GetFileName(assemblyPath),
            ExpectedAssemblySha256,
            inventory);

        var allowlist = new ApprovedAllowlistDocument(
            SchemaVersion: "bmw03.allowlist.v1",
            GeneratedUtc: generatedUtc,
            ProfileState: "controlled_test",
            PrgFileName,
            ExpectedPrgSha256,
            ExpectedAssemblySha256,
            Entries:
            [
                new ApprovedDispatch(
                    ParameterId: "engine.rpm",
                    Job: "STATUS_MOTORDREHZAHL",
                    Argument: string.Empty,
                    ResultNames:
                    [
                        "JOB_STATUS",
                        "STAT_MOTORDREHZAHL_WERT",
                        "STATUS_MOTORDREHZAHL_WERT",
                        "STAT_MOTORDREHZAHL_EINH"
                    ],
                    Evidence: "BMW-02C live proof")
            ]);

        string inventoryPath = Path.Combine(
            options.OutputPath,
            "ms430ds0-job-inventory.json");
        string coveragePath = Path.Combine(
            options.OutputPath,
            "ms430ds0-coverage.csv");
        string reviewQueuePath = Path.Combine(
            options.OutputPath,
            "ms430ds0-review-queue.csv");
        string allowlistPath = Path.Combine(
            options.OutputPath,
            "ms430ds0-approved-allowlist.json");

        WriteJson(inventoryPath, document);
        WriteCoverageCsv(coveragePath, inventory);
        WriteReviewQueueCsv(reviewQueuePath, inventory);
        WriteJson(allowlistPath, allowlist);

        PrintSummary(
            inventory,
            inventoryPath,
            coveragePath,
            reviewQueuePath,
            allowlistPath,
            assemblyPath,
            prgPath);
    }

    private static JobInventoryItem InspectJob(EdiabasNet ediabas, string job)
    {
        var metadataErrors = new List<string>();
        IReadOnlyList<MetadataRow> comments = ReadMetadata(
            ediabas,
            "_JOBCOMMENTS",
            job,
            metadataErrors);
        IReadOnlyList<MetadataRow> arguments = ReadMetadata(
            ediabas,
            "_ARGUMENTS",
            job,
            metadataErrors);
        IReadOnlyList<MetadataRow> results = ReadMetadata(
            ediabas,
            "_RESULTS",
            job,
            metadataErrors);

        ReviewDecision decision = Classify(
            job,
            comments,
            arguments,
            results,
            metadataErrors);

        string[] resultNames = GetFieldValues(results, "RESULT");
        int liveValueCount = resultNames.Count(IsLiveValueResultName);
        int unitResultCount = resultNames.Count(IsUnitResultName);

        return new JobInventoryItem(
            job,
            decision.Status,
            decision.Reason,
            arguments.Count,
            results.Count,
            liveValueCount,
            unitResultCount,
            comments,
            arguments,
            results,
            metadataErrors);
    }

    private static IReadOnlyList<MetadataRow> ReadMetadata(
        EdiabasNet ediabas,
        string virtualJob,
        string job,
        ICollection<string> metadataErrors)
    {
        try
        {
            List<Dictionary<string, EdiabasNet.ResultData>> resultSets =
                ExecuteVirtualJob(ediabas, virtualJob, job);
            int rowCount = Math.Max(0, resultSets.Count - 1);
            if (rowCount > MaximumRowsPerVirtualJob)
            {
                throw new InvalidOperationException(
                    $"Metadata row limit exceeded for {virtualJob}: {rowCount}.");
            }

            var rows = new List<MetadataRow>(rowCount);
            for (int index = 1; index < resultSets.Count; index++)
            {
                Dictionary<string, EdiabasNet.ResultData> source = resultSets[index];
                if (source.Count > MaximumFieldsPerRow)
                {
                    throw new InvalidOperationException(
                        $"Metadata field limit exceeded for {virtualJob}: {source.Count}.");
                }

                var fields = new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
                foreach ((string key, EdiabasNet.ResultData result) in source)
                {
                    fields[key] = FormatValue(result.OpData);
                }
                rows.Add(new MetadataRow(fields));
            }
            return rows;
        }
        catch (Exception exception)
        {
            RequireNoInterface(ediabas);
            metadataErrors.Add(
                $"{virtualJob}:{exception.GetType().Name}:{Sanitize(exception.Message)}");
            return [];
        }
    }

    private static ReviewDecision Classify(
        string job,
        IReadOnlyList<MetadataRow> comments,
        IReadOnlyList<MetadataRow> arguments,
        IReadOnlyList<MetadataRow> results,
        IReadOnlyCollection<string> metadataErrors)
    {
        if (string.Equals(
                job,
                "STATUS_MOTORDREHZAHL",
                StringComparison.Ordinal))
        {
            bool exactShape = arguments.Count == 0 &&
                              GetFieldValues(results, "RESULT").Contains(
                                  "STAT_MOTORDREHZAHL_WERT",
                                  StringComparer.Ordinal) &&
                              GetFieldValues(results, "RESULT").Contains(
                                  "STAT_MOTORDREHZAHL_EINH",
                                  StringComparer.Ordinal);
            if (exactShape && metadataErrors.Count == 0)
            {
                return new ReviewDecision(
                    ReviewStatus.Approved,
                    "Exact BMW-02C tuple; live RPM proof passed.");
            }
            return new ReviewDecision(
                ReviewStatus.Approved,
                "Pinned BMW-02C live proof remains authoritative for this exact " +
                "SGBD/backend hash; offline metadata shape was incomplete.");
        }

        if (metadataErrors.Count > 0)
        {
            return new ReviewDecision(
                ReviewStatus.NotAvailable,
                "Offline metadata is incomplete; dispatch remains blocked.");
        }

        string searchableText = BuildSearchableText(
            job,
            comments,
            arguments,
            results);
        string? riskToken = MutationRiskTokens.FirstOrDefault(
            token => searchableText.Contains(
                token,
                StringComparison.OrdinalIgnoreCase));
        if (riskToken is not null)
        {
            return new ReviewDecision(
                ReviewStatus.Excluded,
                $"Mutation/active-operation risk token: {riskToken}.");
        }

        string[] resultNames = GetFieldValues(results, "RESULT");
        if (resultNames.Length == 0)
        {
            return new ReviewDecision(
                ReviewStatus.NotAvailable,
                "No offline result metadata is available.");
        }

        int liveValueCount = resultNames.Count(IsLiveValueResultName);
        if (liveValueCount == 0)
        {
            return new ReviewDecision(
                ReviewStatus.Excluded,
                "No live-data-shaped value result; outside the v1 live-data scope.");
        }

        if (arguments.Count > 0)
        {
            return new ReviewDecision(
                ReviewStatus.Conditional,
                "Live-data-shaped results, but argument semantics require manual review.");
        }

        if (!LooksLikeReadJob(job))
        {
            return new ReviewDecision(
                ReviewStatus.Conditional,
                "Live-data-shaped results with ambiguous job naming; manual review required.");
        }

        return new ReviewDecision(
            ReviewStatus.Conditional,
            "No mutation evidence in offline metadata; bounded live proof still required.");
    }

    private static List<Dictionary<string, EdiabasNet.ResultData>> ExecuteVirtualJob(
        EdiabasNet ediabas,
        string jobName,
        string argument)
    {
        if (!AllowedVirtualJobs.Contains(jobName))
        {
            throw new InvalidOperationException(
                $"Virtual job is not allowlisted: {jobName}");
        }

        RequireNoInterface(ediabas);
        ediabas.ArgString = argument;
        ediabas.ArgBinaryStd = null;
        ediabas.ResultsRequests = string.Empty;
        ediabas.ExecuteJob(jobName);
        RequireNoInterface(ediabas);

        return ediabas.ResultSets ?? throw new InvalidOperationException(
            $"No result sets returned by {jobName}.");
    }

    private static string[] ExtractJobNames(
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> resultSets)
    {
        return resultSets
            .Skip(1)
            .Select(resultSet =>
                resultSet.TryGetValue(
                    "JOBNAME",
                    out EdiabasNet.ResultData? result)
                    ? result?.OpData as string
                    : null)
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(job => job, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetFieldValues(
        IEnumerable<MetadataRow> rows,
        string field)
    {
        return rows
            .Select(row => row.Fields.TryGetValue(field, out string? value)
                ? value
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsLiveValueResultName(string resultName)
    {
        if (string.Equals(resultName, "JOB_STATUS", StringComparison.Ordinal))
        {
            return false;
        }

        return resultName.EndsWith("_WERT", StringComparison.Ordinal) ||
               resultName.StartsWith("STAT_", StringComparison.Ordinal) ||
               resultName.StartsWith("STATUS_", StringComparison.Ordinal);
    }

    private static bool IsUnitResultName(string resultName) =>
        resultName.Contains("EINH", StringComparison.Ordinal) ||
        resultName.Contains("UNIT", StringComparison.Ordinal);

    private static bool LooksLikeReadJob(string job) =>
        job.StartsWith("STATUS", StringComparison.Ordinal) ||
        job.Contains("LESEN", StringComparison.Ordinal) ||
        job.Contains("ABFRAGE", StringComparison.Ordinal) ||
        job.Contains("ABFRAGEN", StringComparison.Ordinal);

    private static string BuildSearchableText(
        string job,
        params IReadOnlyList<MetadataRow>[] collections)
    {
        var text = new StringBuilder(job);
        foreach (IReadOnlyList<MetadataRow> rows in collections)
        {
            foreach (MetadataRow row in rows)
            {
                foreach ((string key, string value) in row.Fields)
                {
                    text.Append(' ').Append(key).Append(' ').Append(value);
                }
            }
        }
        return text.ToString();
    }

    private static void ValidateInventory(IReadOnlyList<JobInventoryItem> inventory)
    {
        if (inventory.Select(item => item.Job).Distinct(StringComparer.Ordinal).Count() !=
            inventory.Count)
        {
            throw new InvalidOperationException("Duplicate job names in inventory.");
        }

        JobInventoryItem rpm = inventory.Single(item =>
            item.Job == "STATUS_MOTORDREHZAHL");
        if (rpm.Status != ReviewStatus.Approved)
        {
            throw new InvalidOperationException(
                "The exact BMW-02C RPM tuple was not retained as approved.");
        }

        if (inventory.Count(item => item.Status == ReviewStatus.Approved) != 1)
        {
            throw new InvalidOperationException(
                "Offline inventory must not approve any unproven job.");
        }
    }

    private static void RunClassifierSelfTests()
    {
        var valueResult = new MetadataRow(
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["RESULT"] = "STAT_TEST_WERT",
                ["RESULTTYPE"] = "real"
            });
        ReviewDecision risky = Classify(
            "STEUERN_TEST",
            [],
            [],
            [valueResult],
            []);
        if (risky.Status != ReviewStatus.Excluded)
        {
            throw new InvalidOperationException(
                "Classifier self-test accepted an active job.");
        }

        ReviewDecision unknown = Classify(
            "STATUS_TESTWERT",
            [],
            [],
            [valueResult],
            []);
        if (unknown.Status != ReviewStatus.Conditional)
        {
            throw new InvalidOperationException(
                "Classifier self-test auto-approved an unproven read job.");
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void WriteCoverageCsv(
        string path,
        IEnumerable<JobInventoryItem> inventory)
    {
        var output = new StringBuilder();
        output.AppendLine(
            "job,status,reason,argument_count,result_count,live_value_count," +
            "unit_result_count,metadata_error_count");
        foreach (JobInventoryItem item in inventory)
        {
            AppendCsvRow(
                output,
                item.Job,
                ToWireValue(item.Status),
                item.Reason,
                item.ArgumentCount.ToString(CultureInfo.InvariantCulture),
                item.ResultCount.ToString(CultureInfo.InvariantCulture),
                item.LiveValueCount.ToString(CultureInfo.InvariantCulture),
                item.UnitResultCount.ToString(CultureInfo.InvariantCulture),
                item.MetadataErrors.Count.ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
    }

    private static void WriteReviewQueueCsv(
        string path,
        IEnumerable<JobInventoryItem> inventory)
    {
        var output = new StringBuilder();
        output.AppendLine(
            "job,status,reason,arguments,results,comments");
        foreach (JobInventoryItem item in inventory.Where(item =>
                     item.Status is ReviewStatus.Approved or ReviewStatus.Conditional))
        {
            AppendCsvRow(
                output,
                item.Job,
                ToWireValue(item.Status),
                item.Reason,
                FlattenRows(item.Arguments),
                FlattenRows(item.Results),
                FlattenRows(item.Comments));
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
    }

    private static string FlattenRows(IEnumerable<MetadataRow> rows) =>
        string.Join(
            " || ",
            rows.Select(row => string.Join(
                " | ",
                row.Fields.Select(pair => $"{pair.Key}={pair.Value}"))));

    private static void AppendCsvRow(StringBuilder output, params string[] fields)
    {
        output.AppendLine(string.Join(',', fields.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') &&
            !value.Contains('"') &&
            !value.Contains('\r') &&
            !value.Contains('\n'))
        {
            return value;
        }
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static void PrintSummary(
        IReadOnlyList<JobInventoryItem> inventory,
        string inventoryPath,
        string coveragePath,
        string reviewQueuePath,
        string allowlistPath,
        string assemblyPath,
        string prgPath)
    {
        Console.WriteLine("BMW-03 OFFLINE MS43 JOB/RESULT INVENTORY");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine("CLASSIFIER_SELF_TESTS=PASS");
        Console.WriteLine($"SGBD_SHA256={GetSha256(prgPath)}");
        Console.WriteLine($"ASSEMBLY_SHA256={GetSha256(assemblyPath)}");
        Console.WriteLine($"JOB_COUNT={inventory.Count}");
        foreach (ReviewStatus status in Enum.GetValues<ReviewStatus>())
        {
            int count = inventory.Count(item => item.Status == status);
            Console.WriteLine($"{ToWireValue(status).ToUpperInvariant()}={count}");
        }
        Console.WriteLine(
            $"METADATA_ERROR_JOBS={inventory.Count(item => item.MetadataErrors.Count > 0)}");
        Console.WriteLine($"INVENTORY={inventoryPath}");
        Console.WriteLine($"INVENTORY_SHA256={GetSha256(inventoryPath)}");
        Console.WriteLine($"COVERAGE={coveragePath}");
        Console.WriteLine($"REVIEW_QUEUE={reviewQueuePath}");
        Console.WriteLine($"REVIEW_QUEUE_SHA256={GetSha256(reviewQueuePath)}");
        Console.WriteLine($"ALLOWLIST={allowlistPath}");
        Console.WriteLine($"ALLOWLIST_SHA256={GetSha256(allowlistPath)}");
        Console.WriteLine("RESULT=PASS");
        Console.WriteLine(
            "NEXT=Upload ms430ds0-review-queue.csv and ms430ds0-job-inventory.json.");
    }

    private static string FormatValue(object? value)
    {
        string text = value switch
        {
            null => "<null>",
            byte[] bytes => Convert.ToHexString(
                bytes.AsSpan(0, Math.Min(bytes.Length, 256))),
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        text = Sanitize(text);
        return text.Length <= MaximumFieldCharacters
            ? text
            : text[..MaximumFieldCharacters] + "...[truncated]";
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string ToWireValue(ReviewStatus status) => status switch
    {
        ReviewStatus.Approved => "approved",
        ReviewStatus.Conditional => "conditional",
        ReviewStatus.Excluded => "excluded",
        ReviewStatus.NotAvailable => "not_available",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static void RequireNoInterface(EdiabasNet ediabas)
    {
        if (ediabas.EdInterfaceClass is not null)
        {
            throw new InvalidOperationException(
                "Offline safety invariant failed: an EDIABAS interface instance exists.");
        }
    }

    private static void RequireFileHash(
        string path,
        string expectedSha256,
        string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found.", path);
        }

        string actualSha256 = GetSha256(path);
        if (!string.Equals(
                actualSha256,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} SHA-256 mismatch. " +
                $"Expected {expectedSha256}; actual {actualSha256}.");
        }
    }

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed record ProbeOptions(string EcuPath, string OutputPath)
{
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        string ecuPath = @"C:\EDIABAS\ECU";
        string outputPath = Path.Combine("artifacts", "bmw03");

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        if (ecuPath.Contains(';'))
        {
            throw new ArgumentException(
                "The ECU directory must not contain a semicolon.");
        }

        return new ProbeOptions(
            Path.GetFullPath(ecuPath),
            Path.GetFullPath(outputPath));
    }
}

internal enum ReviewStatus
{
    Approved,
    Conditional,
    Excluded,
    NotAvailable
}

internal sealed record ReviewDecision(ReviewStatus Status, string Reason);

internal sealed record MetadataRow(
    IReadOnlyDictionary<string, string> Fields);

internal sealed record JobInventoryItem(
    string Job,
    ReviewStatus Status,
    string Reason,
    int ArgumentCount,
    int ResultCount,
    int LiveValueCount,
    int UnitResultCount,
    IReadOnlyList<MetadataRow> Comments,
    IReadOnlyList<MetadataRow> Arguments,
    IReadOnlyList<MetadataRow> Results,
    IReadOnlyList<string> MetadataErrors);

internal sealed record InventoryDocument(
    string SchemaVersion,
    string ClassificationVersion,
    DateTimeOffset GeneratedUtc,
    bool CommunicationAttempted,
    string InterfaceInstance,
    string SgbdFileName,
    string SgbdSha256,
    string BackendFileName,
    string BackendSha256,
    IReadOnlyList<JobInventoryItem> Jobs);

internal sealed record ApprovedAllowlistDocument(
    string SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string ProfileState,
    string SgbdFileName,
    string SgbdSha256,
    string BackendSha256,
    IReadOnlyList<ApprovedDispatch> Entries);

internal sealed record ApprovedDispatch(
    string ParameterId,
    string Job,
    string Argument,
    IReadOnlyList<string> ResultNames,
    string Evidence);
