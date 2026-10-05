using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EdiabasLib;

namespace BmwMs43Logger.MetadataProbe;

internal static class Program
{
    private const string PrgFileName = "ms430ds0.prg";
    private const string ExpectedPrgSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    private const string ExpectedAssemblySha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    private const int MaximumJobRecords = 4096;
    private const int MaximumCandidates = 24;
    private const int MaximumRowsPerVirtualJob = 128;
    private const int MaximumReportCharacters = 64_000;

    private static readonly HashSet<string> AllowedVirtualJobs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "_JOBS",
            "_JOBCOMMENTS",
            "_ARGUMENTS",
            "_RESULTS"
        };

    private static readonly string[] PrimaryCandidateTokens =
    {
        "DREHZAHL",
        "DREH",
        "RPM",
        "NMOT",
        "N_MOTOR",
        "IDENTIFIKATION"
    };

    public static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Run(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            throw new ArgumentException(
                "Expected arguments: <ECU directory> <output directory>.");
        }

        string ecuPath = Path.GetFullPath(args[0]);
        string outputPath = Path.GetFullPath(args[1]);
        if (ecuPath.Contains(';'))
        {
            throw new ArgumentException("The ECU directory must not contain a semicolon.");
        }

        string prgPath = Path.Combine(ecuPath, PrgFileName);
        RequireFileHash(prgPath, ExpectedPrgSha256, "SGBD");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Assembly assembly = typeof(EdiabasNet).Assembly;
        string assemblyPath = assembly.Location;
        RequireFileHash(assemblyPath, ExpectedAssemblySha256, "EdiabasLib runtime assembly");

        string config = string.Join(
            ';',
            $"EcuPath={ecuPath}",
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

        List<Dictionary<string, EdiabasNet.ResultData>> jobSets =
            ExecuteVirtualJob(ediabas, "_JOBS", "ALL");
        string[] allJobs = ExtractJobNames(jobSets);
        if (allJobs.Length > MaximumJobRecords)
        {
            throw new InvalidOperationException(
                $"Offline job record limit exceeded: {allJobs.Length}.");
        }

        string[] candidates = SelectRpmCandidates(allJobs);

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No RPM-like job name was found among {allJobs.Length} offline job records.");
        }

        Directory.CreateDirectory(outputPath);
        string reportPath = Path.Combine(outputPath, "ms430ds0-rpm-metadata.txt");
        var report = new StringBuilder();
        report.AppendLine("BMW-02A OFFLINE MS43 METADATA PROBE");
        report.AppendLine($"generated_utc={DateTimeOffset.UtcNow:O}");
        report.AppendLine("communication_attempted=false");
        report.AppendLine("interface_instance=none");
        report.AppendLine("no_init_for_virtual_jobs=true");
        report.AppendLine("allowed_virtual_jobs=_JOBS,_JOBCOMMENTS,_ARGUMENTS,_RESULTS");
        report.AppendLine($"assembly_path={assemblyPath}");
        report.AppendLine($"assembly_sha256={GetSha256(assemblyPath)}");
        report.AppendLine($"sgbd_path={prgPath}");
        report.AppendLine($"sgbd_sha256={GetSha256(prgPath)}");
        report.AppendLine($"job_count={allJobs.Length.ToString(CultureInfo.InvariantCulture)}");
        report.AppendLine($"rpm_candidate_count={candidates.Length.ToString(CultureInfo.InvariantCulture)}");

        foreach (string candidate in candidates)
        {
            report.AppendLine();
            report.AppendLine($"[JOB {candidate}]");
            AppendVirtualJob(report, ediabas, "_JOBCOMMENTS", candidate);
            AppendVirtualJob(report, ediabas, "_ARGUMENTS", candidate);
            AppendVirtualJob(report, ediabas, "_RESULTS", candidate);
        }

        RequireReportWithinLimit(report);
        RequireNoInterface(ediabas);
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        Console.Write(report.ToString());
        Console.WriteLine();
        Console.WriteLine($"REPORT={reportPath}");
        Console.WriteLine("NEXT=Paste this bounded console output back into the project chat.");
    }

    private static List<Dictionary<string, EdiabasNet.ResultData>> ExecuteVirtualJob(
        EdiabasNet ediabas,
        string jobName,
        string argument)
    {
        if (!AllowedVirtualJobs.Contains(jobName))
        {
            throw new InvalidOperationException($"Virtual job is not allowlisted: {jobName}");
        }

        RequireNoInterface(ediabas);
        ediabas.ArgString = argument;
        ediabas.ArgBinaryStd = null;
        ediabas.ResultsRequests = string.Empty;
        ediabas.ExecuteJob(jobName);
        RequireNoInterface(ediabas);

        return ediabas.ResultSets
            ?? throw new InvalidOperationException($"No result sets returned by {jobName}.");
    }

    private static string[] ExtractJobNames(
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> resultSets)
    {
        return resultSets
            .Skip(1)
            .Select(resultSet =>
                resultSet.TryGetValue("JOBNAME", out EdiabasNet.ResultData? result)
                    ? result?.OpData as string
                    : null)
            .Where(jobName => !string.IsNullOrWhiteSpace(jobName))
            .Select(jobName => jobName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(jobName => jobName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SelectRpmCandidates(IEnumerable<string> allJobs)
    {
        string[] candidates = allJobs
            .Where(jobName => PrimaryCandidateTokens.Any(
                token => jobName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Take(MaximumCandidates + 1)
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = allJobs
                .Where(jobName =>
                    jobName.Contains("STATUS", StringComparison.OrdinalIgnoreCase) &&
                    jobName.Contains("MOTOR", StringComparison.OrdinalIgnoreCase))
                .Take(MaximumCandidates + 1)
                .ToArray();
        }

        if (candidates.Length > MaximumCandidates)
        {
            throw new InvalidOperationException(
                $"RPM candidate limit exceeded ({MaximumCandidates}); filter review required.");
        }

        return candidates;
    }

    private static void AppendVirtualJob(
        StringBuilder report,
        EdiabasNet ediabas,
        string virtualJob,
        string candidate)
    {
        List<Dictionary<string, EdiabasNet.ResultData>> resultSets =
            ExecuteVirtualJob(ediabas, virtualJob, candidate);

        int metadataRows = Math.Max(0, resultSets.Count - 1);
        if (metadataRows > MaximumRowsPerVirtualJob)
        {
            throw new InvalidOperationException(
                $"Metadata row limit exceeded for {virtualJob}: {metadataRows}.");
        }

        report.AppendLine($"[{virtualJob}]");
        if (metadataRows == 0)
        {
            report.AppendLine("<none>");
            return;
        }

        for (int index = 1; index < resultSets.Count; index++)
        {
            string row = string.Join(
                " | ",
                resultSets[index]
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={FormatValue(pair.Value.OpData)}"));
            report.AppendLine(row);
            RequireReportWithinLimit(report);
        }
    }

    private static string FormatValue(object? value)
    {
        string text = value switch
        {
            null => "<null>",
            byte[] bytes => FormatBytes(bytes),
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 512 ? text : text[..512] + "...[truncated]";
    }

    private static string FormatBytes(byte[] bytes)
    {
        const int maximumBytes = 128;
        int bytesToFormat = Math.Min(bytes.Length, maximumBytes);
        string text = Convert.ToHexString(bytes.AsSpan(0, bytesToFormat));
        return bytes.Length <= maximumBytes ? text : text + "...[truncated]";
    }

    private static void RequireReportWithinLimit(StringBuilder report)
    {
        if (report.Length > MaximumReportCharacters)
        {
            throw new InvalidOperationException(
                $"Report limit exceeded: {MaximumReportCharacters} characters.");
        }
    }

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
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} SHA-256 mismatch. Expected {expectedSha256}; actual {actualSha256}.");
        }
    }

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
