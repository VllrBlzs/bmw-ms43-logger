using System.Diagnostics;
using System.Globalization;
using BmwMs43Logger.Core;
using EdiabasLib;

namespace BmwMs43Logger.StationaryLogger;

internal sealed record JobExecutionMetric(
    int Sequence,
    string Job,
    double DurationMs,
    string Status,
    string? ErrorCode);

internal sealed record CycleExecution(
    Guid EpochId,
    long CycleId,
    DateTimeOffset StartedAtUtc,
    long StartedMonotonicTicks,
    DateTimeOffset CompletedAtUtc,
    long CompletedMonotonicTicks,
    IReadOnlyList<JobReadBatch> JobBatches,
    IReadOnlyList<JobExecutionMetric> JobMetrics,
    string? FailureCode,
    string? FailureMessage);

internal interface ICycleSource : IDisposable
{
    bool CommunicationAttempted { get; }
    string InterfaceInstance { get; }
    CycleExecution ReadCycle(Guid epochId, long cycleId, PollingPlan plan);
}

internal sealed class EdiabasCycleSource : ICycleSource
{
    private readonly EdiabasNet _ediabas;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;

    public EdiabasCycleSource(
        StationaryOptions options,
        ControlledProfile profile,
        PollingPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        profile.EnsurePlan(plan);
        _cancellationToken = cancellationToken;

        string config = string.Join(
            ';',
            $"EcuPath={options.EcuPath}",
            $"ObdComPort={options.Port}",
            "Interface=STD:OBD",
            "ApiTrace=0",
            "IfhTrace=0",
            "TraceBuffering=0",
            "IfhTraceBuffering=0",
            "RetryComm=1",
            "SystemResults=1");
        var obd = new EdInterfaceObd
        {
            ComPort = options.Port,
            IfhName = "STD:OBD",
            ApplicationName = "BmwMs43Logger.StationaryLogger"
        };
        EdiabasNet? ediabas = null;
        try
        {
            ediabas = new EdiabasNet(config, isHosted: true)
            {
                EdInterfaceClass = obd,
                SgbdFileName = ControlledProfile.SgbdFileName
            };
            ediabas.AbortJobFunc = () => _cancellationToken.IsCancellationRequested;
            _ediabas = ediabas;
        }
        catch
        {
            if (ediabas is not null)
            {
                ediabas.Dispose();
            }
            else
            {
                obd.Dispose();
            }
            throw;
        }
    }

    public bool CommunicationAttempted => true;
    public string InterfaceInstance => nameof(EdInterfaceObd);

    public CycleExecution ReadCycle(Guid epochId, long cycleId, PollingPlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset cycleStartedAtUtc = DateTimeOffset.UtcNow;
        long cycleStartedTicks = Stopwatch.GetTimestamp();
        var batches = new List<JobReadBatch>(plan.Jobs.Count);
        var metrics = new List<JobExecutionMetric>(plan.Jobs.Count);
        string? failureCode = null;
        string? failureMessage = null;

        foreach (PlannedJob job in plan.Jobs.OrderBy(item => item.Sequence))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset jobStartedAtUtc = DateTimeOffset.UtcNow;
            long jobStartedTicks = Stopwatch.GetTimestamp();
            try
            {
                _ediabas.ArgString = job.Argument;
                _ediabas.ArgBinaryStd = null;
                _ediabas.ResultsRequests = string.Join(';', job.ResultNames);
                _ediabas.ExecuteJob(job.Job);

                DateTimeOffset jobFinishedAtUtc = DateTimeOffset.UtcNow;
                long jobFinishedTicks = Stopwatch.GetTimestamp();
                EnsureJobOkay(job.Job);
                IReadOnlyList<ParameterSample> samples = CaptureSamples(
                    plan,
                    job,
                    jobFinishedAtUtc,
                    jobFinishedTicks);
                batches.Add(new JobReadBatch(
                    epochId,
                    cycleId,
                    job.Sequence,
                    job.Job,
                    jobStartedAtUtc,
                    jobFinishedAtUtc,
                    jobStartedTicks,
                    jobFinishedTicks,
                    JobReadQuality.Complete,
                    null,
                    samples));
                metrics.Add(new JobExecutionMetric(
                    job.Sequence,
                    job.Job,
                    ElapsedMilliseconds(jobStartedTicks, jobFinishedTicks),
                    "complete",
                    null));
            }
            catch (Exception exception)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(_cancellationToken);
                }

                DateTimeOffset jobFinishedAtUtc = DateTimeOffset.UtcNow;
                long jobFinishedTicks = Stopwatch.GetTimestamp();
                bool timeout = IsTimeout(exception);
                failureCode = timeout ? "EDIABAS_TIMEOUT" : "EDIABAS_ERROR";
                failureMessage =
                    $"{job.Job} failed; EDIABAS_CODE={_ediabas.ErrorCodeLast}; " +
                    $"EXCEPTION={exception.GetType().Name}.";
                batches.Add(new JobReadBatch(
                    epochId,
                    cycleId,
                    job.Sequence,
                    job.Job,
                    jobStartedAtUtc,
                    jobFinishedAtUtc,
                    jobStartedTicks,
                    jobFinishedTicks,
                    timeout ? JobReadQuality.Timeout : JobReadQuality.Error,
                    failureCode,
                    []));
                metrics.Add(new JobExecutionMetric(
                    job.Sequence,
                    job.Job,
                    ElapsedMilliseconds(jobStartedTicks, jobFinishedTicks),
                    timeout ? "timeout" : "error",
                    failureCode));
                break;
            }
        }

        return new CycleExecution(
            epochId,
            cycleId,
            cycleStartedAtUtc,
            cycleStartedTicks,
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            batches,
            metrics,
            failureCode,
            failureMessage);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _ediabas.Dispose();
        _disposed = true;
    }

    private void EnsureJobOkay(string job)
    {
        EdiabasNet.ResultData? status = FindResult("JOB_STATUS");
        if (status is null || !string.Equals(
                status.OpData?.ToString(),
                "OKAY",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{job} did not return JOB_STATUS=OKAY.");
        }
    }

    private IReadOnlyList<ParameterSample> CaptureSamples(
        PollingPlan plan,
        PlannedJob job,
        DateTimeOffset acquiredAtUtc,
        long acquiredTicks)
    {
        var samples = new List<ParameterSample>();
        foreach (PlannedParameterRoute route in plan.Routes.Where(item =>
                     item.JobSequence == job.Sequence))
        {
            EdiabasNet.ResultData? valueResult = FindResult(route.ValueResult);
            if (valueResult?.OpData is null)
            {
                continue;
            }
            EdiabasNet.ResultData? unitResult = FindResult(route.UnitResult);
            object? value = NormalizeValue(valueResult.OpData);
            if (value is null)
            {
                continue;
            }
            string? unit = NormalizeValue(unitResult?.OpData)?.ToString();
            samples.Add(new ParameterSample(
                route.ParameterId,
                acquiredAtUtc,
                acquiredTicks,
                value,
                string.IsNullOrWhiteSpace(unit) ? null : unit,
                SampleQuality.Valid,
                null));
        }
        return samples;
    }

    private EdiabasNet.ResultData? FindResult(string name)
    {
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> sets =
            _ediabas.ResultSets;
        for (int index = sets.Count - 1; index >= 1; index--)
        {
            if (sets[index].TryGetValue(
                    name,
                    out EdiabasNet.ResultData? result))
            {
                return result;
            }
        }
        return null;
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        string or bool or byte or sbyte or short or ushort or int or uint or
            long or ulong or float or double or decimal => value,
        IFormattable formattable =>
            formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static bool IsTimeout(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return true;
        }
        string text = exception.ToString();
        return text.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("IFH-0009", StringComparison.OrdinalIgnoreCase);
    }

    private static double ElapsedMilliseconds(long startedTicks, long finishedTicks) =>
        (finishedTicks - startedTicks) * 1000.0 / Stopwatch.Frequency;
}

internal sealed class FakeCycleSource : ICycleSource
{
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly long _sessionStartedTicks;
    private readonly double _requestedHz;
    private readonly long? _faultCycle;

    public FakeCycleSource(
        DateTimeOffset sessionStartedAtUtc,
        long sessionStartedTicks,
        double requestedHz,
        long? faultCycle)
    {
        _sessionStartedAtUtc = sessionStartedAtUtc;
        _sessionStartedTicks = sessionStartedTicks;
        _requestedHz = requestedHz;
        _faultCycle = faultCycle;
    }

    public bool CommunicationAttempted => false;
    public string InterfaceInstance => "none";

    public CycleExecution ReadCycle(Guid epochId, long cycleId, PollingPlan plan)
    {
        double periodMs = 1000.0 / _requestedHz;
        double cycleBaseMs = (cycleId - 1) * periodMs;
        DateTimeOffset cycleStartedAtUtc =
            _sessionStartedAtUtc.AddMilliseconds(cycleBaseMs);
        long cycleStartedTicks =
            _sessionStartedTicks + TicksFromMilliseconds(cycleBaseMs);
        DateTimeOffset cursorUtc = cycleStartedAtUtc;
        long cursorTicks = cycleStartedTicks;
        var batches = new List<JobReadBatch>(plan.Jobs.Count);
        var metrics = new List<JobExecutionMetric>(plan.Jobs.Count);
        string? failureCode = null;
        string? failureMessage = null;

        foreach (PlannedJob job in plan.Jobs.OrderBy(item => item.Sequence))
        {
            double durationMs = job.Job == "STATUS_MOTORDREHZAHL" ? 70.0 : 110.0;
            DateTimeOffset jobStartedAtUtc = cursorUtc;
            long jobStartedTicks = cursorTicks;
            DateTimeOffset jobFinishedAtUtc = cursorUtc.AddMilliseconds(durationMs);
            long jobFinishedTicks = cursorTicks + TicksFromMilliseconds(durationMs);

            if (_faultCycle == cycleId)
            {
                failureCode = "SIMULATED_TIMEOUT";
                failureMessage = $"Synthetic timeout in {job.Job}.";
                batches.Add(new JobReadBatch(
                    epochId,
                    cycleId,
                    job.Sequence,
                    job.Job,
                    jobStartedAtUtc,
                    jobFinishedAtUtc,
                    jobStartedTicks,
                    jobFinishedTicks,
                    JobReadQuality.Timeout,
                    failureCode,
                    []));
                metrics.Add(new JobExecutionMetric(
                    job.Sequence,
                    job.Job,
                    durationMs,
                    "timeout",
                    failureCode));
                cursorUtc = jobFinishedAtUtc;
                cursorTicks = jobFinishedTicks;
                break;
            }

            var samples = new List<ParameterSample>();
            foreach (PlannedParameterRoute route in plan.Routes.Where(item =>
                         item.JobSequence == job.Sequence))
            {
                bool missing = cycleId == 5 &&
                    route.ParameterId == plan.Routes[0].ParameterId;
                if (missing)
                {
                    continue;
                }
                bool stale = cycleId == 7 &&
                    route.ParameterId == plan.Routes[0].ParameterId;
                samples.Add(new ParameterSample(
                    route.ParameterId,
                    jobFinishedAtUtc,
                    jobFinishedTicks,
                    SyntheticValue(route.ParameterId, cycleId),
                    SyntheticUnit(route.ParameterId),
                    stale ? SampleQuality.Stale : SampleQuality.Valid,
                    stale ? "SIM_STALE" : null));
            }
            batches.Add(new JobReadBatch(
                epochId,
                cycleId,
                job.Sequence,
                job.Job,
                jobStartedAtUtc,
                jobFinishedAtUtc,
                jobStartedTicks,
                jobFinishedTicks,
                JobReadQuality.Complete,
                null,
                samples));
            metrics.Add(new JobExecutionMetric(
                job.Sequence,
                job.Job,
                durationMs,
                "complete",
                null));
            cursorUtc = jobFinishedAtUtc;
            cursorTicks = jobFinishedTicks;
        }

        return new CycleExecution(
            epochId,
            cycleId,
            cycleStartedAtUtc,
            cycleStartedTicks,
            cursorUtc,
            cursorTicks,
            batches,
            metrics,
            failureCode,
            failureMessage);
    }

    public void Dispose()
    {
    }

    private static double SyntheticValue(ParameterId parameterId, long cycleId)
    {
        int code = parameterId.Value.Aggregate(0, (sum, character) => sum + character);
        return Math.Round((code % 200) + cycleId * 0.25, 3);
    }

    private static string SyntheticUnit(ParameterId parameterId)
    {
        string id = parameterId.Value;
        if (id == "engine.rpm") return "1/min";
        if (id == "vehicle.speed") return "km/h";
        if (id.Contains("temperature", StringComparison.Ordinal)) return "degC";
        if (id.Contains("angle", StringComparison.Ordinal)) return "deg";
        if (id.Contains("voltage", StringComparison.Ordinal)) return "V";
        if (id.Contains("pressure", StringComparison.Ordinal)) return "hPa";
        if (id.Contains("time", StringComparison.Ordinal)) return "ms";
        if (id.Contains("duty", StringComparison.Ordinal) ||
            id.Contains("load", StringComparison.Ordinal) ||
            id.Contains("integrator", StringComparison.Ordinal)) return "%";
        if (id.Contains("mass", StringComparison.Ordinal)) return "kg/h";
        return "1";
    }

    private static long TicksFromMilliseconds(double milliseconds) =>
        checked((long)Math.Round(
            milliseconds * Stopwatch.Frequency / 1000.0,
            MidpointRounding.AwayFromZero));
}
