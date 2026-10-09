namespace BmwMs43Logger.Core;

public sealed record LoggerParameterDefinition(
    ParameterId ParameterId,
    string InternalName,
    string DisplayNameHu,
    string Unit,
    string DataType,
    string SupportStatus,
    string SourceJob)
{
    public bool IsSelectable => string.Equals(
        SupportStatus,
        Ms43ParameterCatalog.ApprovedLiveObserved,
        StringComparison.Ordinal);

    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string term = query.Trim();
        return ParameterId.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               InternalName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               DisplayNameHu.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               Unit.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               SupportStatus.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ParameterSelectionSnapshot(
    IReadOnlyList<ParameterId> SelectedParameters,
    int SelectedCount,
    int ApprovedCount,
    int CatalogCount,
    bool IsLocked,
    long Revision);

public delegate void ParameterSelectionChangedEventHandler(
    object? sender,
    ParameterSelectionSnapshot snapshot);

public sealed record SamplingBudgetEstimate(
    int SelectedParameters,
    int PlannedJobs,
    int ResultRequests,
    double EstimatedCycleMilliseconds,
    double EstimatedMaximumHz,
    double RequestedHz,
    double EstimatedEffectiveHz,
    string ModelKind);

public static class Ms43ParameterCatalog
{
    public const string ApprovedLiveObserved = "approved_live_observed";
    public const string WithheldSemanticValidation =
        "withheld_semantic_validation";
    public const string WithheldDynamicValidation =
        "withheld_dynamic_validation";
    public const string WithheldNamingValidation =
        "withheld_naming_validation";

    public const string RpmJob = "STATUS_MOTORDREHZAHL";
    public const string BatchJob = "STATUS_MESSWERTEBLOCK";

    private static readonly LoggerParameterDefinition[] CatalogEntries =
    [
        Approved(
            "engine.rpm",
            "EngineRpm",
            "Motorfordulat",
            "1/min",
            RpmJob),
        Approved(
            "vehicle.speed",
            "VehicleSpeed",
            "Járműsebesség",
            "km/h"),
        Approved(
            "driver.pedal.angle",
            "DriverPedalAngle",
            "Gázpedál állás",
            "GradPWG"),
        Approved(
            "throttle.angle",
            "ThrottleAngle",
            "Fojtószelepállás",
            "GradDK"),
        Approved(
            "intake.air.mass",
            "IntakeAirMass",
            "Beszívott levegőtömeg",
            "kg/h"),
        Approved(
            "intake.air.temperature",
            "IntakeAirTemperature",
            "Szívólevegő-hőmérséklet",
            "GradC"),
        Approved(
            "engine.coolant.temperature",
            "EngineCoolantTemperature",
            "Motor-hűtőfolyadék hőmérséklet",
            "GradC"),
        Approved(
            "engine.oil.temperature",
            "EngineOilTemperature",
            "Motorolaj-hőmérséklet",
            "GradC"),
        Withheld(
            "coolant.outlet.temperature",
            "CoolantOutletTemperature",
            "Hűtőfolyadék kilépő hőmérséklet",
            "GradC",
            WithheldSemanticValidation),
        Withheld(
            "ignition.cylinder1.advance",
            "IgnitionCylinder1Advance",
            "Gyújtásszög, 1. henger",
            "GradKW",
            WithheldSemanticValidation),
        Approved(
            "fuel.injection.time",
            "FuelInjectionTime",
            "Befecskendezési idő",
            "ms"),
        Withheld(
            "idle.integrator",
            "IdleIntegrator",
            "Alapjárati integrátor",
            "%",
            WithheldSemanticValidation),
        Approved(
            "idle.control.duty",
            "IdleControlDuty",
            "Alapjárati szabályzó kitöltési tényező",
            "%"),
        Withheld(
            "vanos.intake.actual.angle",
            "VanosIntakeActualAngle",
            "Szívó VANOS tényleges szög",
            "GradKW",
            WithheldDynamicValidation),
        Withheld(
            "vanos.exhaust.actual.angle",
            "VanosExhaustActualAngle",
            "Kipufogó VANOS tényleges szög",
            "GradKW",
            WithheldDynamicValidation),
        Approved(
            "electrical.terminal15.voltage",
            "ElectricalTerminal15Voltage",
            "KL15 feszültség",
            "V"),
        Approved(
            "lambda.bank1.integrator",
            "LambdaBank1Integrator",
            "Lambda-integrátor, 1. bank",
            "-"),
        Approved(
            "lambda.bank2.integrator",
            "LambdaBank2Integrator",
            "Lambda-integrátor, 2. bank",
            "-"),
        Approved(
            "lambda.precat.bank1.heater.duty",
            "LambdaPrecatBank1HeaterDuty",
            "Első lambda fűtés, 1. bank",
            "%"),
        Approved(
            "lambda.precat.bank2.heater.duty",
            "LambdaPrecatBank2HeaterDuty",
            "Első lambda fűtés, 2. bank",
            "%"),
        Approved(
            "lambda.postcat.bank1.heater.duty",
            "LambdaPostcatBank1HeaterDuty",
            "Hátsó lambda fűtés, 1. bank",
            "%"),
        Approved(
            "lambda.postcat.bank2.heater.duty",
            "LambdaPostcatBank2HeaterDuty",
            "Hátsó lambda fűtés, 2. bank",
            "%"),
        Approved(
            "engine.load",
            "EngineLoad",
            "Motorterhelés",
            "mg/HUB"),
        Approved(
            "knock.cylinder2.mean.voltage",
            "KnockCylinder2MeanVoltage",
            "Kopogásjel átlaga, 2. henger",
            "V"),
        Approved(
            "knock.cylinder5.mean.voltage",
            "KnockCylinder5MeanVoltage",
            "Kopogásjel átlaga, 5. henger",
            "V"),
        Withheld(
            "cooling.fan.ac.request.stage",
            "CoolingFanAcRequestStage",
            "Elektromos ventilátor klímaigény-fokozat",
            "%",
            WithheldNamingValidation),
        Approved(
            "ambient.pressure",
            "AmbientPressure",
            "Környezeti légnyomás",
            "hPa"),
        Approved(
            "electrical.battery.voltage",
            "ElectricalBatteryVoltage",
            "Akkumulátorfeszültség",
            "V")
    ];

    private static readonly IReadOnlyList<LoggerParameterDefinition> ReadOnlyEntries =
        Array.AsReadOnly(CatalogEntries);

    private static readonly IReadOnlyList<ParameterId> ReadOnlyApproved =
        Array.AsReadOnly(CatalogEntries
            .Where(entry => entry.IsSelectable)
            .Select(entry => entry.ParameterId)
            .ToArray());

    private static readonly IReadOnlyList<ParameterId> ReadOnlyDefaultSelection =
        Array.AsReadOnly(new ParameterId[]
        {
            new ParameterId("engine.rpm"),
            new ParameterId("vehicle.speed"),
            new ParameterId("engine.coolant.temperature"),
            new ParameterId("electrical.battery.voltage")
        });

    private static readonly IReadOnlyDictionary<ParameterId, LoggerParameterDefinition>
        EntryById = CatalogEntries.ToDictionary(entry => entry.ParameterId);

    public static IReadOnlyList<LoggerParameterDefinition> Entries => ReadOnlyEntries;
    public static IReadOnlyList<ParameterId> ApprovedParameters => ReadOnlyApproved;
    public static IReadOnlyList<ParameterId> DefaultSelection => ReadOnlyDefaultSelection;

    public static LoggerParameterDefinition Resolve(ParameterId parameterId)
    {
        ArgumentNullException.ThrowIfNull(parameterId);
        return EntryById.TryGetValue(parameterId, out LoggerParameterDefinition? entry)
            ? entry
            : throw new InvalidOperationException(
                $"Unknown parameter ID: {parameterId.Value}");
    }

    public static IReadOnlyList<LoggerParameterDefinition> Search(string? query) =>
        CatalogEntries.Where(entry => entry.Matches(query)).ToArray();

    public static IReadOnlyList<ParameterId> ValidateSessionSelection(
        IEnumerable<ParameterId> parameterIds) =>
        NormalizeApprovedSelection(parameterIds, requireNonEmpty: true);

    internal static ParameterId[] NormalizeApprovedSelection(
        IEnumerable<ParameterId> parameterIds,
        bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(parameterIds);
        ParameterId[] requested = parameterIds.ToArray();
        if (requireNonEmpty && requested.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one approved parameter must be selected.");
        }

        var requestedSet = new HashSet<ParameterId>();
        foreach (ParameterId parameterId in requested)
        {
            ArgumentNullException.ThrowIfNull(parameterId);
            LoggerParameterDefinition entry = Resolve(parameterId);
            if (!entry.IsSelectable)
            {
                throw new InvalidOperationException(
                    $"Parameter is not approved for selection: {parameterId.Value}");
            }
            if (!requestedSet.Add(parameterId))
            {
                throw new InvalidOperationException(
                    $"Duplicate parameter selection: {parameterId.Value}");
            }
        }

        return CatalogEntries
            .Where(entry => requestedSet.Contains(entry.ParameterId))
            .Select(entry => entry.ParameterId)
            .ToArray();
    }

    private static LoggerParameterDefinition Approved(
        string id,
        string internalName,
        string displayNameHu,
        string unit,
        string sourceJob = BatchJob) =>
        Create(
            id,
            internalName,
            displayNameHu,
            unit,
            ApprovedLiveObserved,
            sourceJob);

    private static LoggerParameterDefinition Withheld(
        string id,
        string internalName,
        string displayNameHu,
        string unit,
        string supportStatus) =>
        Create(
            id,
            internalName,
            displayNameHu,
            unit,
            supportStatus,
            BatchJob);

    private static LoggerParameterDefinition Create(
        string id,
        string internalName,
        string displayNameHu,
        string unit,
        string supportStatus,
        string sourceJob) =>
        new(
            new ParameterId(id),
            internalName,
            displayNameHu,
            unit,
            "real",
            supportStatus,
            sourceJob);
}

public static class SamplingBudgetEstimator
{
    public const string ModelKind = "bmw05.simulated_virtual_cost_model.v1";

    public static SamplingBudgetEstimate Estimate(
        IEnumerable<ParameterId> parameterIds,
        double requestedHz)
    {
        if (!double.IsFinite(requestedHz) || requestedHz is < 0.2 or > 5.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedHz),
                "Requested Hz is bounded to 0.2-5.0.");
        }

        ParameterId[] selected = Ms43ParameterCatalog.NormalizeApprovedSelection(
            parameterIds,
            requireNonEmpty: false);
        if (selected.Length == 0)
        {
            return new SamplingBudgetEstimate(
                0,
                0,
                0,
                0.0,
                0.0,
                requestedHz,
                0.0,
                ModelKind);
        }

        bool includesRpm = selected.Any(parameterId => string.Equals(
            Ms43ParameterCatalog.Resolve(parameterId).SourceJob,
            Ms43ParameterCatalog.RpmJob,
            StringComparison.Ordinal));
        bool includesBatch = selected.Any(parameterId => string.Equals(
            Ms43ParameterCatalog.Resolve(parameterId).SourceJob,
            Ms43ParameterCatalog.BatchJob,
            StringComparison.Ordinal));

        int jobs = (includesRpm ? 1 : 0) + (includesBatch ? 1 : 0);
        int resultRequests = (includesRpm ? 4 : 0) + (includesBatch ? 57 : 0);
        double cycleMilliseconds = (includesRpm, includesBatch) switch
        {
            (true, false) => 191.0,
            (false, true) => 219.25,
            (true, true) => 410.25,
            _ => throw new InvalidOperationException("Unreachable sampling budget state.")
        };
        double maximumHz = 1000.0 / cycleMilliseconds;
        return new SamplingBudgetEstimate(
            selected.Length,
            jobs,
            resultRequests,
            cycleMilliseconds,
            maximumHz,
            requestedHz,
            Math.Min(requestedHz, maximumHz),
            ModelKind);
    }
}

public sealed class ParameterSelectionModel
{
    private readonly object _gate = new();
    private readonly HashSet<ParameterId> _selected;
    private bool _locked;
    private long _revision;

    public ParameterSelectionModel(IEnumerable<ParameterId>? initialSelection = null)
    {
        ParameterId[] initial = Ms43ParameterCatalog.NormalizeApprovedSelection(
            initialSelection ?? Ms43ParameterCatalog.DefaultSelection,
            requireNonEmpty: false);
        _selected = new HashSet<ParameterId>(initial);
    }

    public event ParameterSelectionChangedEventHandler? Changed;

    public ParameterSelectionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public void SetSelected(ParameterId parameterId, bool selected)
    {
        ArgumentNullException.ThrowIfNull(parameterId);
        ParameterSelectionSnapshot? changed = null;
        lock (_gate)
        {
            EnsureUnlocked();
            LoggerParameterDefinition entry = Ms43ParameterCatalog.Resolve(parameterId);
            if (selected && !entry.IsSelectable)
            {
                throw new InvalidOperationException(
                    $"Parameter is not selectable: {parameterId.Value}");
            }

            bool wasChanged = selected
                ? _selected.Add(parameterId)
                : _selected.Remove(parameterId);
            if (wasChanged)
            {
                _revision++;
                changed = CreateSnapshot();
            }
        }
        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    public void SelectAllApproved() =>
        ReplaceSelection(Ms43ParameterCatalog.ApprovedParameters);

    public void Clear() => ReplaceSelection(Array.Empty<ParameterId>());

    public IReadOnlyList<ParameterId> CaptureForSession()
    {
        ParameterSelectionSnapshot changed;
        lock (_gate)
        {
            EnsureUnlocked();
            if (_selected.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one approved parameter must be selected.");
            }
            _locked = true;
            _revision++;
            changed = CreateSnapshot();
        }
        Changed?.Invoke(this, changed);
        return changed.SelectedParameters;
    }

    public void ReleaseSession()
    {
        ParameterSelectionSnapshot? changed = null;
        lock (_gate)
        {
            if (_locked)
            {
                _locked = false;
                _revision++;
                changed = CreateSnapshot();
            }
        }
        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    private void ReplaceSelection(IEnumerable<ParameterId> parameterIds)
    {
        ParameterId[] normalized = Ms43ParameterCatalog.NormalizeApprovedSelection(
            parameterIds,
            requireNonEmpty: false);
        ParameterSelectionSnapshot? changed = null;
        lock (_gate)
        {
            EnsureUnlocked();
            var replacement = new HashSet<ParameterId>(normalized);
            if (!_selected.SetEquals(replacement))
            {
                _selected.Clear();
                _selected.UnionWith(replacement);
                _revision++;
                changed = CreateSnapshot();
            }
        }
        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    private void EnsureUnlocked()
    {
        if (_locked)
        {
            throw new InvalidOperationException(
                "Parameter selection is locked for the active session.");
        }
    }

    private ParameterSelectionSnapshot CreateSnapshot()
    {
        ParameterId[] ordered = Ms43ParameterCatalog.Entries
            .Where(entry => _selected.Contains(entry.ParameterId))
            .Select(entry => entry.ParameterId)
            .ToArray();
        return new ParameterSelectionSnapshot(
            Array.AsReadOnly(ordered),
            ordered.Length,
            Ms43ParameterCatalog.ApprovedParameters.Count,
            Ms43ParameterCatalog.Entries.Count,
            _locked,
            _revision);
    }
}
