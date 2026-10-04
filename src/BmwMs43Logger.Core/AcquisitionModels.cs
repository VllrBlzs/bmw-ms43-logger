namespace BmwMs43Logger.Core;

public enum SampleQuality
{
    Valid,
    Missing,
    Timeout,
    Stale,
    Error
}

public sealed record ParameterSample(
    ParameterId ParameterId,
    DateTimeOffset AcquiredAtUtc,
    long MonotonicTicks,
    object? Value,
    string? Unit,
    SampleQuality Quality,
    string? ErrorCode);

public sealed record AcquisitionBatch(
    Guid EpochId,
    long CycleId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<ParameterSample> Samples);
