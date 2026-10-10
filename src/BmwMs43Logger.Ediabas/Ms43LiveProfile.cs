using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.Ediabas;

public sealed record Ms43LiveRuntimeOptions(
    string EcuPath,
    string Port,
    string CatalogPath,
    string PackPath)
{
    public static Ms43LiveRuntimeOptions CreateDefault(
        string repositoryRoot,
        string ecuPath = @"C:\EDIABAS\ECU",
        string port = "COM5")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string root = Path.GetFullPath(repositoryRoot);
        return new Ms43LiveRuntimeOptions(
            Path.GetFullPath(ecuPath),
            port.ToUpperInvariant(),
            Path.Combine(root, "config", "ms430ds0-parameter-catalog.draft.json"),
            Path.Combine(root, "config", "ms430ds0-bounded-live-test-pack.json"));
    }
}

public sealed record Ms43LivePreflightReport(
    int CatalogEntries,
    int SelectableParameters,
    int AllowlistedJobs,
    string Port,
    string SgbdPath,
    string BackendPath,
    string SgbdSha256,
    string BackendSha256,
    string CatalogSemanticSha256,
    string PackSemanticSha256);

public static class Ms43ReadOnlyPreflight
{
    public static Ms43LivePreflightReport Run(
        Ms43LiveRuntimeOptions options,
        string backendAssemblyPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        Ms43LiveProfile profile = Ms43LiveProfile.Load(
            options.CatalogPath,
            options.PackPath);
        profile.RunGuardSelfTests();
        profile.ValidateRuntime(
            options.EcuPath,
            options.Port,
            backendAssemblyPath);
        PollingPlan plan = profile.BuildPlan(
            Ms43ParameterCatalog.ApprovedParameters);
        return new Ms43LivePreflightReport(
            profile.Catalog.Entries.Count,
            plan.SelectedParameterCount,
            profile.Pack.Entries.Count,
            options.Port,
            Path.Combine(options.EcuPath, Ms43LiveProfile.SgbdFileName),
            Path.GetFullPath(backendAssemblyPath),
            Ms43LiveProfile.ExpectedSgbdSha256,
            Ms43LiveProfile.ExpectedBackendSha256,
            Ms43LiveProfile.ExpectedCatalogSha256,
            Ms43LiveProfile.ExpectedPackSha256);
    }
}

internal sealed class Ms43LiveProfile
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

    private Ms43LiveProfile(
        LiveCatalogDocument catalog,
        LivePackDocument pack,
        IReadOnlyList<PollingParameterDefinition> definitions,
        IReadOnlyList<ReviewedJobDefinition> reviewedJobs)
    {
        Catalog = catalog;
        Pack = pack;
        Definitions = definitions;
        ReviewedJobs = reviewedJobs;
    }

    public LiveCatalogDocument Catalog { get; }
    public LivePackDocument Pack { get; }
    public IReadOnlyList<PollingParameterDefinition> Definitions { get; }
    public IReadOnlyList<ReviewedJobDefinition> ReviewedJobs { get; }

    public static Ms43LiveProfile Load(string catalogPath, string packPath)
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

        LiveCatalogDocument catalog =
            JsonSerializer.Deserialize<LiveCatalogDocument>(catalogText) ??
            throw new InvalidOperationException("Parameter catalog is empty.");
        LivePackDocument pack =
            JsonSerializer.Deserialize<LivePackDocument>(packText) ??
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
        return new Ms43LiveProfile(catalog, pack, definitions, reviewedJobs);
    }

    public PollingPlan BuildPlan(IEnumerable<ParameterId> selectedParameters)
    {
        ParameterId[] approved = Ms43ParameterCatalog
            .ValidateSessionSelection(selectedParameters)
            .ToArray();
        PollingPlan plan = PollingPlanner.Build(
            Definitions,
            ReviewedJobs,
            approved,
            PollingPlanMode.ControlledTest);
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
            LivePackEntry entry = Resolve(job.Job);
            if (!job.Job.StartsWith("STATUS_", StringComparison.Ordinal) ||
                job.Argument.Length != 0 ||
                job.Argument != entry.Argument ||
                !job.ResultNames.SequenceEqual(entry.ResultNames, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dispatch tuple is not exactly allowlisted: {job.Job}");
            }
        }
        foreach (PlannedParameterRoute route in plan.Routes)
        {
            LoggerParameterDefinition approved =
                Ms43ParameterCatalog.Resolve(route.ParameterId);
            PlannedJob job = plan.Jobs.Single(item =>
                item.Sequence == route.JobSequence);
            if (!approved.IsSelectable ||
                !string.Equals(approved.SourceJob, job.Job, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Route is not live-approved: {route.ParameterId.Value}");
            }
        }
    }

    public void RunGuardSelfTests()
    {
        PollingPlan all = BuildPlan(Ms43ParameterCatalog.ApprovedParameters);
        if (all.SelectedParameterCount != 22 ||
            all.Jobs.Count != 2 ||
            all.Jobs.Any(job => job.Argument.Length != 0))
        {
            throw new InvalidOperationException("Approved live plan shape changed.");
        }

        PollingPlan rpm = BuildPlan([new ParameterId("engine.rpm")]);
        PlannedJob job = rpm.Jobs.Single();
        ExpectRejected(() => EnsurePlan(rpm with
        {
            Jobs = [job with { Argument = "750" }]
        }));
        ExpectRejected(() => EnsurePlan(rpm with
        {
            Jobs = [job with { ResultNames = ["JOB_STATUS"] }]
        }));
        ExpectRejected(() => Resolve("STEUERN_LL_DREHZAHL_VERSTELLEN"));
        ExpectRejected(() => BuildPlan(
        [
            new ParameterId("coolant.outlet.temperature")
        ]));
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
        PollingPlan plan) =>
        new(
            sessionId,
            "bmw.e39.525i.m54b25.ms43.ms430ds0",
            ExpectedSgbdSha256,
            ExpectedBackendSha256,
            Catalog.CatalogVersion,
            ExpectedCatalogSha256,
            requestedHz,
            plan.Routes.Select(route => route.ParameterId).ToArray());

    private LivePackEntry Resolve(string job) =>
        Pack.Entries.SingleOrDefault(entry =>
            string.Equals(entry.Job, job, StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"Job is not allowlisted: {job}");

    private static void ValidateCatalog(LiveCatalogDocument catalog)
    {
        RequireEqual(catalog.SchemaVersion, "bmw04.parameter_catalog.v1", "catalog schema");
        RequireEqual(catalog.SgbdFile, SgbdFileName, "catalog SGBD file");
        RequireEqual(catalog.SgbdSha256, ExpectedSgbdSha256, "catalog SGBD hash");
        RequireEqual(catalog.BackendSha256, ExpectedBackendSha256, "catalog backend hash");
        RequireEqual(catalog.UnknownParameterPolicy, "reject", "unknown parameter policy");
        if (catalog.Entries.Count != 28)
        {
            throw new InvalidOperationException(
                $"Expected 28 catalog entries, found {catalog.Entries.Count}.");
        }

        var ids = new HashSet<ParameterId>();
        foreach (LiveCatalogEntry entry in catalog.Entries)
        {
            var id = new ParameterId(entry.ParameterId);
            if (!ids.Add(id) || entry.Source.Argument.Length != 0 ||
                string.IsNullOrWhiteSpace(entry.Source.Job) ||
                string.IsNullOrWhiteSpace(entry.Source.ValueResult) ||
                string.IsNullOrWhiteSpace(entry.Source.UnitResult))
            {
                throw new InvalidOperationException(
                    $"Invalid catalog tuple: {entry.ParameterId}");
            }
            RequireEqual(entry.DataType, "real", $"data type for {entry.ParameterId}");
        }
        RequireEqual(catalog.Entries[0].ParameterId, "engine.rpm", "first catalog entry");

        foreach (LoggerParameterDefinition approved in
                 Ms43ParameterCatalog.Entries.Where(entry => entry.IsSelectable))
        {
            LiveCatalogEntry source = catalog.Entries.Single(entry =>
                entry.ParameterId == approved.ParameterId.Value);
            RequireEqual(
                source.Source.Job,
                approved.SourceJob,
                $"source job for {approved.ParameterId.Value}");
        }
    }

    private static void ValidatePack(LivePackDocument pack)
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
            LivePackEntry entry = pack.Entries[index];
            RequireEqual(entry.Job, ExpectedJobs[index], $"pack job {index}");
            RequireEqual(entry.Argument, string.Empty, $"argument for {entry.Job}");
            if (!entry.Job.StartsWith("STATUS_", StringComparison.Ordinal) ||
                entry.ResultNames.Count < 2 ||
                entry.ResultNames[0] != "JOB_STATUS" ||
                entry.ResultNames.Distinct(StringComparer.Ordinal).Count() !=
                entry.ResultNames.Count)
            {
                throw new InvalidOperationException(
                    $"Invalid read-only allowlist entry: {entry.Job}");
            }
        }
    }

    private static void ValidateCrossReferences(
        LiveCatalogDocument catalog,
        LivePackDocument pack)
    {
        foreach (LiveCatalogEntry catalogEntry in catalog.Entries)
        {
            LivePackEntry packEntry = pack.Entries.SingleOrDefault(entry =>
                string.Equals(entry.Job, catalogEntry.Source.Job, StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
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
        RequireEqual(
            Convert.ToHexString(SHA256.HashData(stream)),
            expected,
            $"{description} SHA256");
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

internal sealed record LiveCatalogDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("catalog_version")] string CatalogVersion,
    [property: JsonPropertyName("catalog_state")] string CatalogState,
    [property: JsonPropertyName("sgbd_file")] string SgbdFile,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("unknown_parameter_policy")] string UnknownParameterPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<LiveCatalogEntry> Entries);

internal sealed record LiveCatalogEntry(
    [property: JsonPropertyName("parameter_id")] string ParameterId,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("unit")] LiveCatalogUnit Unit,
    [property: JsonPropertyName("support_status")] string SupportStatus,
    [property: JsonPropertyName("source")] LiveCatalogSource Source);

internal sealed record LiveCatalogUnit(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("source_result")] string SourceResult);

internal sealed record LiveCatalogSource(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("value_result")] string ValueResult,
    [property: JsonPropertyName("unit_result")] string UnitResult);

internal sealed record LivePackDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("profile_state")] string ProfileState,
    [property: JsonPropertyName("sgbd_file")] string SgbdFile,
    [property: JsonPropertyName("sgbd_sha256")] string SgbdSha256,
    [property: JsonPropertyName("backend_sha256")] string BackendSha256,
    [property: JsonPropertyName("port")] string Port,
    [property: JsonPropertyName("argument_policy")] string ArgumentPolicy,
    [property: JsonPropertyName("unknown_dispatch_policy")] string UnknownDispatchPolicy,
    [property: JsonPropertyName("entries")] IReadOnlyList<LivePackEntry> Entries);

internal sealed record LivePackEntry(
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("result_names")] IReadOnlyList<string> ResultNames,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("tier")] int Tier);
