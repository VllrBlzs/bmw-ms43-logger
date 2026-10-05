using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BmwMs43Logger.Core;
using EdiabasLib;

namespace BmwMs43Logger.LiveRpmProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            ProbeOptions options = ProbeOptions.Parse(args);
            ControlledRpmProfile.RunGuardSelfTests();
            ControlledRpmProfile.ValidateRuntime(options.EcuPath, options.Port);

            if (options.Mode == ProbeMode.Preflight)
            {
                PrintPreflight(options);
                return 0;
            }

            await RunLiveAsync(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("RESULT=CANCELLED");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void PrintPreflight(ProbeOptions options)
    {
        string assemblyPath = typeof(EdiabasNet).Assembly.Location;
        string sgbdPath = Path.Combine(
            options.EcuPath,
            ControlledRpmProfile.SgbdFileName);

        Console.WriteLine("BMW-02C CONTROLLED LIVE RPM PREFLIGHT");
        Console.WriteLine("PROFILE_STATE=CONTROLLED_TEST");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
        Console.WriteLine("INTERFACE_INSTANCE=None");
        Console.WriteLine("GUARD_TESTS=PASS");
        Console.WriteLine($"PORT={options.Port}");
        Console.WriteLine($"JOB={ControlledRpmProfile.Job}");
        Console.WriteLine("ARGUMENT=<EMPTY>");
        Console.WriteLine(
            $"RESULT_REQUESTS={string.Join(';', ControlledRpmProfile.ResultNames)}");
        Console.WriteLine($"ASSEMBLY={assemblyPath}");
        Console.WriteLine(
            $"ASSEMBLY_SHA256={ControlledRpmProfile.GetSha256(assemblyPath)}");
        Console.WriteLine($"SGBD={sgbdPath}");
        Console.WriteLine(
            $"SGBD_SHA256={ControlledRpmProfile.GetSha256(sgbdPath)}");
        Console.WriteLine("RESULT=PASS");
    }

    private static async Task RunLiveAsync(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var counts = Enum.GetValues<SampleQuality>()
            .ToDictionary(quality => quality, _ => 0);
        var rpmValues = new List<double>();
        var acquisition = new EdiabasRpmAcquisition(options.EcuPath, options.Port);
        var elapsed = Stopwatch.StartNew();

        try
        {
            await acquisition.ConnectAsync(cancellationToken);
            using var csv = new CsvSampleWriter(options.OutputPath);

            while (elapsed.Elapsed < options.Duration)
            {
                long cycleStarted = Stopwatch.GetTimestamp();
                AcquisitionBatch batch = await acquisition.ReadAsync(
                    [ControlledRpmProfile.RpmId],
                    cancellationToken);
                csv.Write(batch);

                foreach (ParameterSample sample in batch.Samples)
                {
                    counts[sample.Quality]++;
                    if (sample.Quality == SampleQuality.Valid)
                    {
                        if (sample.Value is not double rpm)
                        {
                            throw new InvalidOperationException(
                                "A valid RPM sample did not contain a real value.");
                        }
                        rpmValues.Add(rpm);
                    }
                    else if (sample.Value is not null)
                    {
                        throw new InvalidOperationException(
                            "A non-valid RPM sample contained a value.");
                    }
                }

                TimeSpan remaining = TimeSpan.FromMilliseconds(200) -
                                     Stopwatch.GetElapsedTime(cycleStarted);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
        }
        finally
        {
            await acquisition.DisposeAsync();
        }

        if (rpmValues.Count < 3)
        {
            throw new InvalidOperationException(
                $"Too few valid RPM samples: {rpmValues.Count}.");
        }

        int invalidCount = counts
            .Where(pair => pair.Key != SampleQuality.Valid)
            .Sum(pair => pair.Value);
        if (invalidCount != 0)
        {
            throw new InvalidOperationException(
                $"The controlled proof produced {invalidCount} invalid samples.");
        }

        Console.WriteLine("BMW-02C CONTROLLED LIVE RPM PROBE");
        Console.WriteLine("PROFILE_STATE=CONTROLLED_TEST");
        Console.WriteLine("COMMUNICATION_ATTEMPTED=True");
        Console.WriteLine("GUARD_TESTS=PASS");
        Console.WriteLine($"PORT={options.Port}");
        Console.WriteLine($"DURATION_SECONDS={elapsed.Elapsed.TotalSeconds:F2}");
        Console.WriteLine($"SAMPLES={rpmValues.Count}");
        foreach ((SampleQuality quality, int count) in counts)
        {
            Console.WriteLine($"{quality.ToString().ToUpperInvariant()}={count}");
        }
        Console.WriteLine($"RPM_MIN={rpmValues.Min():F2}");
        Console.WriteLine($"RPM_MAX={rpmValues.Max():F2}");
        Console.WriteLine($"RPM_LAST={rpmValues[^1]:F2}");
        Console.WriteLine(
            $"EFFECTIVE_HZ={rpmValues.Count / elapsed.Elapsed.TotalSeconds:F2}");
        Console.WriteLine($"CSV={options.OutputPath}");
        Console.WriteLine("COM_CLOSED=True");
        Console.WriteLine("RESULT=PASS");
    }
}

internal enum ProbeMode
{
    Preflight,
    Live
}

internal sealed record ProbeOptions(
    ProbeMode Mode,
    TimeSpan Duration,
    string OutputPath,
    string EcuPath,
    string Port)
{
    public static ProbeOptions Parse(IReadOnlyList<string> args)
    {
        bool preflight = false;
        bool live = false;
        bool confirmed = false;
        int durationSeconds = 10;
        string ecuPath = @"C:\EDIABAS\ECU";
        string port = "COM5";
        string? outputPath = null;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--preflight":
                    preflight = true;
                    break;
                case "--live":
                    live = true;
                    break;
                case "--confirm-read-only":
                    confirmed = true;
                    break;
                case "--duration-seconds" when index + 1 < args.Count:
                    durationSeconds = int.Parse(
                        args[++index],
                        CultureInfo.InvariantCulture);
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
                case "--ecu-path" when index + 1 < args.Count:
                    ecuPath = args[++index];
                    break;
                case "--port" when index + 1 < args.Count:
                    port = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete option: {args[index]}");
            }
        }

        if (preflight == live)
        {
            throw new ArgumentException(
                "Specify exactly one mode: --preflight or --live.");
        }
        if (live && !confirmed)
        {
            throw new InvalidOperationException(
                "Live mode requires --confirm-read-only.");
        }
        if (durationSeconds is < 10 or > 300)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "The controlled live proof is bounded to 10-300 seconds.");
        }

        ProbeMode mode = live ? ProbeMode.Live : ProbeMode.Preflight;
        string modeName = mode == ProbeMode.Live ? "live" : "preflight";
        outputPath ??= Path.Combine(
            "data",
            "private",
            $"bmw02c_{modeName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");

        return new ProbeOptions(
            mode,
            TimeSpan.FromSeconds(durationSeconds),
            Path.GetFullPath(outputPath),
            Path.GetFullPath(ecuPath),
            port.ToUpperInvariant());
    }
}

internal sealed record ReviewedDispatch(
    string Job,
    string Argument,
    IReadOnlyList<string> ResultNames,
    string SgbdSha256,
    string BackendSha256);

internal static class ControlledRpmProfile
{
    public const string SgbdFileName = "ms430ds0.prg";
    public const string Job = "STATUS_MOTORDREHZAHL";
    public const string ExpectedSgbdSha256 =
        "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523";
    public const string ExpectedBackendSha256 =
        "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59";

    public static readonly ParameterId RpmId = new("engine.rpm");
    public static readonly IReadOnlyList<string> ResultNames =
    [
        "JOB_STATUS",
        "STAT_MOTORDREHZAHL_WERT",
        "STATUS_MOTORDREHZAHL_WERT",
        "STAT_MOTORDREHZAHL_EINH"
    ];

    public static ReviewedDispatch Resolve(ParameterId parameterId)
    {
        ArgumentNullException.ThrowIfNull(parameterId);
        if (parameterId != RpmId)
        {
            throw new InvalidOperationException(
                $"Parameter is not allowlisted: {parameterId.Value}");
        }

        return new ReviewedDispatch(
            Job,
            string.Empty,
            ResultNames,
            ExpectedSgbdSha256,
            ExpectedBackendSha256);
    }

    public static void EnsureExact(ReviewedDispatch candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ReviewedDispatch expected = Resolve(RpmId);
        bool exact = candidate.Job == expected.Job &&
                     candidate.Argument == expected.Argument &&
                     candidate.ResultNames.SequenceEqual(
                         expected.ResultNames,
                         StringComparer.Ordinal) &&
                     candidate.SgbdSha256 == expected.SgbdSha256 &&
                     candidate.BackendSha256 == expected.BackendSha256;

        if (!exact)
        {
            throw new InvalidOperationException(
                "Dispatch tuple is not exactly allowlisted.");
        }
    }

    public static void ValidateRuntime(string ecuPath, string port)
    {
        if (!string.Equals(port, "COM5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This controlled profile permits COM5 only, not {port}.");
        }
        if (ecuPath.Contains(';'))
        {
            throw new InvalidOperationException(
                "The ECU directory must not contain a semicolon.");
        }

        string sgbdPath = Path.Combine(ecuPath, SgbdFileName);
        RequireHash(sgbdPath, ExpectedSgbdSha256, "SGBD");

        string assemblyPath = typeof(EdiabasNet).Assembly.Location;
        RequireHash(
            assemblyPath,
            ExpectedBackendSha256,
            "EdiabasLib runtime assembly");
    }

    public static void RunGuardSelfTests()
    {
        ReviewedDispatch approved = Resolve(RpmId);
        EnsureExact(approved);
        ExpectRejected(approved with
        {
            Job = "STEUERN_LL_DREHZAHL_VERSTELLEN"
        });
        ExpectRejected(approved with { Argument = "750" });
        ExpectRejected(approved with { ResultNames = ["JOB_STATUS"] });
        ExpectRejected(approved with
        {
            SgbdSha256 = new string('0', 64)
        });
        ExpectRejected(approved with
        {
            BackendSha256 = new string('0', 64)
        });

        try
        {
            Resolve(new ParameterId("engine.coolant"));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Guard accepted a non-allowlisted parameter.");
    }

    public static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RequireHash(
        string path,
        string expected,
        string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found.", path);
        }

        string actual = GetSha256(path);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{description} hash mismatch. Expected {expected}, actual {actual}.");
        }
    }

    private static void ExpectRejected(ReviewedDispatch candidate)
    {
        try
        {
            EnsureExact(candidate);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Guard accepted a prohibited dispatch tuple.");
    }
}

internal sealed class EdiabasRpmAcquisition : IReadOnlyAcquisition
{
    private readonly string _ecuPath;
    private readonly string _port;
    private readonly Guid _epochId = Guid.NewGuid();
    private EdiabasNet? _ediabas;
    private CancellationToken _activeCancellationToken;
    private long _cycleId;

    public EdiabasRpmAcquisition(string ecuPath, string port)
    {
        _ecuPath = ecuPath;
        _port = port;
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ediabas is not null)
        {
            throw new InvalidOperationException(
                "EDIABAS acquisition is already initialized.");
        }

        ControlledRpmProfile.ValidateRuntime(_ecuPath, _port);
        string config = string.Join(
            ';',
            $"EcuPath={_ecuPath}",
            $"ObdComPort={_port}",
            "Interface=STD:OBD",
            "ApiTrace=0",
            "IfhTrace=0",
            "TraceBuffering=0",
            "IfhTraceBuffering=0",
            "RetryComm=1",
            "SystemResults=1");

        var obd = new EdInterfaceObd
        {
            ComPort = _port,
            IfhName = "STD:OBD",
            ApplicationName = "BmwMs43Logger.LiveRpmProbe"
        };
        var ediabas = new EdiabasNet(config, isHosted: true)
        {
            EdInterfaceClass = obd,
            SgbdFileName = ControlledRpmProfile.SgbdFileName
        };
        ediabas.AbortJobFunc = () =>
            _activeCancellationToken.IsCancellationRequested;
        _ediabas = ediabas;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AcquisitionBatch> ReadAsync(
        IReadOnlyCollection<ParameterId> parameterIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EdiabasNet ediabas = _ediabas ?? throw new InvalidOperationException(
            "EDIABAS acquisition is not initialized.");

        if (parameterIds.Count != 1 ||
            !parameterIds.Contains(ControlledRpmProfile.RpmId))
        {
            throw new InvalidOperationException(
                "The controlled probe supports engine.rpm only.");
        }

        ReviewedDispatch dispatch = ControlledRpmProfile.Resolve(
            ControlledRpmProfile.RpmId);
        ControlledRpmProfile.EnsureExact(dispatch);

        long cycleId = ++_cycleId;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        _activeCancellationToken = cancellationToken;

        try
        {
            ediabas.ArgString = dispatch.Argument;
            ediabas.ArgBinary = null;
            ediabas.ArgStringStd = string.Empty;
            ediabas.ArgBinaryStd = null;
            ediabas.ResultsRequests = string.Join(';', dispatch.ResultNames);
            ediabas.ExecuteJob(dispatch.Job);

            double rpm = ReadRpm(ediabas.ResultSets);
            string unit = ReadUnit(ediabas.ResultSets);
            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            var sample = new ParameterSample(
                ControlledRpmProfile.RpmId,
                finishedAt,
                Stopwatch.GetTimestamp(),
                rpm,
                unit,
                SampleQuality.Valid,
                null);
            var batch = new AcquisitionBatch(
                _epochId,
                cycleId,
                startedAt,
                finishedAt,
                [sample]);
            return new ValueTask<AcquisitionBatch>(batch);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string detail = EdiabasNet.GetExceptionText(
                exception,
                includeTypes: true,
                includeStack: false);
            throw new InvalidOperationException(
                $"EDIABAS RPM read failed ({ediabas.ErrorCodeLast}): {detail}",
                exception);
        }
        finally
        {
            _activeCancellationToken = default;
        }
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisposeBackend();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeBackend();
        return ValueTask.CompletedTask;
    }

    private static double ReadRpm(
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> resultSets)
    {
        List<Dictionary<string, EdiabasNet.ResultData>> dataSets = resultSets
            .Skip(1)
            .ToList();
        bool jobOkay = dataSets.Any(resultSet =>
            resultSet.TryGetValue("JOB_STATUS", out EdiabasNet.ResultData? status) &&
            status?.OpData is string statusText &&
            string.Equals(statusText, "OKAY", StringComparison.OrdinalIgnoreCase));
        if (!jobOkay)
        {
            throw new InvalidOperationException(
                "STATUS_MOTORDREHZAHL did not return JOB_STATUS=OKAY.");
        }

        Dictionary<string, EdiabasNet.ResultData>? rpmSet = dataSets
            .LastOrDefault(resultSet =>
                resultSet.ContainsKey("STAT_MOTORDREHZAHL_WERT") ||
                resultSet.ContainsKey("STATUS_MOTORDREHZAHL_WERT"));
        if (rpmSet is null)
        {
            throw new InvalidOperationException(
                "The RPM result fields are missing.");
        }

        bool hasPrimary = TryReadReal(
            rpmSet,
            "STAT_MOTORDREHZAHL_WERT",
            out double primary);
        bool hasAlias = TryReadReal(
            rpmSet,
            "STATUS_MOTORDREHZAHL_WERT",
            out double alias);
        if (!hasPrimary && !hasAlias)
        {
            throw new InvalidOperationException(
                "Neither allowlisted RPM result contained a real value.");
        }
        if (hasPrimary && hasAlias && Math.Abs(primary - alias) > 0.5)
        {
            throw new InvalidOperationException(
                $"RPM aliases disagree: {primary} versus {alias}.");
        }

        double rpm = hasPrimary ? primary : alias;
        if (!double.IsFinite(rpm) || rpm is < 0 or > 10_000)
        {
            throw new InvalidOperationException(
                $"RPM is outside the controlled range: {rpm}.");
        }
        return rpm;
    }

    private static string ReadUnit(
        IReadOnlyList<Dictionary<string, EdiabasNet.ResultData>> resultSets)
    {
        string? unit = resultSets
            .Skip(1)
            .Select(resultSet =>
                resultSet.TryGetValue(
                    "STAT_MOTORDREHZAHL_EINH",
                    out EdiabasNet.ResultData? result)
                    ? result?.OpData as string
                    : null)
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return unit ?? throw new InvalidOperationException(
            "The allowlisted RPM unit result is missing.");
    }

    private static bool TryReadReal(
        IReadOnlyDictionary<string, EdiabasNet.ResultData> resultSet,
        string name,
        out double value)
    {
        value = default;
        if (!resultSet.TryGetValue(name, out EdiabasNet.ResultData? result))
        {
            return false;
        }
        if (result?.OpData is not double realValue)
        {
            throw new InvalidOperationException(
                $"{name} did not contain the reviewed real result type.");
        }
        value = realValue;
        return true;
    }

    private void DisposeBackend()
    {
        EdiabasNet? ediabas = _ediabas;
        _ediabas = null;
        ediabas?.Dispose();
    }
}

internal sealed class CsvSampleWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvSampleWriter(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        _writer.WriteLine(
            "epoch_id,cycle_id,started_utc,finished_utc,acquisition_ms," +
            "acquired_utc,monotonic_ticks,parameter_id,value,unit,quality,error_code");
    }

    public void Write(AcquisitionBatch batch)
    {
        double acquisitionMs =
            (batch.FinishedAtUtc - batch.StartedAtUtc).TotalMilliseconds;
        foreach (ParameterSample sample in batch.Samples)
        {
            string[] fields =
            {
                batch.EpochId.ToString("D"),
                batch.CycleId.ToString(CultureInfo.InvariantCulture),
                batch.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                batch.FinishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                acquisitionMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.AcquiredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.MonotonicTicks.ToString(CultureInfo.InvariantCulture),
                sample.ParameterId.Value,
                FormatValue(sample.Value),
                sample.Unit ?? string.Empty,
                sample.Quality.ToString(),
                sample.ErrorCode ?? string.Empty
            };
            _writer.WriteLine(string.Join(',', fields.Select(Escape)));
        }
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable =>
            formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') &&
            !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
