using System.Globalization;
using System.Text;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.StationaryLogger;

internal sealed class RunMetrics
{
    private readonly List<double> _cycleDurationsMs = [];
    private readonly Dictionary<string, List<double>> _jobDurationsMs =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _jobFailures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ParameterId, MutableParameterObservation> _parameters = [];
    private readonly SortedDictionary<string, long> _qualityCounts =
        new(StringComparer.Ordinal);

    public RunMetrics(IEnumerable<ParameterId> parameterIds)
    {
        foreach (string quality in Enum.GetNames<SampleQuality>())
        {
            _qualityCounts.Add(quality.ToLowerInvariant(), 0);
        }
        foreach (ParameterId parameterId in parameterIds)
        {
            _parameters.Add(parameterId, new MutableParameterObservation(parameterId));
        }
    }

    public long CyclesObserved { get; private set; }
    public long SamplesObserved { get; private set; }

    public void Observe(CycleExecution execution, NormalizedCycle cycle)
    {
        CyclesObserved++;
        _cycleDurationsMs.Add(ElapsedMilliseconds(
            execution.StartedMonotonicTicks,
            execution.CompletedMonotonicTicks));
        foreach (JobExecutionMetric metric in execution.JobMetrics)
        {
            if (!_jobDurationsMs.TryGetValue(metric.Job, out List<double>? durations))
            {
                durations = [];
                _jobDurationsMs.Add(metric.Job, durations);
                _jobFailures.Add(metric.Job, 0);
            }
            durations.Add(metric.DurationMs);
            if (metric.Status != "complete")
            {
                _jobFailures[metric.Job]++;
            }
        }

        foreach (NormalizedSample sample in cycle.Samples)
        {
            SamplesObserved++;
            string quality = sample.Quality.ToString().ToLowerInvariant();
            _qualityCounts.TryGetValue(quality, out long count);
            _qualityCounts[quality] = count + 1;
            if (_parameters.TryGetValue(
                    sample.ParameterId,
                    out MutableParameterObservation? observation))
            {
                observation.Observe(sample);
            }
        }
    }

    public AcceptanceReport BuildReport(
        StationaryOptions options,
        ControlledProfile profile,
        PollingPlan plan,
        SessionLogMetadata metadata,
        bool communicationAttempted,
        string interfaceInstance,
        bool comClosed,
        string? failureCode,
        string? failureMessage)
    {
        double? medianCycle = Percentile(_cycleDurationsMs, 0.50);
        double? p95Cycle = Percentile(_cycleDurationsMs, 0.95);
        return new AcceptanceReport(
            "bmw08.stationary_acceptance.v1",
            options.Mode.ToString().ToLowerInvariant(),
            options.VehicleState,
            options.Selection,
            communicationAttempted,
            interfaceInstance,
            comClosed,
            PollingPlanner.SerialExecutionPolicy,
            options.DurationSeconds,
            metadata.DurationSeconds,
            options.RequestedHz,
            metadata.EffectiveHz,
            plan.SelectedParameterCount,
            plan.Jobs.Count,
            plan.Jobs.Sum(job => job.ResultNames.Count),
            CyclesObserved,
            SamplesObserved,
            medianCycle,
            p95Cycle,
            medianCycle.HasValue && medianCycle.Value > 0
                ? 1000.0 / medianCycle.Value
                : null,
            new SortedDictionary<string, long>(_qualityCounts, StringComparer.Ordinal),
            metadata.QueueDropped,
            metadata.WriteErrors,
            profile.Catalog.CatalogVersion,
            ControlledProfile.ExpectedCatalogSha256,
            ControlledProfile.ExpectedPackSha256,
            ControlledProfile.ExpectedSgbdSha256,
            ControlledProfile.ExpectedBackendSha256,
            metadata.CsvFileName,
            metadata.MetadataFileName,
            metadata.CsvSha256,
            failureCode is null ? "PASS" : "FAIL",
            failureCode,
            failureMessage,
            BuildJobReports(),
            _parameters.Values
                .OrderBy(item => item.ParameterId.Value, StringComparer.Ordinal)
                .Select(item => item.ToReport())
                .ToArray());
    }

    private IReadOnlyList<JobTimingReport> BuildJobReports()
    {
        return _jobDurationsMs
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new JobTimingReport(
                item.Key,
                item.Value.Count,
                _jobFailures[item.Key],
                item.Value.Min(),
                RequiredPercentile(item.Value, 0.50),
                RequiredPercentile(item.Value, 0.95),
                item.Value.Max()))
            .ToArray();
    }

    private static double RequiredPercentile(
        IReadOnlyCollection<double> values,
        double percentile) => Percentile(values, percentile) ??
        throw new InvalidOperationException("Timing collection is empty.");

    private static double? Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }
        double[] ordered = values.Order().ToArray();
        int index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static double ElapsedMilliseconds(long startedTicks, long finishedTicks) =>
        (finishedTicks - startedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

internal sealed class MutableParameterObservation
{
    private readonly SortedDictionary<string, long> _qualityCounts =
        new(StringComparer.Ordinal);
    private readonly SortedSet<string> _units = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _valueKinds = new(StringComparer.Ordinal);
    private string? _firstValue;
    private string? _lastValue;
    private double? _numericMinimum;
    private double? _numericMaximum;

    public MutableParameterObservation(ParameterId parameterId)
    {
        ParameterId = parameterId;
        foreach (string quality in Enum.GetNames<SampleQuality>())
        {
            _qualityCounts.Add(quality.ToLowerInvariant(), 0);
        }
    }

    public ParameterId ParameterId { get; }
    public long Samples { get; private set; }
    public long ValuesPresent { get; private set; }

    public void Observe(NormalizedSample sample)
    {
        Samples++;
        string quality = sample.Quality.ToString().ToLowerInvariant();
        _qualityCounts.TryGetValue(quality, out long count);
        _qualityCounts[quality] = count + 1;
        if (sample.Value is not null)
        {
            ValuesPresent++;
            _valueKinds.Add(sample.Value.GetType().Name);
            string formatted = FormatValue(sample.Value);
            _firstValue ??= formatted;
            _lastValue = formatted;
            if (TryGetDouble(sample.Value, out double numeric))
            {
                _numericMinimum = !_numericMinimum.HasValue
                    ? numeric
                    : Math.Min(_numericMinimum.Value, numeric);
                _numericMaximum = !_numericMaximum.HasValue
                    ? numeric
                    : Math.Max(_numericMaximum.Value, numeric);
            }
        }
        if (!string.IsNullOrWhiteSpace(sample.Unit))
        {
            _units.Add(sample.Unit);
        }
    }

    public ParameterObservationReport ToReport() => new(
        ParameterId.Value,
        Samples,
        ValuesPresent,
        new SortedDictionary<string, long>(_qualityCounts, StringComparer.Ordinal),
        _units.ToArray(),
        _valueKinds.ToArray(),
        _firstValue,
        _lastValue,
        _numericMinimum,
        _numericMaximum);

    private static string FormatValue(object value) => value switch
    {
        IFormattable formattable =>
            formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static bool TryGetDouble(object value, out double numeric)
    {
        switch (value)
        {
            case byte byteValue:
                numeric = byteValue;
                return true;
            case sbyte signedByteValue:
                numeric = signedByteValue;
                return true;
            case short shortValue:
                numeric = shortValue;
                return true;
            case ushort unsignedShortValue:
                numeric = unsignedShortValue;
                return true;
            case int intValue:
                numeric = intValue;
                return true;
            case uint unsignedIntValue:
                numeric = unsignedIntValue;
                return true;
            case long longValue:
                numeric = longValue;
                return true;
            case ulong unsignedLongValue:
                numeric = unsignedLongValue;
                return true;
            case float floatValue when float.IsFinite(floatValue):
                numeric = floatValue;
                return true;
            case double doubleValue when double.IsFinite(doubleValue):
                numeric = doubleValue;
                return true;
            case decimal decimalValue:
                numeric = (double)decimalValue;
                return true;
            default:
                numeric = default;
                return false;
        }
    }
}

internal static class AcceptanceReportWriter
{
    public static string Write(
        string path,
        AcceptanceReport report)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        File.WriteAllText(
            path,
            json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(path);
    }
}

internal sealed record AcceptanceReport(
    string SchemaVersion,
    string Mode,
    string VehicleState,
    string Selection,
    bool CommunicationAttempted,
    string InterfaceInstance,
    bool ComClosed,
    string ExecutionPolicy,
    int RequestedDurationSeconds,
    double ActualDurationSeconds,
    double RequestedHz,
    double? EffectiveHz,
    int PlannedChannels,
    int PlannedJobsPerCycle,
    int ResultRequestsPerCycle,
    long CyclesObserved,
    long SamplesObserved,
    double? CycleMedianMs,
    double? CycleP95Ms,
    double? MedianBasedHz,
    IReadOnlyDictionary<string, long> QualityCounts,
    long QueueDropped,
    long WriteErrors,
    string CatalogVersion,
    string CatalogSha256,
    string PackSha256,
    string SgbdSha256,
    string BackendSha256,
    string CsvFileName,
    string MetadataFileName,
    string? CsvSha256,
    string Result,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<JobTimingReport> Jobs,
    IReadOnlyList<ParameterObservationReport> Parameters);

internal sealed record JobTimingReport(
    string Job,
    int Attempts,
    long Failures,
    double MinimumMs,
    double MedianMs,
    double P95Ms,
    double MaximumMs);

internal sealed record ParameterObservationReport(
    string ParameterId,
    long Samples,
    long ValuesPresent,
    IReadOnlyDictionary<string, long> QualityCounts,
    IReadOnlyList<string> ObservedUnits,
    IReadOnlyList<string> ValueKinds,
    string? FirstValue,
    string? LastValue,
    double? NumericMinimum,
    double? NumericMaximum);
