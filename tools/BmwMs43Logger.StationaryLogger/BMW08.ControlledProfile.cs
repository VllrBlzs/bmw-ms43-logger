using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.StationaryLogger;

internal sealed class ControlledProfile
{
    public const string SgbdFileName = "ms430ds0.prg";
    public const string ExpectedSgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    public const string ExpectedBackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";
    public const string ExpectedPackSha256 =
        "AB953E495EEBB294C17E1C6735D5B173C70E28BC4F8A19B19D15323BF97B8D55";
    public const string ExpectedCatalogSha256 =
        "5ABF17EE1730546CE2FE4A4D950650E76B4EB7005AAF830068A413CF4903693C";

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

    private ControlledProfile(
        CatalogDocument catalog,
        PackDocument pack,
        IReadOnlyList<PollingParameterDefinition> definitions,
        IReadOnlyList<ReviewedJobDefinition> reviewedJobs)
    {
        Catalog = catalog;
        Pack = pack;
        Definitions = definitions;
        ReviewedJobs = reviewedJobs;
    }

    public CatalogDocument Catalog { get; }
    public PackDocument Pack { get; }
    public IReadOnlyList<PollingParameterDefinition> Definitions { get; }
    public IReadOnlyList<ReviewedJobDefinition> ReviewedJobs { get; }

    public static ControlledProfile Load(string catalogPath, string packPath)
    {
        string catalogText = ReadFile(catalogPath, "Parameter catalog");
        string packText = ReadFile(packPath, "Bounded live-test pack");
        RequireEqual(
            NormalizedTextSha256(catalogText),
            ExpectedCatalogSha256,
            "catalog semantic SHA256");
        RequireEqual(
            NormalizedTextSha256(packText),
            ExpectedPackSha256,
            "pack semantic SHA256");

        CatalogDocument catalog =
            JsonSerializer.Deserialize<CatalogDocument>(catalogText) ??
            throw new InvalidOperationException("Parameter catalog is empty.");
        PackDocument pack =
            JsonSerializer.Deserialize<PackDocument>(packText) ??
            throw new InvalidOperationException("Bounded live-test pack is empty.");
        ValidateCatalog(catalog);
        ValidatePack(pack);
        ValidateCrossReferences(catalog, pack);

        PollingParameterDefinition[] definitions = catalog.Entries
            .Select(entry => new PollingParameterDefinition(
                new ParameterId(entry.ParameterId),
                entry.Source.Job,
                entry.Source.Argument,
                entry.Source.ValueResult,
                entry.Source.UnitResult,
                entry.SupportStatus))
            .ToArray();
        ReviewedJobDefinition[] reviewedJobs = pack.Entries
            .Select(entry => new ReviewedJobDefinition(
                entry.Job,
                entry.Argument,
                entry.ResultNames))
            .ToArray();
        return new ControlledProfile(catalog, pack, definitions, reviewedJobs);
    }

    public PollingPlan BuildPlan(string selection)
    {
        IReadOnlyList<PollingParameterDefinition> selected;
        PollingPlanMode mode;
        switch (selection)
        {
            case "rpm_only":
                selected = Definitions.Take(1).ToArray();
                mode = PollingPlanMode.RuntimeApprovedOnly;
                break;
            case "core4":
                selected = Definitions.Take(4).ToArray();
                mode = PollingPlanMode.ControlledTest;
                break;
            case "batch8":
                selected = Definitions.Skip(1).Take(8).ToArray();
                mode = PollingPlanMode.ControlledTest;
                break;
            case "core16":
                selected = Definitions.Take(16).ToArray();
                mode = PollingPlanMode.ControlledTest;
                break;
            case "all28":
                selected = Definitions;
                mode = PollingPlanMode.ControlledTest;
                break;
            default:
                throw new InvalidOperationException($"Unknown selection: {selection}");
        }

        PollingPlan plan = PollingPlanner.Build(
            Definitions,
            ReviewedJobs,
            selected.Select(item => item.ParameterId).ToArray(),
            mode);
        EnsurePlan(plan);
        return plan;
    }

    public void EnsurePlan(PollingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(
                plan.ExecutionPolicy,
                PollingPlanner.SerialExecutionPolicy,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only one serial worker is permitted.");
        }
        foreach (PlannedJob job in plan.Jobs)
        {
            PackEntry entry = Resolve(job.Job);
            if (job.Argument != entry.Argument ||
                !job.ResultNames.SequenceEqual(entry.ResultNames, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dispatch tuple is not exactly allowlisted: {job.Job}");
            }
        }
    }

    public void RunGuardSelfTests()
    {
        foreach (string selection in StationaryOptions.ValidSelections)
        {
            EnsurePlan(BuildPlan(selection));
        }

        PollingPlan approved = BuildPlan("rpm_only");
        PlannedJob job = approved.Jobs.Single();
        ExpectRejected(() => EnsurePlan(approved with
        {
            Jobs = [job with { Argument = "750" }]
        }));
        ExpectRejected(() => EnsurePlan(approved with
        {
            Jobs = [job with { ResultNames = ["JOB_STATUS"] }]
        }));
        ExpectRejected(() => Resolve("STEUERN_LL_DREHZAHL_VERSTELLEN"));
    }

    public void ValidateRuntime(string ecuPath, string port, string backendPath)
    {
        if (!string.Equals(port, "COM5", StringComparison.Ordinal) ||
            !string.Equals(port, Pack.Port, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This controlled profile permits COM5 only, not {port}.");
        }
        if (ecuPath.Contains(';', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ECU directory must not contain a semicolon.");
        }
        RequireFileHash(
            Path.Combine(ecuPath, SgbdFileName),
            ExpectedSgbdSha256,
            "SGBD");
        RequireFileHash(
            backendPath,
            ExpectedBackendSha256,
            "EdiabasLib runtime assembly");
    }

    public SessionLogDescriptor CreateSessionDescriptor(
        string sessionId,
        double requestedHz,
        PollingPlan plan)
    {
        return new SessionLogDescriptor(
            sessionId,
            "bmw.e39.525i.m54b25.ms43.ms430ds0",
            ExpectedSgbdSha256,
            ExpectedBackendSha256,
            Catalog.CatalogVersion,
            ExpectedCatalogSha256,
            requestedHz,
            plan.Routes.Select(route => route.ParameterId).ToArray());
    }

    private PackEntry Resolve(string job)
    {
        return Pack.Entries.SingleOrDefault(entry =>
                   string.Equals(entry.Job, job, StringComparison.Ordinal)) ??
               throw new InvalidOperationException($"Job is not allowlisted: {job}");
    }

    private static void ValidateCatalog(CatalogDocument catalog)
    {
        RequireEqual(catalog.SchemaVersion, "bmw04.parameter_catalog.v1", "catalog schema");
        RequireEqual(
            catalog.CatalogState,
            "draft_pending_bounded_live_validation",
            "catalog state");
        RequireEqual(catalog.SgbdFile, SgbdFileName, "catalog SGBD file");
        RequireEqual(catalog.SgbdSha256, ExpectedSgbdSha256, "catalog SGBD hash");
        RequireEqual(
            catalog.BackendSha256,
            ExpectedBackendSha256,
            "catalog backend hash");
        RequireEqual(
            catalog.UnknownParameterPolicy,
            "reject",
            "unknown parameter policy");
        if (catalog.Entries.Count != 28)
        {
            throw new InvalidOperationException(
                $"Expected 28 catalog entries, found {catalog.Entries.Count}.");
        }

        var ids = new HashSet<ParameterId>();
        for (int index = 0; index < catalog.Entries.Count; index++)
        {
            CatalogEntry entry = catalog.Entries[index];
            var parameterId = new ParameterId(entry.ParameterId);
            if (!ids.Add(parameterId))
            {
                throw new InvalidOperationException(
                    $"Duplicate parameter ID: {entry.ParameterId}");
            }
            RequireEqual(entry.DataType, "real", $"data type for {entry.ParameterId}");
            RequireEqual(entry.Source.Argument, string.Empty, $"argument for {entry.ParameterId}");
            if (string.IsNullOrWhiteSpace(entry.Source.Job) ||
                string.IsNullOrWhiteSpace(entry.Source.ValueResult) ||
                string.IsNullOrWhiteSpace(entry.Source.UnitResult))
            {
                throw new InvalidOperationException(
                    $"Incomplete source tuple: {entry.ParameterId}");
            }
            RequireEqual(
                entry.Unit.SourceResult,
                entry.Source.UnitResult,
                $"unit source for {entry.ParameterId}");

            string expectedStatus = index == 0
                ? PollingPlanner.ApprovedStatus
                : "candidate_live_validation";
            RequireEqual(
                entry.SupportStatus,
                expectedStatus,
                $"support status for {entry.ParameterId}");
            if (index > 0)
            {
                RequireEqual(
                    entry.Unit.State,
                    "pending_runtime_observation",
                    $"unit state for {entry.ParameterId}");
                if (entry.Unit.Value is not null)
                {
                    throw new InvalidOperationException(
                        $"Pending unit must not be invented: {entry.ParameterId}");
                }
            }
        }
        RequireEqual(catalog.Entries[0].ParameterId, "engine.rpm", "first catalog entry");
        RequireEqual(catalog.Entries[0].Unit.State, "validated_live", "RPM unit state");
        RequireEqual(catalog.Entries[0].Unit.Value ?? string.Empty, "1/min", "RPM unit");
    }

    private static void ValidatePack(PackDocument pack)
    {
        RequireEqual(pack.SchemaVersion, "bmw03.bounded_live_test_pack.v1", "pack schema");
        RequireEqual(pack.ProfileState, "controlled_test_only", "pack state");
        RequireEqual(pack.SgbdFile, SgbdFileName, "pack SGBD file");
        RequireEqual(pack.SgbdSha256, ExpectedSgbdSha256, "pack SGBD hash");
        RequireEqual(pack.BackendSha256, ExpectedBackendSha256, "pack backend hash");
        RequireEqual(pack.Port, "COM5", "pack port");
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
            RequireEqual(entry.Job, ExpectedJobs[index], $"pack job {index}");
            RequireEqual(entry.Argument, string.Empty, $"argument for {entry.Job}");
            if (!entry.Job.StartsWith("STATUS_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Only pinned STATUS jobs are permitted: {entry.Job}");
            }
            RequireEqual(
                entry.ReviewState,
                index == 0 ? "validated" : "approved_for_bounded_live_test",
                $"review state for {entry.Job}");
            if (entry.Tier != (index == 0 ? 0 : 1))
            {
                throw new InvalidOperationException(
                    $"Unexpected test tier for {entry.Job}: {entry.Tier}");
            }
            if (entry.ResultNames.Count < 2 || entry.ResultNames[0] != "JOB_STATUS" ||
                entry.ResultNames.Distinct(StringComparer.Ordinal).Count() !=
                entry.ResultNames.Count)
            {
                throw new InvalidOperationException(
                    $"Invalid result allowlist: {entry.Job}");
            }
            if (entry.Job.StartsWith("STEUERN_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("START_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("STOP_", StringComparison.Ordinal) ||
                entry.Job.StartsWith("TEST_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"State-changing job entered the pack: {entry.Job}");
            }
        }
    }

    private static void ValidateCrossReferences(
        CatalogDocument catalog,
        PackDocument pack)
    {
        foreach (CatalogEntry catalogEntry in catalog.Entries)
        {
            PackEntry packEntry = pack.Entries.SingleOrDefault(entry =>
                string.Equals(
                    entry.Job,
                    catalogEntry.Source.Job,
                    StringComparison.Ordinal)) ?? throw new InvalidOperationException(
                        $"Catalog job is outside the pack: {catalogEntry.Source.Job}");
            if (packEntry.Argument != catalogEntry.Source.Argument ||
                !packEntry.ResultNames.Contains(
                    catalogEntry.Source.ValueResult,
                    StringComparer.Ordinal) ||
                !packEntry.ResultNames.Contains(
                    catalogEntry.Source.UnitResult,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Catalog tuple is not covered by the pack: {catalogEntry.ParameterId}");
            }
        }
    }

    private static string ReadFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found.", path);
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static string NormalizedTextSha256(string text)
    {
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void RequireFileHash(
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
        throw new InvalidOperationException("Negative dispatch guard test was accepted.");
    }
}

internal sealed record CatalogDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("catalog_version")] string CatalogVersion,
    [property: JsonPropertyName("catalog_state")] string CatalogState,
    [property: JsonPropertyName("sgbd_file")] string SgbdFile,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("unknown_parameter_policy")] string UnknownParameterPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<CatalogEntry> Entries);

internal sealed record CatalogEntry(
    [property: JsonPropertyName("parameter_id")] string ParameterId,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("unit")] CatalogUnit Unit,
    [property: JsonPropertyName("support_status")] string SupportStatus,
    [property: JsonPropertyName("source")] CatalogSource Source);

internal sealed record CatalogUnit(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("source_result")] string SourceResult);

internal sealed record CatalogSource(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("value_result")] string ValueResult,
    [property: JsonPropertyName("unit_result")] string UnitResult);

internal sealed record PackDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("profile_state")] string ProfileState,
    [property: JsonPropertyName("sgbd_file")] string SgbdFile,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
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
