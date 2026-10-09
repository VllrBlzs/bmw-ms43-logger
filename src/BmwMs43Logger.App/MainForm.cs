using System.Diagnostics;
using System.Globalization;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.App;

internal sealed class MainForm : Form
{
    private readonly LoggerUiStateMachine _stateMachine = new();
    private readonly SimulatedLoggerRuntime _runtime = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _modeLabel = new();
    private readonly Label _stateLabel = new();
    private readonly Label _errorLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _browseButton = new();
    private readonly Button _openFolderButton = new();
    private readonly TextBox _outputTextBox = new();
    private readonly NumericUpDown _hzInput = new();
    private readonly DataGridView _valuesGrid = new();
    private readonly RichTextBox _eventLog = new();
    private readonly Dictionary<string, DataGridViewRow> _valueRows =
        new(StringComparer.Ordinal);
    private string? _lastRunDirectory;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainForm()
    {
        Text = "BMW MS43 Read-only Logger — BMW-09 offline";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 600);
        Size = new Size(980, 680);
        Font = new Font("Segoe UI", 9F);
        BuildLayout();
        WireEvents();
        InitializeValuesGrid();
        ApplySnapshot(_stateMachine.Snapshot);
        AppendEvent("Offline GUI ready. No ECU interface has been created.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }
        if (_stateMachine.Snapshot.IsBusy)
        {
            MessageBox.Show(
                this,
                "Várd meg az aktuális művelet végét, majd zárd be újra az ablakot.",
                "BMW MS43 Logger",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _shutdownStarted = true;
        _ = ShutdownAndCloseAsync();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _modeLabel.AutoSize = true;
        _modeLabel.Font = new Font(Font, FontStyle.Bold);
        _modeLabel.ForeColor = Color.DarkOrange;
        _modeLabel.Text = "OFFLINE SIMULATION — COMMUNICATION_ATTEMPTED=False";
        root.Controls.Add(_modeLabel, 0, 0);

        var commandRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 6),
            WrapContents = false
        };
        ConfigureButton(_connectButton, "Connect", 100);
        ConfigureButton(_startButton, "Start logging", 120);
        ConfigureButton(_stopButton, "Stop", 100);
        ConfigureButton(_disconnectButton, "Disconnect", 100);
        commandRow.Controls.AddRange(
            [_connectButton, _startButton, _stopButton, _disconnectButton]);
        commandRow.Controls.Add(new Label
        {
            Text = "Requested Hz:",
            AutoSize = true,
            Margin = new Padding(18, 9, 4, 0)
        });
        _hzInput.DecimalPlaces = 1;
        _hzInput.Increment = 0.2M;
        _hzInput.Minimum = 0.2M;
        _hzInput.Maximum = 5.0M;
        _hzInput.Value = 5.0M;
        _hzInput.Width = 72;
        _hzInput.Margin = new Padding(0, 5, 0, 0);
        commandRow.Controls.Add(_hzInput);
        root.Controls.Add(commandRow, 0, 1);

        var outputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(0, 0, 0, 8)
        };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.Controls.Add(new Label
        {
            Text = "Output:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 0)
        }, 0, 0);
        _outputTextBox.Dock = DockStyle.Fill;
        _outputTextBox.Text = Path.GetFullPath(
            Path.Combine("data", "private", "bmw09-gui-sim"));
        outputRow.Controls.Add(_outputTextBox, 1, 0);
        ConfigureButton(_browseButton, "Browse…", 88);
        ConfigureButton(_openFolderButton, "Open last run", 112);
        outputRow.Controls.Add(_browseButton, 2, 0);
        outputRow.Controls.Add(_openFolderButton, 3, 0);
        root.Controls.Add(outputRow, 0, 2);

        _valuesGrid.Dock = DockStyle.Fill;
        _valuesGrid.AllowUserToAddRows = false;
        _valuesGrid.AllowUserToDeleteRows = false;
        _valuesGrid.AllowUserToResizeRows = false;
        _valuesGrid.ReadOnly = true;
        _valuesGrid.RowHeadersVisible = false;
        _valuesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _valuesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _valuesGrid.MultiSelect = false;
        _valuesGrid.Columns.Add("parameter", "Parameter");
        _valuesGrid.Columns.Add("value", "Value");
        _valuesGrid.Columns.Add("unit", "Unit");
        _valuesGrid.Columns.Add("quality", "Quality");
        root.Controls.Add(_valuesGrid, 0, 3);

        _eventLog.Dock = DockStyle.Fill;
        _eventLog.ReadOnly = true;
        _eventLog.BackColor = Color.FromArgb(248, 248, 248);
        _eventLog.Font = new Font("Consolas", 9F);
        root.Controls.Add(_eventLog, 0, 4);

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _stateLabel.AutoSize = true;
        _stateLabel.Font = new Font(Font, FontStyle.Bold);
        _errorLabel.AutoSize = true;
        _errorLabel.ForeColor = Color.DarkRed;
        _errorLabel.Margin = new Padding(18, 0, 0, 0);
        statusRow.Controls.Add(_stateLabel, 0, 0);
        statusRow.Controls.Add(_errorLabel, 1, 0);
        root.Controls.Add(statusRow, 0, 5);
    }

    private void WireEvents()
    {
        _stateMachine.Changed += (_, snapshot) => RunOnUi(() => ApplySnapshot(snapshot));
        _runtime.ProgressChanged += (_, progress) =>
            RunOnUi(() => ApplyProgress(progress));
        _runtime.Faulted += (_, safeError) => RunOnUi(() =>
        {
            LoggerUiSnapshot snapshot = _stateMachine.Snapshot;
            if (snapshot.State is not (LoggerUiState.Disconnected or LoggerUiState.Faulted))
            {
                _stateMachine.ReportRuntimeFault(safeError);
            }
            AppendEvent($"Runtime fault: {safeError}");
        });

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _startButton.Click += async (_, _) => await StartAsync();
        _stopButton.Click += async (_, _) => await StopAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _browseButton.Click += (_, _) => BrowseOutput();
        _openFolderButton.Click += (_, _) => OpenLastRun();
    }

    private void InitializeValuesGrid()
    {
        AddValueRow("engine.rpm", "1/min");
        AddValueRow("vehicle.speed", "km/h");
        AddValueRow("engine.coolant.temperature", "GradC");
        AddValueRow("electrical.battery.voltage", "V");
    }

    private void AddValueRow(string parameterId, string unit)
    {
        int index = _valuesGrid.Rows.Add(parameterId, "—", unit, "missing");
        _valueRows.Add(parameterId, _valuesGrid.Rows[index]);
    }

    private async Task ConnectAsync()
    {
        LoggerUiOperation operation;
        try
        {
            operation = _stateMachine.BeginConnect();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        AppendEvent("Connecting simulated backend…");
        try
        {
            await _runtime.ConnectAsync(_lifetime.Token);
            _stateMachine.CompleteConnect(operation, succeeded: true);
            AppendEvent("Simulated backend ready.");
        }
        catch (Exception exception)
        {
            CompleteFailure(
                operation,
                LoggerUiOperationKind.Connect,
                SafeError(exception));
        }
    }

    private async Task StartAsync()
    {
        LoggerUiOperation operation;
        try
        {
            operation = _stateMachine.BeginStart();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            string output = _outputTextBox.Text.Trim();
            double hz = decimal.ToDouble(_hzInput.Value);
            string runDirectory = await _runtime.StartAsync(
                output,
                hz,
                _lifetime.Token);
            _lastRunDirectory = runDirectory;
            _stateMachine.CompleteStart(operation, succeeded: true);
            AppendEvent($"Logging started: {runDirectory}");
        }
        catch (Exception exception)
        {
            CompleteFailure(
                operation,
                LoggerUiOperationKind.Start,
                SafeError(exception));
        }
    }

    private async Task StopAsync()
    {
        LoggerUiOperation operation;
        try
        {
            operation = _stateMachine.BeginStop();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        AppendEvent("Stopping and finalizing CSV…");
        try
        {
            SimulatedLoggerSessionResult result = await _runtime.StopAsync(
                _lifetime.Token);
            _lastRunDirectory = result.RunDirectory;
            _stateMachine.CompleteStop(operation, succeeded: true);
            AppendEvent(
                $"Completed: cycles={result.CyclesWritten}; " +
                $"samples={result.SamplesWritten}; " +
                $"duration={result.DurationSeconds:F1}s");
            AppendEvent($"CSV: {result.CsvPath}");
        }
        catch (Exception exception)
        {
            CompleteFailure(
                operation,
                LoggerUiOperationKind.Stop,
                SafeError(exception));
        }
    }

    private async Task DisconnectAsync()
    {
        LoggerUiOperation operation;
        try
        {
            operation = _stateMachine.BeginDisconnect();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            await _runtime.DisconnectAsync(_lifetime.Token);
            _stateMachine.CompleteDisconnect(operation, succeeded: true);
            ResetDisplayedValues();
            AppendEvent("Disconnected.");
        }
        catch (Exception exception)
        {
            CompleteFailure(
                operation,
                LoggerUiOperationKind.Disconnect,
                SafeError(exception));
        }
    }

    private void CompleteFailure(
        LoggerUiOperation operation,
        LoggerUiOperationKind kind,
        string safeError)
    {
        try
        {
            switch (kind)
            {
                case LoggerUiOperationKind.Connect:
                    _stateMachine.CompleteConnect(operation, false, safeError);
                    break;
                case LoggerUiOperationKind.Start:
                    _stateMachine.CompleteStart(operation, false, safeError);
                    break;
                case LoggerUiOperationKind.Stop:
                    _stateMachine.CompleteStop(operation, false, safeError);
                    break;
                case LoggerUiOperationKind.Disconnect:
                    _stateMachine.CompleteDisconnect(operation, false, safeError);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        catch (InvalidOperationException) when (
            _stateMachine.Snapshot.State == LoggerUiState.Faulted)
        {
            // A background runtime fault may win the race and invalidate completion.
        }
        AppendEvent($"{kind} failed: {safeError}");
    }

    private void ApplySnapshot(LoggerUiSnapshot snapshot)
    {
        _stateLabel.Text = $"State: {snapshot.State}";
        _stateLabel.ForeColor = snapshot.State switch
        {
            LoggerUiState.Logging => Color.DarkGreen,
            LoggerUiState.Faulted => Color.DarkRed,
            _ => SystemColors.ControlText
        };
        _errorLabel.Text = snapshot.LastError is null
            ? string.Empty
            : $"Error: {snapshot.LastError}";
        _connectButton.Enabled = snapshot.CanConnect;
        _startButton.Enabled = snapshot.CanStart;
        _stopButton.Enabled = snapshot.CanStop;
        _disconnectButton.Enabled = snapshot.CanDisconnect;
        bool configurationEnabled = snapshot.State is
            LoggerUiState.Disconnected or LoggerUiState.Ready;
        _outputTextBox.Enabled = configurationEnabled;
        _browseButton.Enabled = configurationEnabled;
        _hzInput.Enabled = configurationEnabled;
        _openFolderButton.Enabled =
            _lastRunDirectory is not null && Directory.Exists(_lastRunDirectory);
    }

    private void ApplyProgress(SimulatedLoggerProgress progress)
    {
        foreach ((string parameterId, double value) in progress.LatestValues)
        {
            if (_valueRows.TryGetValue(parameterId, out DataGridViewRow? row))
            {
                row.Cells[1].Value = value.ToString("F3", CultureInfo.InvariantCulture);
                row.Cells[3].Value = "valid";
            }
        }
        if (progress.CyclesWritten == 1 || progress.CyclesWritten % 25 == 0)
        {
            AppendEvent($"Progress: {progress.CyclesWritten} cycles");
        }
    }

    private void ResetDisplayedValues()
    {
        foreach (DataGridViewRow row in _valueRows.Values)
        {
            row.Cells[1].Value = "—";
            row.Cells[3].Value = "missing";
        }
    }

    private void BrowseOutput()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the root directory for private logger sessions",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_outputTextBox.Text)
                ? _outputTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputTextBox.Text = dialog.SelectedPath;
        }
    }

    private void OpenLastRun()
    {
        if (_lastRunDirectory is null || !Directory.Exists(_lastRunDirectory))
        {
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = _lastRunDirectory,
            UseShellExecute = true
        });
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            if (_runtime.IsRunning)
            {
                if (_stateMachine.Snapshot.State == LoggerUiState.Logging)
                {
                    await StopAsync();
                }
                else
                {
                    await _runtime.StopAsync();
                }
            }
            if (_runtime.IsConnected &&
                _stateMachine.Snapshot.State is LoggerUiState.Ready or LoggerUiState.Faulted)
            {
                await DisconnectAsync();
            }
            await _runtime.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppendEvent($"Shutdown cleanup: {SafeError(exception)}");
        }
        finally
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _shutdownComplete = true;
            BeginInvoke(new Action(Close));
        }
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private void AppendEvent(string text)
    {
        _eventLog.AppendText(
            $"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        _eventLog.SelectionStart = _eventLog.TextLength;
        _eventLog.ScrollToCaret();
    }

    private static void ConfigureButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 30;
        button.Margin = new Padding(0, 2, 8, 2);
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "OPERATION_CANCELLED",
        UnauthorizedAccessException => "OUTPUT_ACCESS_DENIED",
        IOException => "OUTPUT_IO_ERROR",
        ArgumentException => "INVALID_CONFIGURATION",
        InvalidOperationException => "INVALID_STATE",
        _ => "UNEXPECTED_GUI_ERROR"
    };
}
