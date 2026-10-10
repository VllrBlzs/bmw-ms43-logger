using System.Collections.ObjectModel;
using System.Globalization;

namespace BmwMs43Logger.Core;

public sealed record LivePlotPoint(
    long CycleId,
    DateTimeOffset PlotTimeUtc,
    bool UsesAcquisitionTimestamp,
    double? Value,
    SampleQuality Quality,
    string? ErrorCode);

public sealed record LivePlotSeries(
    ParameterId ParameterId,
    IReadOnlyList<LivePlotPoint> Points);

public sealed record LivePlotSnapshot(
    Guid? EpochId,
    DateTimeOffset? LatestCycleUtc,
    IReadOnlyList<LivePlotSeries> Series,
    long PublishedCycles,
    long SnapshotReads,
    TimeSpan TimeWindow,
    int MaximumPointsPerSeries);

/// <summary>
/// Receives the same normalized acquisition cycles as the CSV logger and keeps
/// a bounded, thread-safe plotting history. Snapshot reads never invoke polling.
/// </summary>
public sealed class LivePlotHistory
{
    public const int MaximumChannels = 5;
    public const int DefaultMaximumPointsPerSeries = 600;
    public static readonly TimeSpan DefaultTimeWindow = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private readonly TimeSpan _timeWindow;
    private readonly int _maximumPointsPerSeries;
    private ParameterId[] _channels = Array.Empty<ParameterId>();
    private Dictionary<ParameterId, Queue<LivePlotPoint>> _points = new();
    private Guid? _epochId;
    private DateTimeOffset? _latestCycleUtc;
    private long _latestCycleId;
    private long _publishedCycles;
    private long _snapshotReads;

    public LivePlotHistory(
        TimeSpan? timeWindow = null,
        int maximumPointsPerSeries = DefaultMaximumPointsPerSeries)
    {
        _timeWindow = timeWindow ?? DefaultTimeWindow;
        if (_timeWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeWindow));
        }
        if (maximumPointsPerSeries < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPointsPerSeries));
        }
        _maximumPointsPerSeries = maximumPointsPerSeries;
    }

    public long PublishedCycles
    {
        get
        {
            lock (_gate)
            {
                return _publishedCycles;
            }
        }
    }

    public long SnapshotReads
    {
        get
        {
            lock (_gate)
            {
                return _snapshotReads;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _channels = Array.Empty<ParameterId>();
            _points = new Dictionary<ParameterId, Queue<LivePlotPoint>>();
            _epochId = null;
            _latestCycleUtc = null;
            _latestCycleId = 0;
            _publishedCycles = 0;
            _snapshotReads = 0;
        }
    }

    public void Reset(IEnumerable<ParameterId> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ParameterId[] requested = channels.ToArray();
        if (requested.Length is < 1 or > MaximumChannels)
        {
            throw new InvalidOperationException(
                $"A live plot requires 1-{MaximumChannels} channels.");
        }

        var unique = new HashSet<ParameterId>();
        foreach (ParameterId parameterId in requested)
        {
            ArgumentNullException.ThrowIfNull(parameterId);
            LoggerParameterDefinition definition =
                Ms43ParameterCatalog.Resolve(parameterId);
            if (!definition.IsSelectable)
            {
                throw new InvalidOperationException(
                    $"Plot channel is not approved: {parameterId.Value}");
            }
            if (!IsNumericDataType(definition.DataType))
            {
                throw new InvalidOperationException(
                    $"Plot channel is not numeric: {parameterId.Value}");
            }
            if (!unique.Add(parameterId))
            {
                throw new InvalidOperationException(
                    $"Duplicate plot channel: {parameterId.Value}");
            }
        }

        ParameterId[] ordered = Ms43ParameterCatalog.Entries
            .Where(definition => unique.Contains(definition.ParameterId))
            .Select(definition => definition.ParameterId)
            .ToArray();
        var replacement = ordered.ToDictionary(
            parameterId => parameterId,
            _ => new Queue<LivePlotPoint>());

        lock (_gate)
        {
            _channels = ordered;
            _points = replacement;
            _epochId = null;
            _latestCycleUtc = null;
            _latestCycleId = 0;
            _publishedCycles = 0;
            _snapshotReads = 0;
        }
    }

    public void Publish(NormalizedCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        var samples = new Dictionary<ParameterId, NormalizedSample>();
        foreach (NormalizedSample sample in cycle.Samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            if (!samples.TryAdd(sample.ParameterId, sample))
            {
                throw new InvalidOperationException(
                    $"Duplicate plot sample: {sample.ParameterId.Value}");
            }
        }

        lock (_gate)
        {
            if (_channels.Length == 0)
            {
                throw new InvalidOperationException(
                    "Configure plot channels before publishing cycles.");
            }
            if (!_epochId.HasValue)
            {
                _epochId = cycle.EpochId;
            }
            else if (_epochId.Value != cycle.EpochId)
            {
                throw new InvalidOperationException(
                    "Reset plot history before publishing a new epoch.");
            }
            if (cycle.CycleId <= _latestCycleId)
            {
                throw new InvalidOperationException(
                    "Plot cycles must be published in increasing order.");
            }
            if (_latestCycleUtc.HasValue &&
                cycle.CompletedAtUtc < _latestCycleUtc.Value)
            {
                throw new InvalidOperationException(
                    "Plot cycle UTC time moved backwards.");
            }

            foreach (ParameterId parameterId in _channels)
            {
                LivePlotPoint point = samples.TryGetValue(
                    parameterId,
                    out NormalizedSample? sample)
                    ? CreatePoint(cycle, sample)
                    : new LivePlotPoint(
                        cycle.CycleId,
                        cycle.CompletedAtUtc,
                        false,
                        null,
                        SampleQuality.Missing,
                        "RESULT_NOT_RETURNED");
                Queue<LivePlotPoint> series = _points[parameterId];
                series.Enqueue(point);
                Trim(series, cycle.CompletedAtUtc);
            }

            _latestCycleId = cycle.CycleId;
            _latestCycleUtc = cycle.CompletedAtUtc;
            _publishedCycles++;
        }
    }

    public LivePlotSnapshot ReadSnapshot()
    {
        lock (_gate)
        {
            _snapshotReads++;
            LivePlotSeries[] series = _channels
                .Select(parameterId => new LivePlotSeries(
                    parameterId,
                    Array.AsReadOnly(_points[parameterId].ToArray())))
                .ToArray();
            return new LivePlotSnapshot(
                _epochId,
                _latestCycleUtc,
                Array.AsReadOnly(series),
                _publishedCycles,
                _snapshotReads,
                _timeWindow,
                _maximumPointsPerSeries);
        }
    }

    private void Trim(Queue<LivePlotPoint> series, DateTimeOffset latestCycleUtc)
    {
        DateTimeOffset cutoff = latestCycleUtc - _timeWindow;
        while (series.Count > _maximumPointsPerSeries ||
               (series.Count > 0 && series.Peek().PlotTimeUtc < cutoff))
        {
            series.Dequeue();
        }
    }

    private static LivePlotPoint CreatePoint(
        NormalizedCycle cycle,
        NormalizedSample sample)
    {
        if (sample.EpochId != cycle.EpochId || sample.CycleId != cycle.CycleId)
        {
            throw new InvalidOperationException(
                "Plot sample belongs to another epoch or cycle.");
        }

        DateTimeOffset plotTime = sample.AcquiredAtUtc ?? cycle.CompletedAtUtc;
        bool usesAcquisitionTime = sample.AcquiredAtUtc.HasValue;
        if (sample.Quality != SampleQuality.Valid)
        {
            return new LivePlotPoint(
                cycle.CycleId,
                plotTime,
                usesAcquisitionTime,
                null,
                sample.Quality,
                sample.ErrorCode);
        }
        if (!sample.AcquiredAtUtc.HasValue || sample.Value is null)
        {
            return new LivePlotPoint(
                cycle.CycleId,
                cycle.CompletedAtUtc,
                false,
                null,
                SampleQuality.Error,
                "INCOMPLETE_VALID_SAMPLE");
        }

        try
        {
            double value = Convert.ToDouble(
                sample.Value,
                CultureInfo.InvariantCulture);
            return double.IsFinite(value)
                ? new LivePlotPoint(
                    cycle.CycleId,
                    sample.AcquiredAtUtc.Value,
                    true,
                    value,
                    SampleQuality.Valid,
                    null)
                : new LivePlotPoint(
                    cycle.CycleId,
                    sample.AcquiredAtUtc.Value,
                    true,
                    null,
                    SampleQuality.Error,
                    "NON_FINITE_PLOT_VALUE");
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            return new LivePlotPoint(
                cycle.CycleId,
                sample.AcquiredAtUtc.Value,
                true,
                null,
                SampleQuality.Error,
                "NON_NUMERIC_PLOT_VALUE");
        }
    }

    private static bool IsNumericDataType(string dataType) =>
        dataType.Equals("real", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("integer", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("long", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("double", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase);
}

public static class LivePlotSegmenter
{
    public static IReadOnlyList<IReadOnlyList<LivePlotPoint>> CreateValidSegments(
        IReadOnlyList<LivePlotPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var segments = new List<IReadOnlyList<LivePlotPoint>>();
        var current = new List<LivePlotPoint>();
        foreach (LivePlotPoint point in points)
        {
            ArgumentNullException.ThrowIfNull(point);
            if (point.Quality == SampleQuality.Valid && point.Value.HasValue)
            {
                current.Add(point);
                continue;
            }
            Flush(current, segments);
        }
        Flush(current, segments);
        return new ReadOnlyCollection<IReadOnlyList<LivePlotPoint>>(segments);
    }

    private static void Flush(
        List<LivePlotPoint> current,
        List<IReadOnlyList<LivePlotPoint>> segments)
    {
        if (current.Count == 0)
        {
            return;
        }
        segments.Add(Array.AsReadOnly(current.ToArray()));
        current.Clear();
    }
}
