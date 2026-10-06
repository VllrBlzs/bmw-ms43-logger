using System.Diagnostics;

namespace BmwMs43Logger.Core;

public enum JobReadQuality
{
    Complete,
    Timeout,
    Error
}

public sealed record JobReadBatch(
    Guid EpochId,
    long CycleId,
    int JobSequence,
    string Job,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    long StartedMonotonicTicks,
    long FinishedMonotonicTicks,
    JobReadQuality Quality,
    string? ErrorCode,
    IReadOnlyList<ParameterSample> Samples);

public sealed record SamplingRoute(
    ParameterId ParameterId,
    int JobSequence,
    string Job);

public sealed record NormalizedSample(
    Guid EpochId,
    long CycleId,
    ParameterId ParameterId,
    int? JobSequence,
    string? SourceJob,
    DateTimeOffset? JobStartedAtUtc,
    DateTimeOffset? JobFinishedAtUtc,
    DateTimeOffset? AcquiredAtUtc,
    long? MonotonicTicks,
    object? Value,
    string? Unit,
    SampleQuality Quality,
    string? ErrorCode,
    double? AcquisitionMs,
    double? LatencyMs,
    double? AgeAtCycleEndMs);

public sealed record NormalizedCycle(
    Guid EpochId,
    long CycleId,
    DateTimeOffset CompletedAtUtc,
    long CompletedMonotonicTicks,
    IReadOnlyList<NormalizedSample> Samples);

public static class SamplingEngine
{
    public static NormalizedCycle NormalizeCycle(
        Guid epochId,
        long cycleId,
        IReadOnlyCollection<SamplingRoute> routes,
        IReadOnlyCollection<JobReadBatch> jobBatches,
        DateTimeOffset completedAtUtc,
        long completedMonotonicTicks)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(jobBatches);
        if (routes.Count == 0)
        {
            throw new ArgumentException("At least one sampling route is required.", nameof(routes));
        }

        var routeIds = new HashSet<ParameterId>();
        foreach (SamplingRoute route in routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            if (!routeIds.Add(route.ParameterId))
            {
                throw new InvalidOperationException(
                    $"Duplicate sampling route: {route.ParameterId.Value}");
            }
        }

        JobReadBatch[] orderedJobs = jobBatches
            .OrderBy(batch => batch.JobSequence)
            .ToArray();
        ValidateJobs(
            epochId,
            cycleId,
            orderedJobs,
            completedAtUtc,
            completedMonotonicTicks);

        var jobsBySequence = orderedJobs.ToDictionary(batch => batch.JobSequence);
        var normalized = new List<NormalizedSample>(routes.Count);
        foreach (SamplingRoute route in routes)
        {
            if (!jobsBySequence.TryGetValue(
                    route.JobSequence,
                    out JobReadBatch? job))
            {
                normalized.Add(CreateUnavailable(
                    epochId,
                    cycleId,
                    route,
                    null,
                    SampleQuality.Missing,
                    "JOB_NOT_EXECUTED",
                    completedMonotonicTicks));
                continue;
            }
            if (!string.Equals(job.Job, route.Job, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Route/job mismatch for {route.ParameterId.Value}.");
            }
            if (job.Quality == JobReadQuality.Timeout)
            {
                normalized.Add(CreateUnavailable(
                    epochId,
                    cycleId,
                    route,
                    job,
                    SampleQuality.Timeout,
                    job.ErrorCode ?? "JOB_TIMEOUT",
                    completedMonotonicTicks));
                continue;
            }
            if (job.Quality == JobReadQuality.Error)
            {
                normalized.Add(CreateUnavailable(
                    epochId,
                    cycleId,
                    route,
                    job,
                    SampleQuality.Error,
                    job.ErrorCode ?? "JOB_ERROR",
                    completedMonotonicTicks));
                continue;
            }

            ParameterSample? sample = job.Samples.SingleOrDefault(item =>
                item.ParameterId == route.ParameterId);
            if (sample is null)
            {
                normalized.Add(CreateUnavailable(
                    epochId,
                    cycleId,
                    route,
                    job,
                    SampleQuality.Missing,
                    "RESULT_NOT_RETURNED",
                    completedMonotonicTicks));
                continue;
            }

            ValidateSample(job, sample, completedMonotonicTicks);
            normalized.Add(new NormalizedSample(
                epochId,
                cycleId,
                sample.ParameterId,
                job.JobSequence,
                job.Job,
                job.StartedAtUtc,
                job.FinishedAtUtc,
                sample.AcquiredAtUtc,
                sample.MonotonicTicks,
                sample.Value,
                sample.Unit,
                sample.Quality,
                sample.ErrorCode,
                ElapsedMilliseconds(
                    job.StartedMonotonicTicks,
                    job.FinishedMonotonicTicks),
                ElapsedMilliseconds(
                    job.StartedMonotonicTicks,
                    sample.MonotonicTicks),
                ElapsedMilliseconds(
                    sample.MonotonicTicks,
                    completedMonotonicTicks)));
        }

        return new NormalizedCycle(
            epochId,
            cycleId,
            completedAtUtc,
            completedMonotonicTicks,
            normalized);
    }

    private static void ValidateJobs(
        Guid epochId,
        long cycleId,
        IReadOnlyList<JobReadBatch> jobs,
        DateTimeOffset completedAtUtc,
        long completedMonotonicTicks)
    {
        var sequences = new HashSet<int>();
        long previousFinishedTicks = long.MinValue;
        DateTimeOffset? previousFinishedUtc = null;
        foreach (JobReadBatch job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job);
            if (job.EpochId != epochId || job.CycleId != cycleId)
            {
                throw new InvalidOperationException("Job belongs to another epoch or cycle.");
            }
            if (job.JobSequence < 1 || !sequences.Add(job.JobSequence))
            {
                throw new InvalidOperationException(
                    $"Invalid or duplicate job sequence: {job.JobSequence}");
            }
            if (job.FinishedMonotonicTicks < job.StartedMonotonicTicks ||
                job.FinishedAtUtc < job.StartedAtUtc)
            {
                throw new InvalidOperationException($"Negative job window: {job.Job}");
            }
            if (job.StartedMonotonicTicks < previousFinishedTicks ||
                (previousFinishedUtc.HasValue && job.StartedAtUtc < previousFinishedUtc.Value))
            {
                throw new InvalidOperationException(
                    "Job windows overlap; the acquisition worker must be serial.");
            }
            if (job.FinishedMonotonicTicks > completedMonotonicTicks ||
                job.FinishedAtUtc > completedAtUtc)
            {
                throw new InvalidOperationException("Job ends after the cycle completion time.");
            }
            if (job.Quality == JobReadQuality.Complete && job.ErrorCode is not null)
            {
                throw new InvalidOperationException(
                    $"Complete job contains an error code: {job.Job}");
            }
            if (job.Quality != JobReadQuality.Complete && job.Samples.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Failed job contains samples: {job.Job}");
            }
            previousFinishedTicks = job.FinishedMonotonicTicks;
            previousFinishedUtc = job.FinishedAtUtc;
        }
    }

    private static void ValidateSample(
        JobReadBatch job,
        ParameterSample sample,
        long completedMonotonicTicks)
    {
        if (sample.MonotonicTicks < job.StartedMonotonicTicks ||
            sample.MonotonicTicks > job.FinishedMonotonicTicks ||
            sample.MonotonicTicks > completedMonotonicTicks ||
            sample.AcquiredAtUtc < job.StartedAtUtc ||
            sample.AcquiredAtUtc > job.FinishedAtUtc)
        {
            throw new InvalidOperationException(
                $"Sample is outside its job window: {sample.ParameterId.Value}");
        }
        if (sample.Quality == SampleQuality.Valid && sample.Value is null)
        {
            throw new InvalidOperationException(
                $"Valid sample has no value: {sample.ParameterId.Value}");
        }
        if ((sample.Quality is SampleQuality.Missing or SampleQuality.Timeout or
             SampleQuality.Error) && sample.Value is not null)
        {
            throw new InvalidOperationException(
                $"Invalid sample contains a value: {sample.ParameterId.Value}");
        }
    }

    private static NormalizedSample CreateUnavailable(
        Guid epochId,
        long cycleId,
        SamplingRoute route,
        JobReadBatch? job,
        SampleQuality quality,
        string errorCode,
        long completedMonotonicTicks)
    {
        return new NormalizedSample(
            epochId,
            cycleId,
            route.ParameterId,
            job?.JobSequence ?? route.JobSequence,
            job?.Job ?? route.Job,
            job?.StartedAtUtc,
            job?.FinishedAtUtc,
            null,
            null,
            null,
            null,
            quality,
            errorCode,
            job is null
                ? null
                : ElapsedMilliseconds(
                    job.StartedMonotonicTicks,
                    job.FinishedMonotonicTicks),
            null,
            job is null
                ? null
                : ElapsedMilliseconds(
                    job.FinishedMonotonicTicks,
                    completedMonotonicTicks));
    }

    private static double ElapsedMilliseconds(long startedTicks, long finishedTicks)
    {
        if (finishedTicks < startedTicks)
        {
            throw new InvalidOperationException("Negative monotonic duration.");
        }
        return (finishedTicks - startedTicks) * 1000.0 / Stopwatch.Frequency;
    }
}

public sealed class BoundedDropOldestQueue<T> where T : notnull
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();

    public BoundedDropOldestQueue(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        Capacity = capacity;
    }

    public int Capacity { get; }
    public long DroppedCount { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool Enqueue(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            bool dropped = _items.Count == Capacity;
            if (dropped)
            {
                _items.Dequeue();
                DroppedCount++;
            }
            _items.Enqueue(item);
            return dropped;
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }
            item = _items.Dequeue();
            return true;
        }
    }
}
