using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BmwMs43Logger.Core;

public enum SessionStopReason
{
    Requested,
    SourceCompleted,
    AcquisitionError,
    StorageError,
    DisposedWithoutStop
}

public sealed record SessionLogDescriptor(
    string SessionId,
    string EcuProfileId,
    string SgbdSha256,
    string BackendSha256,
    string CatalogVersion,
    string CatalogSha256,
    double RequestedHz,
    IReadOnlyList<ParameterId> SelectedParameters);

public sealed record SessionPrivacyMetadata(
    bool VinRecorded,
    bool AbsolutePathsRecorded,
    bool LicensedDescriptorsEmbedded);

public sealed record SessionLogMetadata(
    string SchemaVersion,
    string CsvSchemaVersion,
    string SessionId,
    string Status,
    string StopReason,
    string? FaultCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    long StartedMonotonicTicks,
    long FinishedMonotonicTicks,
    double DurationSeconds,
    double RequestedHz,
    double? EffectiveHz,
    string EcuProfileId,
    string SgbdSha256,
    string BackendSha256,
    string CatalogVersion,
    string CatalogSha256,
    IReadOnlyList<string> SelectedParameterIds,
    string CsvFileName,
    string MetadataFileName,
    string? CsvSha256,
    long CyclesWritten,
    long SamplesWritten,
    long? IncompleteCycleId,
    IReadOnlyDictionary<string, long> QualityCounts,
    long QueueDropped,
    long WriteErrors,
    SessionPrivacyMetadata Privacy);

public sealed class SessionLogWriteException : IOException
{
    public SessionLogWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface ISessionLogStorage : IDisposable
{
    string CsvFileName { get; }
    string MetadataFileName { get; }
    void WriteCsvLine(string line);
    void CompleteCsv();
    string GetCsvSha256();
    void WriteMetadata(string json);
}

public sealed class FileSessionLogStorage : ISessionLogStorage
{
    private readonly string _csvPath;
    private readonly string _metadataPath;
    private FileStream? _csvStream;
    private StreamWriter? _csvWriter;
    private bool _csvCompleted;
    private bool _disposed;

    public FileSessionLogStorage(string outputDirectory, string fileStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateFileStem(fileStem);

        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        _csvPath = Path.Combine(directory, fileStem + ".csv");
        _metadataPath = Path.Combine(directory, fileStem + ".meta.json");
        if (File.Exists(_metadataPath))
        {
            throw new IOException($"Metadata file already exists: {MetadataFileName}");
        }

        _csvStream = new FileStream(
            _csvPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        _csvWriter = new StreamWriter(
            _csvStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            NewLine = "\n"
        };
    }

    public string CsvFileName => Path.GetFileName(_csvPath);
    public string MetadataFileName => Path.GetFileName(_metadataPath);

    public void WriteCsvLine(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_csvCompleted)
        {
            throw new InvalidOperationException("CSV output is already completed.");
        }
        _csvWriter!.WriteLine(line);
    }

    public void CompleteCsv()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_csvCompleted)
        {
            return;
        }

        _csvWriter!.Flush();
        _csvStream!.Flush(flushToDisk: true);
        _csvWriter.Dispose();
        _csvStream.Dispose();
        _csvWriter = null;
        _csvStream = null;
        _csvCompleted = true;
    }

    public string GetCsvSha256()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_csvCompleted)
        {
            throw new InvalidOperationException("Complete the CSV before hashing it.");
        }
        using FileStream stream = File.OpenRead(_csvPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void WriteMetadata(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(json);
        string temporaryPath = _metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       bufferSize: 4096,
                       leaveOpen: true))
            {
                writer.NewLine = "\n";
                writer.Write(json);
                if (!json.EndsWith('\n'))
                {
                    writer.WriteLine();
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _metadataPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _csvWriter?.Dispose();
        _csvStream?.Dispose();
        _csvWriter = null;
        _csvStream = null;
        _disposed = true;
    }

    private static void ValidateFileStem(string fileStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        if (!string.Equals(fileStem, Path.GetFileName(fileStem), StringComparison.Ordinal) ||
            fileStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileStem is "." or "..")
        {
            throw new ArgumentException("File stem must be a safe basename.", nameof(fileStem));
        }
    }
}

public sealed class SessionCsvLogger : IDisposable
{
    public const string CsvSchemaVersion = "bmw07.long_csv.v1";
    public const string MetadataSchemaVersion = "bmw07.session_metadata.v1";

    public const string CsvHeader =
        "schema_version,session_id,epoch_id,cycle_id,cycle_completed_utc," +
        "cycle_completed_monotonic_ticks,parameter_id,job_sequence,source_job," +
        "job_started_utc,job_finished_utc,acquired_utc,monotonic_ticks,value," +
        "value_type,unit,quality,error_code,acquisition_ms,latency_ms," +
        "age_at_cycle_end_ms";

    private readonly SessionLogDescriptor _descriptor;
    private readonly ISessionLogStorage _storage;
    private readonly ParameterId[] _selectedParameters;
    private readonly HashSet<ParameterId> _selectedParameterSet;
    private readonly SortedDictionary<string, long> _qualityCounts;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly long _startedMonotonicTicks;
    private Guid? _epochId;
    private long? _lastCycleId;
    private DateTimeOffset? _lastCycleCompletedAtUtc;
    private long? _lastCycleCompletedMonotonicTicks;
    private long? _firstCycleCompletedMonotonicTicks;
    private long? _incompleteCycleId;
    private long _cyclesWritten;
    private long _samplesWritten;
    private long _queueDropped;
    private long _writeErrors;
    private bool _finalized;
    private bool _disposed;

    public SessionCsvLogger(
        SessionLogDescriptor descriptor,
        ISessionLogStorage storage,
        DateTimeOffset startedAtUtc,
        long startedMonotonicTicks)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(storage);
        ValidateDescriptor(descriptor);
        if (startedMonotonicTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startedMonotonicTicks));
        }

        _descriptor = descriptor;
        _storage = storage;
        _selectedParameters = descriptor.SelectedParameters.ToArray();
        _selectedParameterSet = _selectedParameters.ToHashSet();
        _qualityCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string name in Enum.GetNames<SampleQuality>())
        {
            _qualityCounts.Add(name.ToLowerInvariant(), 0);
        }
        _startedAtUtc = startedAtUtc;
        _startedMonotonicTicks = startedMonotonicTicks;
        _storage.WriteCsvLine(CsvHeader);
    }

    public void AppendCycle(NormalizedCycle cycle)
    {
        EnsureRunning();
        ArgumentNullException.ThrowIfNull(cycle);
        ValidateCycle(cycle);
        string[] lines = cycle.Samples.Select(sample => FormatRow(cycle, sample)).ToArray();

        _incompleteCycleId = cycle.CycleId;
        try
        {
            for (int index = 0; index < lines.Length; index++)
            {
                _storage.WriteCsvLine(lines[index]);
                _samplesWritten++;
                IncrementQuality(cycle.Samples[index].Quality);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _writeErrors++;
            Exception? finalizationError = null;
            DateTimeOffset faultFinishedAtUtc = DateTimeOffset.UtcNow;
            if (faultFinishedAtUtc < cycle.CompletedAtUtc)
            {
                faultFinishedAtUtc = cycle.CompletedAtUtc;
            }
            try
            {
                FinalizeInternal(
                    "faulted",
                    SessionStopReason.StorageError,
                    "CSV_WRITE_FAILED",
                    faultFinishedAtUtc,
                    Math.Max(Stopwatch.GetTimestamp(), cycle.CompletedMonotonicTicks));
            }
            catch (Exception finalizeException)
            {
                finalizationError = finalizeException;
            }

            if (finalizationError is not null)
            {
                throw new AggregateException(
                    "CSV write and fault-metadata finalization both failed.",
                    exception,
                    finalizationError);
            }
            throw new SessionLogWriteException("CSV write failed; session was faulted.", exception);
        }

        _cyclesWritten++;
        _incompleteCycleId = null;
        _epochId ??= cycle.EpochId;
        _lastCycleId = cycle.CycleId;
        _lastCycleCompletedAtUtc = cycle.CompletedAtUtc;
        _lastCycleCompletedMonotonicTicks = cycle.CompletedMonotonicTicks;
        _firstCycleCompletedMonotonicTicks ??= cycle.CompletedMonotonicTicks;
    }

    public void RecordQueueDropped(long cumulativeDropped)
    {
        EnsureRunning();
        if (cumulativeDropped < _queueDropped)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeDropped),
                "Queue drop count must be monotonic.");
        }
        _queueDropped = cumulativeDropped;
    }

    public SessionLogMetadata Complete(
        DateTimeOffset finishedAtUtc,
        long finishedMonotonicTicks,
        SessionStopReason reason = SessionStopReason.Requested)
    {
        EnsureRunning();
        if (reason is not (SessionStopReason.Requested or SessionStopReason.SourceCompleted))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        return FinalizeInternal(
            "completed",
            reason,
            null,
            finishedAtUtc,
            finishedMonotonicTicks);
    }

    public SessionLogMetadata Fail(
        DateTimeOffset finishedAtUtc,
        long finishedMonotonicTicks,
        SessionStopReason reason,
        string faultCode)
    {
        EnsureRunning();
        if (reason is not (SessionStopReason.AcquisitionError or SessionStopReason.StorageError))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        ValidateToken(faultCode, nameof(faultCode), allowDot: false);
        return FinalizeInternal(
            "faulted",
            reason,
            faultCode,
            finishedAtUtc,
            finishedMonotonicTicks);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_finalized)
        {
            try
            {
                FinalizeInternal(
                    "faulted",
                    SessionStopReason.DisposedWithoutStop,
                    "LOGGER_DISPOSED_WITHOUT_STOP",
                    DateTimeOffset.UtcNow,
                    Math.Max(Stopwatch.GetTimestamp(), _startedMonotonicTicks));
            }
            catch
            {
                // Dispose must still release the underlying handle.
            }
        }
        _storage.Dispose();
        _disposed = true;
    }

    private SessionLogMetadata FinalizeInternal(
        string status,
        SessionStopReason stopReason,
        string? faultCode,
        DateTimeOffset finishedAtUtc,
        long finishedMonotonicTicks)
    {
        if (finishedAtUtc < _startedAtUtc ||
            finishedMonotonicTicks < _startedMonotonicTicks ||
            (_lastCycleCompletedAtUtc.HasValue &&
             finishedAtUtc < _lastCycleCompletedAtUtc.Value) ||
            (_lastCycleCompletedMonotonicTicks.HasValue &&
             finishedMonotonicTicks < _lastCycleCompletedMonotonicTicks.Value))
        {
            throw new InvalidOperationException("Session finish precedes logged data.");
        }

        _storage.CompleteCsv();
        string csvSha256 = _storage.GetCsvSha256();
        double durationSeconds =
            (finishedMonotonicTicks - _startedMonotonicTicks) /
            (double)Stopwatch.Frequency;
        double? effectiveHz = CalculateEffectiveHz();
        var metadata = new SessionLogMetadata(
            MetadataSchemaVersion,
            CsvSchemaVersion,
            _descriptor.SessionId,
            status,
            ToSnakeCase(stopReason),
            faultCode,
            _startedAtUtc,
            finishedAtUtc,
            _startedMonotonicTicks,
            finishedMonotonicTicks,
            durationSeconds,
            _descriptor.RequestedHz,
            effectiveHz,
            _descriptor.EcuProfileId,
            _descriptor.SgbdSha256,
            _descriptor.BackendSha256,
            _descriptor.CatalogVersion,
            _descriptor.CatalogSha256,
            _selectedParameters.Select(parameter => parameter.Value).ToArray(),
            _storage.CsvFileName,
            _storage.MetadataFileName,
            csvSha256,
            _cyclesWritten,
            _samplesWritten,
            _incompleteCycleId,
            new SortedDictionary<string, long>(_qualityCounts, StringComparer.Ordinal),
            _queueDropped,
            _writeErrors,
            new SessionPrivacyMetadata(
                VinRecorded: false,
                AbsolutePathsRecorded: false,
                LicensedDescriptorsEmbedded: false));

        string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        _storage.WriteMetadata(json);
        _finalized = true;
        return metadata;
    }

    private void ValidateCycle(NormalizedCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle.Samples);
        if (cycle.CycleId < 1)
        {
            throw new InvalidOperationException("Cycle ID must be positive.");
        }
        if (cycle.CompletedMonotonicTicks < _startedMonotonicTicks ||
            cycle.CompletedAtUtc < _startedAtUtc)
        {
            throw new InvalidOperationException("Cycle precedes the session start.");
        }
        if (_epochId.HasValue && cycle.EpochId != _epochId.Value)
        {
            throw new InvalidOperationException(
                "A reconnect epoch requires a new logging session.");
        }
        if (_lastCycleId.HasValue && cycle.CycleId <= _lastCycleId.Value)
        {
            throw new InvalidOperationException("Cycle IDs must be strictly increasing.");
        }
        if ((_lastCycleCompletedAtUtc.HasValue &&
             cycle.CompletedAtUtc < _lastCycleCompletedAtUtc.Value) ||
            (_lastCycleCompletedMonotonicTicks.HasValue &&
             cycle.CompletedMonotonicTicks <= _lastCycleCompletedMonotonicTicks.Value))
        {
            throw new InvalidOperationException("Cycle completion time is not monotonic.");
        }

        var actualParameters = new HashSet<ParameterId>();
        foreach (NormalizedSample sample in cycle.Samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            if (sample.EpochId != cycle.EpochId || sample.CycleId != cycle.CycleId)
            {
                throw new InvalidOperationException("Sample belongs to another cycle.");
            }
            if (!_selectedParameterSet.Contains(sample.ParameterId) ||
                !actualParameters.Add(sample.ParameterId))
            {
                throw new InvalidOperationException(
                    $"Unexpected or duplicate parameter: {sample.ParameterId.Value}");
            }
        }
        if (!actualParameters.SetEquals(_selectedParameterSet))
        {
            throw new InvalidOperationException(
                "Every selected parameter requires an explicit sample or quality state.");
        }
    }

    private string FormatRow(NormalizedCycle cycle, NormalizedSample sample)
    {
        object?[] fields =
        [
            CsvSchemaVersion,
            _descriptor.SessionId,
            cycle.EpochId.ToString("D"),
            cycle.CycleId,
            FormatDateTime(cycle.CompletedAtUtc),
            cycle.CompletedMonotonicTicks,
            sample.ParameterId.Value,
            sample.JobSequence,
            sample.SourceJob,
            FormatNullableDateTime(sample.JobStartedAtUtc),
            FormatNullableDateTime(sample.JobFinishedAtUtc),
            FormatNullableDateTime(sample.AcquiredAtUtc),
            sample.MonotonicTicks,
            FormatValue(sample.Value),
            GetValueType(sample.Value),
            sample.Unit,
            sample.Quality.ToString().ToLowerInvariant(),
            sample.ErrorCode,
            FormatNullableDouble(sample.AcquisitionMs),
            FormatNullableDouble(sample.LatencyMs),
            FormatNullableDouble(sample.AgeAtCycleEndMs)
        ];
        return string.Join(',', fields.Select(field => EscapeCsv(ConvertField(field))));
    }

    private double? CalculateEffectiveHz()
    {
        if (_cyclesWritten < 2 ||
            !_firstCycleCompletedMonotonicTicks.HasValue ||
            !_lastCycleCompletedMonotonicTicks.HasValue)
        {
            return null;
        }
        long span = _lastCycleCompletedMonotonicTicks.Value -
            _firstCycleCompletedMonotonicTicks.Value;
        if (span <= 0)
        {
            return null;
        }
        return (_cyclesWritten - 1) * Stopwatch.Frequency / (double)span;
    }

    private void IncrementQuality(SampleQuality quality)
    {
        string key = quality.ToString().ToLowerInvariant();
        _qualityCounts.TryGetValue(key, out long count);
        _qualityCounts[key] = count + 1;
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
        {
            throw new InvalidOperationException("Logging session is already finalized.");
        }
    }

    private static void ValidateDescriptor(SessionLogDescriptor descriptor)
    {
        ValidateToken(descriptor.SessionId, nameof(descriptor.SessionId), allowDot: true);
        ValidateToken(descriptor.EcuProfileId, nameof(descriptor.EcuProfileId), allowDot: true);
        ValidateHash(descriptor.SgbdSha256, nameof(descriptor.SgbdSha256));
        ValidateHash(descriptor.BackendSha256, nameof(descriptor.BackendSha256));
        ValidateToken(descriptor.CatalogVersion, nameof(descriptor.CatalogVersion), allowDot: true);
        ValidateHash(descriptor.CatalogSha256, nameof(descriptor.CatalogSha256));
        if (!double.IsFinite(descriptor.RequestedHz) || descriptor.RequestedHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor.RequestedHz));
        }
        ArgumentNullException.ThrowIfNull(descriptor.SelectedParameters);
        if (descriptor.SelectedParameters.Count == 0 ||
            descriptor.SelectedParameters.Distinct().Count() !=
            descriptor.SelectedParameters.Count)
        {
            throw new ArgumentException(
                "Selected parameters must be non-empty and unique.",
                nameof(descriptor.SelectedParameters));
        }
    }

    private static void ValidateHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "SHA-256 must contain exactly 64 hexadecimal characters.",
                parameterName);
        }
    }

    private static void ValidateToken(string value, string parameterName, bool allowDot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        bool valid = value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' ||
            (allowDot && character == '.'));
        if (!valid)
        {
            throw new ArgumentException("Identifier contains unsafe characters.", parameterName);
        }
    }

    private static string ConvertField(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatNullableDateTime(DateTimeOffset? value) =>
        value.HasValue ? FormatDateTime(value.Value) : string.Empty;

    private static string FormatNullableDouble(double? value) =>
        value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement element => FormatJsonElement(element),
            DateTimeOffset dateTimeOffset => FormatDateTime(dateTimeOffset),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            float single => single.ToString("R", CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText()
        };
    }

    private static string GetValueType(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number => "number",
                JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => "json"
            };
        }
        return value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong => "integer",
            float or double or decimal => "real",
            bool => "boolean",
            string or char => "string",
            DateTime or DateTimeOffset => "datetime",
            _ => "object"
        };
    }

    private static string ToSnakeCase(SessionStopReason reason)
    {
        return reason switch
        {
            SessionStopReason.Requested => "requested",
            SessionStopReason.SourceCompleted => "source_completed",
            SessionStopReason.AcquisitionError => "acquisition_error",
            SessionStopReason.StorageError => "storage_error",
            SessionStopReason.DisposedWithoutStop => "disposed_without_stop",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
    }
}
