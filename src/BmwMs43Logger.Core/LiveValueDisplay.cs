using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

namespace BmwMs43Logger.Core;

public sealed record LiveValueObservation(
    Guid EpochId,
    long CycleId,
    ParameterId ParameterId,
    object? Value,
    string? Unit,
    SampleQuality SourceQuality,
    DateTimeOffset? AcquiredAtUtc,
    long? AcquiredAtMonotonicTicks,
    string? ErrorCode);

public sealed record LiveValueFrame(
    Guid EpochId,
    long CycleId,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyDictionary<string, LiveValueObservation> Values);

public sealed record LiveValuePresentation(
    ParameterId ParameterId,
    string ValueText,
    string UnitText,
    string AcquiredAtText,
    string AgeText,
    double? AgeMilliseconds,
    SampleQuality EffectiveQuality,
    string QualityText,
    string? ErrorCode);

/// <summary>
/// A thread-safe, latest-frame-only handoff between the acquisition worker and
/// the GUI. Reading this buffer never invokes acquisition and never queues an
/// additional ECU request.
/// </summary>
public sealed class LatestLiveValueBuffer
{
    private readonly object _gate = new();
    private LiveValueFrame? _latest;
    private long _publishedFrames;
    private long _snapshotReads;

    public long PublishedFrames
    {
        get
        {
            lock (_gate)
            {
                return _publishedFrames;
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

    public void Reset()
    {
        lock (_gate)
        {
            _latest = null;
            _publishedFrames = 0;
            _snapshotReads = 0;
        }
    }

    public void Publish(NormalizedCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (cycle.CycleId < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycle),
                "A live frame requires a positive cycle ID.");
        }

        var values = new Dictionary<string, LiveValueObservation>(
            StringComparer.Ordinal);
        foreach (NormalizedSample sample in cycle.Samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            if (sample.EpochId != cycle.EpochId || sample.CycleId != cycle.CycleId)
            {
                throw new InvalidOperationException(
                    "A live sample belongs to another epoch or cycle.");
            }
            ValidateSampleShape(sample);
            if (!values.TryAdd(
                    sample.ParameterId.Value,
                    new LiveValueObservation(
                        sample.EpochId,
                        sample.CycleId,
                        sample.ParameterId,
                        sample.Value,
                        sample.Unit,
                        sample.Quality,
                        sample.AcquiredAtUtc,
                        sample.MonotonicTicks,
                        sample.ErrorCode)))
            {
                throw new InvalidOperationException(
                    $"Duplicate live parameter: {sample.ParameterId.Value}");
            }
        }

        var frame = new LiveValueFrame(
            cycle.EpochId,
            cycle.CycleId,
            cycle.CompletedAtUtc,
            new ReadOnlyDictionary<string, LiveValueObservation>(values));

        lock (_gate)
        {
            if (_latest is not null)
            {
                if (_latest.EpochId != frame.EpochId)
                {
                    throw new InvalidOperationException(
                        "Reset the live-value buffer before publishing a new epoch.");
                }
                if (frame.CycleId <= _latest.CycleId)
                {
                    throw new InvalidOperationException(
                        "Live-value frames must be published in increasing cycle order.");
                }
            }
            _latest = frame;
            _publishedFrames++;
        }
    }

    public LiveValueFrame? ReadLatest()
    {
        lock (_gate)
        {
            _snapshotReads++;
            return _latest;
        }
    }

    private static void ValidateSampleShape(NormalizedSample sample)
    {
        if (sample.Quality == SampleQuality.Valid)
        {
            if (sample.Value is null ||
                !sample.AcquiredAtUtc.HasValue ||
                !sample.MonotonicTicks.HasValue)
            {
                throw new InvalidOperationException(
                    $"Valid live sample is incomplete: {sample.ParameterId.Value}");
            }
            if (sample.ErrorCode is not null)
            {
                throw new InvalidOperationException(
                    $"Valid live sample contains an error: {sample.ParameterId.Value}");
            }
            return;
        }

        if (sample.Quality is SampleQuality.Missing or
            SampleQuality.Timeout or SampleQuality.Error)
        {
            if (sample.Value is not null)
            {
                throw new InvalidOperationException(
                    $"Unavailable live sample contains a value: {sample.ParameterId.Value}");
            }
        }
    }
}

public static class LiveValuePresenter
{
    public const int GuiRefreshMilliseconds = 250;
    public const double MinimumStaleMilliseconds = 1500.0;

    public static TimeSpan StaleAfterForHz(double effectiveHz)
    {
        if (!double.IsFinite(effectiveHz) || effectiveHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveHz));
        }
        double milliseconds = Math.Max(
            MinimumStaleMilliseconds,
            3000.0 / effectiveHz);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public static LiveValuePresentation Project(
        LoggerParameterDefinition definition,
        LiveValueObservation? observation,
        long nowMonotonicTicks,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (nowMonotonicTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nowMonotonicTicks));
        }
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        if (observation is null)
        {
            return Unavailable(
                definition,
                SampleQuality.Missing,
                "NO_OBSERVATION");
        }
        if (observation.ParameterId != definition.ParameterId)
        {
            throw new InvalidOperationException(
                "The live observation does not match the parameter definition.");
        }

        double? ageMilliseconds = ComputeAgeMilliseconds(
            observation.AcquiredAtMonotonicTicks,
            nowMonotonicTicks);
        SampleQuality effectiveQuality = observation.SourceQuality;
        string? errorCode = observation.ErrorCode;

        if (observation.SourceQuality == SampleQuality.Valid)
        {
            if (observation.Value is null ||
                !observation.AcquiredAtUtc.HasValue ||
                !ageMilliseconds.HasValue)
            {
                effectiveQuality = SampleQuality.Error;
                errorCode = "INCOMPLETE_VALID_OBSERVATION";
            }
            else if (ageMilliseconds.Value > staleAfter.TotalMilliseconds)
            {
                effectiveQuality = SampleQuality.Stale;
                errorCode = "AGE_LIMIT_EXCEEDED";
            }
        }

        string valueText = "—";
        if ((effectiveQuality is SampleQuality.Valid or SampleQuality.Stale) &&
            observation.Value is not null)
        {
            if (!TryFormatValue(definition, observation.Value, out valueText))
            {
                effectiveQuality = SampleQuality.Error;
                errorCode = "VALUE_FORMAT_ERROR";
                valueText = "—";
            }
        }

        string unit = string.IsNullOrWhiteSpace(observation.Unit)
            ? definition.Unit
            : observation.Unit.Trim();
        string timestamp = observation.AcquiredAtUtc.HasValue
            ? observation.AcquiredAtUtc.Value.UtcDateTime.ToString(
                "HH:mm:ss.fff 'UTC'",
                CultureInfo.InvariantCulture)
            : "—";

        return new LiveValuePresentation(
            definition.ParameterId,
            valueText,
            unit,
            timestamp,
            FormatAge(ageMilliseconds),
            ageMilliseconds,
            effectiveQuality,
            QualityText(effectiveQuality),
            errorCode);
    }

    public static bool TryFormatValue(
        LoggerParameterDefinition definition,
        object value,
        out string formatted)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);

        if (value is bool boolean)
        {
            formatted = boolean ? "ON" : "OFF";
            return true;
        }
        if (value is string text)
        {
            formatted = string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();
            return true;
        }
        if (value is char character)
        {
            formatted = character.ToString();
            return true;
        }
        if (value is Enum enumeration)
        {
            formatted = enumeration.ToString();
            return true;
        }
        if (value is DateTimeOffset timestamp)
        {
            formatted = timestamp.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);
            return true;
        }

        try
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number))
            {
                formatted = "—";
                return false;
            }
            formatted = number.ToString(
                NumericFormat(definition),
                CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            formatted = "—";
            return false;
        }
    }

    private static LiveValuePresentation Unavailable(
        LoggerParameterDefinition definition,
        SampleQuality quality,
        string errorCode) =>
        new(
            definition.ParameterId,
            "—",
            definition.Unit,
            "—",
            "—",
            null,
            quality,
            QualityText(quality),
            errorCode);

    private static double? ComputeAgeMilliseconds(
        long? acquiredAtMonotonicTicks,
        long nowMonotonicTicks)
    {
        if (!acquiredAtMonotonicTicks.HasValue)
        {
            return null;
        }
        if (nowMonotonicTicks < acquiredAtMonotonicTicks.Value)
        {
            return null;
        }
        return (nowMonotonicTicks - acquiredAtMonotonicTicks.Value) *
            1000.0 / Stopwatch.Frequency;
    }

    private static string FormatAge(double? ageMilliseconds)
    {
        if (!ageMilliseconds.HasValue)
        {
            return "—";
        }
        return ageMilliseconds.Value < 1000.0
            ? $"{ageMilliseconds.Value:F0} ms"
            : $"{ageMilliseconds.Value / 1000.0:F2} s";
    }

    private static string QualityText(SampleQuality quality) => quality switch
    {
        SampleQuality.Valid => "valid",
        SampleQuality.Stale => "stale",
        SampleQuality.Missing => "missing",
        SampleQuality.Timeout => "timeout",
        SampleQuality.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(quality))
    };

    private static string NumericFormat(LoggerParameterDefinition definition) =>
        definition.Unit switch
        {
            "1/min" => "F0",
            "km/h" => "F1",
            "GradC" or "GradKW" or "GradPWG" or "GradDK" or "%" => "F1",
            "V" or "kg/h" => "F2",
            "ms" => "F3",
            "hPa" => "F0",
            "mg/HUB" => "F1",
            "-" => "F4",
            _ => "G6"
        };
}
