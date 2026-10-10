using System.Diagnostics;
using System.Globalization;
using System.Text;
using BmwMs43Logger.Core;
using EdiabasLib;

namespace BmwMs43Logger.Ediabas;

internal sealed record LiveCycleExecution(
    DateTimeOffset CompletedAtUtc,
    long CompletedMonotonicTicks,
    IReadOnlyList<JobReadBatch> JobBatches,
    string? FailureCode,
    string? FailureMessage);

internal sealed class EdiabasReadOnlyClient : IDisposable
{
    private readonly EdiabasNet _ediabas;
    private CancellationToken _activeCancellation;
    private bool _disposed;

    public EdiabasReadOnlyClient(Ms43LiveRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
            ApplicationName = "BmwMs43Logger.Bmw14LiveRuntime"
        };
        EdiabasNet? ediabas = null;
        try
        {
            ediabas = new EdiabasNet(config, isHosted: true)
            {
                EdInterfaceClass = obd,
                SgbdFileName = Ms43LiveProfile.SgbdFileName
            };
            ediabas.AbortJobFunc = () => _activeCancellation.IsCancellationRequested;
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

    public LiveCycleExecution ReadCycle(
        Guid epochId,
        long cycleId,
        PollingPlan plan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        _activeCancellation = cancellationToken;

        var batches = new List<JobReadBatch>(plan.Jobs.Count);
        string? failureCode = null;
        string? failureMessage = null;

        foreach (PlannedJob job in plan.Jobs.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
            long startedTicks = Stopwatch.GetTimestamp();
            try
            {
                ExecuteExactReadOnlyJob(job);
                DateTimeOffset finishedAtUtc = DateTimeOffset.UtcNow;
                long finishedTicks = Stopwatch.GetTimestamp();
                EnsureJobOkay(job.Job);
                batches.Add(new JobReadBatch(
                    epochId,
                    cycleId,
                    job.Sequence,
                    job.Job,
                    startedAtUtc,
                    finishedAtUtc,
                    startedTicks,
                    finishedTicks,
                    JobReadQuality.Complete,
                    null,
                    CaptureSamples(
                        plan,
                        job,
                        finishedAtUtc,
                        finishedTicks)));
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                DateTimeOffset finishedAtUtc = DateTimeOffset.UtcNow;
                long finishedTicks = Stopwatch.GetTimestamp();
                failureCode = Classify(exception);
                failureMessage =
                    $"{job.Job} failed; EDIABAS_CODE={_ediabas.ErrorCodeLast}; " +
                    $"EXCEPTION={exception.GetType().Name}.";
                batches.Add(new JobReadBatch(
                    epochId,
                    cycleId,
                    job.Sequence,
                    job.Job,
                    startedAtUtc,
                    finishedAtUtc,
                    startedTicks,
                    finishedTicks,
                    failureCode == "EDIABAS_TIMEOUT"
                        ? JobReadQuality.Timeout
                        : JobReadQuality.Error,
                    failureCode,
                    []));
                break;
            }
        }

        return new LiveCycleExecution(
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            batches,
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

    private void ExecuteExactReadOnlyJob(PlannedJob job)
    {
        if (!job.Job.StartsWith("STATUS_", StringComparison.Ordinal) ||
            job.Argument.Length != 0 ||
            job.ResultNames.Count < 2 ||
            job.ResultNames[0] != "JOB_STATUS")
        {
            throw new InvalidOperationException(
                "A non-read-only or altered dispatch tuple was rejected.");
        }

        _ediabas.ArgString = string.Empty;
        _ediabas.ArgBinaryStd = null;
        _ediabas.ResultsRequests = string.Join(';', job.ResultNames);
        _ediabas.ExecuteJob(job.Job);
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

    private static string Classify(Exception exception)
    {
        string text = exception.ToString();
        if (exception is TimeoutException ||
            text.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("IFH-0009", StringComparison.OrdinalIgnoreCase))
        {
            return "EDIABAS_TIMEOUT";
        }
        if (exception is IOException or UnauthorizedAccessException ||
            text.Contains("COM5", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PORT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("IFH-0003", StringComparison.OrdinalIgnoreCase))
        {
            return "EDIABAS_PORT_LOST";
        }
        return "EDIABAS_TRANSPORT_ERROR";
    }
}
