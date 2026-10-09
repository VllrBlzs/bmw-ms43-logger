namespace BmwMs43Logger.Core;

public enum LoggerUiState
{
    Disconnected,
    Connecting,
    Ready,
    Starting,
    Logging,
    Stopping,
    Disconnecting,
    Faulted
}

public enum LoggerUiOperationKind
{
    Connect,
    Start,
    Stop,
    Disconnect
}

public readonly record struct LoggerUiOperation(
    Guid Id,
    LoggerUiOperationKind Kind);

public sealed record LoggerUiSnapshot(
    LoggerUiState State,
    LoggerUiOperation? ActiveOperation,
    string? LastError,
    long Revision)
{
    public bool CanConnect => State == LoggerUiState.Disconnected;
    public bool CanStart => State == LoggerUiState.Ready;
    public bool CanStop => State == LoggerUiState.Logging;
    public bool CanDisconnect => State is LoggerUiState.Ready or LoggerUiState.Faulted;
    public bool CanResetFault => State == LoggerUiState.Faulted;
    public bool IsBusy => State is
        LoggerUiState.Connecting or
        LoggerUiState.Starting or
        LoggerUiState.Stopping or
        LoggerUiState.Disconnecting;
}

public delegate void LoggerUiStateChangedEventHandler(
    object? sender,
    LoggerUiSnapshot snapshot);

public sealed class LoggerUiStateMachine
{
    private readonly object _gate = new();
    private LoggerUiState _state = LoggerUiState.Disconnected;
    private LoggerUiOperation? _activeOperation;
    private string? _lastError;
    private long _revision;

    public event LoggerUiStateChangedEventHandler? Changed;

    public LoggerUiSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public LoggerUiOperation BeginConnect() =>
        Begin(
            LoggerUiOperationKind.Connect,
            [LoggerUiState.Disconnected],
            LoggerUiState.Connecting);

    public LoggerUiOperation BeginStart() =>
        Begin(
            LoggerUiOperationKind.Start,
            [LoggerUiState.Ready],
            LoggerUiState.Starting);

    public LoggerUiOperation BeginStop() =>
        Begin(
            LoggerUiOperationKind.Stop,
            [LoggerUiState.Logging],
            LoggerUiState.Stopping);

    public LoggerUiOperation BeginDisconnect() =>
        Begin(
            LoggerUiOperationKind.Disconnect,
            [LoggerUiState.Ready, LoggerUiState.Faulted],
            LoggerUiState.Disconnecting);

    public void CompleteConnect(
        LoggerUiOperation operation,
        bool succeeded,
        string? error = null) =>
        Complete(
            operation,
            LoggerUiOperationKind.Connect,
            succeeded,
            LoggerUiState.Ready,
            error);

    public void CompleteStart(
        LoggerUiOperation operation,
        bool succeeded,
        string? error = null) =>
        Complete(
            operation,
            LoggerUiOperationKind.Start,
            succeeded,
            LoggerUiState.Logging,
            error);

    public void CompleteStop(
        LoggerUiOperation operation,
        bool succeeded,
        string? error = null) =>
        Complete(
            operation,
            LoggerUiOperationKind.Stop,
            succeeded,
            LoggerUiState.Ready,
            error);

    public void CompleteDisconnect(
        LoggerUiOperation operation,
        bool succeeded,
        string? error = null) =>
        Complete(
            operation,
            LoggerUiOperationKind.Disconnect,
            succeeded,
            LoggerUiState.Disconnected,
            error);

    public void ReportRuntimeFault(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException(
                "A visible runtime fault requires a non-empty error.",
                nameof(error));
        }

        LoggerUiSnapshot snapshot;
        lock (_gate)
        {
            if (_state == LoggerUiState.Disconnected)
            {
                throw new InvalidOperationException(
                    "A disconnected logger cannot report a runtime fault.");
            }

            _state = LoggerUiState.Faulted;
            _activeOperation = null;
            _lastError = error.Trim();
            _revision++;
            snapshot = CreateSnapshot();
        }
        OnChanged(snapshot);
    }

    public void ResetFault()
    {
        LoggerUiSnapshot snapshot;
        lock (_gate)
        {
            if (_state != LoggerUiState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Fault reset is not allowed from {_state}.");
            }

            _state = LoggerUiState.Disconnected;
            _activeOperation = null;
            _lastError = null;
            _revision++;
            snapshot = CreateSnapshot();
        }
        OnChanged(snapshot);
    }

    private LoggerUiOperation Begin(
        LoggerUiOperationKind kind,
        IReadOnlyCollection<LoggerUiState> allowedStates,
        LoggerUiState pendingState)
    {
        LoggerUiSnapshot snapshot;
        LoggerUiOperation operation;
        lock (_gate)
        {
            if (_activeOperation.HasValue || !allowedStates.Contains(_state))
            {
                throw new InvalidOperationException(
                    $"{kind} is not allowed from {_state}.");
            }

            operation = new LoggerUiOperation(Guid.NewGuid(), kind);
            _activeOperation = operation;
            _state = pendingState;
            _lastError = null;
            _revision++;
            snapshot = CreateSnapshot();
        }
        OnChanged(snapshot);
        return operation;
    }

    private void Complete(
        LoggerUiOperation operation,
        LoggerUiOperationKind expectedKind,
        bool succeeded,
        LoggerUiState successState,
        string? error)
    {
        LoggerUiSnapshot snapshot;
        lock (_gate)
        {
            if (operation.Kind != expectedKind ||
                !_activeOperation.HasValue ||
                _activeOperation.Value != operation)
            {
                throw new InvalidOperationException(
                    $"Stale or mismatched {expectedKind} completion was rejected.");
            }

            if (!succeeded && string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException(
                    "A failed operation requires a visible error.",
                    nameof(error));
            }

            _activeOperation = null;
            _state = succeeded ? successState : LoggerUiState.Faulted;
            _lastError = succeeded ? null : error!.Trim();
            _revision++;
            snapshot = CreateSnapshot();
        }
        OnChanged(snapshot);
    }

    private LoggerUiSnapshot CreateSnapshot() =>
        new(_state, _activeOperation, _lastError, _revision);

    private void OnChanged(LoggerUiSnapshot snapshot) =>
        Changed?.Invoke(this, snapshot);
}
