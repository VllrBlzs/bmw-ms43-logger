using System.Diagnostics;
using System.Globalization;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.App;

internal sealed class MainForm : Form
{
    private readonly LoggerUiStateMachine _stateMachine = new();
    private readonly ParameterSelectionModel _selection = new();
    private readonly SimulatedLoggerRuntime _runtime = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _modeLabel = new();
    private readonly Label _stateLabel = new();
    private readonly Label _errorLabel = new();
    private readonly Label _selectionSummaryLabel = new();
    private readonly Label _searchResultLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _browseButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _selectApprovedButton = new();
    private readonly Button _clearSelectionButton = new();
    private readonly TextBox _outputTextBox = new();
    private readonly TextBox _searchTextBox = new();
    private readonly NumericUpDown _hzInput = new();
    private readonly DataGridView _parameterGrid = new();
    private readonly RichTextBox _eventLog = new();
    private readonly Dictionary<string, DataGridViewRow> _parameterRows =
        new(StringComparer.Ordinal);
    private string? _lastRunDirectory;
    private bool _updatingGrid;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainForm()
    {
        Text = "BMW MS43 Read-only Logger — BMW-10 parameter manager (offline)";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 680);
        Size = new Size(1320, 820);
        Font = new Font("Segoe UI", 9F);
        BuildLayout();
        WireEvents();
        InitializeParameterGrid();
        ApplySelectionSnapshot(_selection.Snapshot);
        ApplySnapshot(_stateMachine.Snapshot);
        AppendEvent(
            "Offline GUI ready. 28 catalog entries; 22 approved and selectable. " +
            "No ECU interface has been created.");
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
            RowCount = 7
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 64F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 36F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _modeLabel.AutoSize = true;
        _modeLabel.Font = new Font(Font, FontStyle.Bold);
        _modeLabel.ForeColor = Color.DarkOrange;
        _modeLabel.Text =
            "OFFLINE SIMULATION — COMMUNICATION_ATTEMPTED=False — BMW-10";
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
            Padding = new Padding(0, 0, 0, 6)
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
            Path.Combine("data", "private", "bmw10-gui-sim"));
        outputRow.Controls.Add(_outputTextBox, 1, 0);
        ConfigureButton(_browseButton, "Browse…", 88);
        ConfigureButton(_openFolderButton, "Open last run", 112);
        outputRow.Controls.Add(_browseButton, 2, 0);
        outputRow.Controls.Add(_openFolderButton, 3, 0);
        root.Controls.Add(outputRow, 0, 2);

        var selectionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 0, 0, 6)
        };
        var selectionCommands = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true
        };
        selectionCommands.Controls.Add(new Label
        {
            Text = "Search:",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        _searchTextBox.Width = 300;
        _searchTextBox.PlaceholderText =
            "display name, internal name, ID, unit or status";
        _searchTextBox.Margin = new Padding(0, 4, 10, 0);
        selectionCommands.Controls.Add(_searchTextBox);
        ConfigureButton(_selectApprovedButton, "Select approved", 128);
        ConfigureButton(_clearSelectionButton, "Clear selection", 124);
        selectionCommands.Controls.Add(_selectApprovedButton);
        selectionCommands.Controls.Add(_clearSelectionButton);
        _searchResultLabel.AutoSize = true;
        _searchResultLabel.Margin = new Padding(10, 8, 0, 0);
        selectionCommands.Controls.Add(_searchResultLabel);
        selectionPanel.Controls.Add(selectionCommands, 0, 0);

        _selectionSummaryLabel.AutoSize = true;
        _selectionSummaryLabel.Font = new Font(Font, FontStyle.Bold);
        _selectionSummaryLabel.ForeColor = Color.DarkSlateBlue;
        _selectionSummaryLabel.Margin = new Padding(0, 4, 0, 2);
        selectionPanel.Controls.Add(_selectionSummaryLabel, 0, 1);
        root.Controls.Add(selectionPanel, 0, 3);

        ConfigureParameterGrid();
        root.Controls.Add(_parameterGrid, 0, 4);

        _eventLog.Dock = DockStyle.Fill;
        _eventLog.ReadOnly = true;
        _eventLog.BackColor = Color.FromArgb(248, 248, 248);
        _eventLog.Font = new Font("Consolas", 9F);
        root.Controls.Add(_eventLog, 0, 5);

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
        root.Controls.Add(statusRow, 0, 6);
    }

    private void ConfigureParameterGrid()
    {
        _parameterGrid.Dock = DockStyle.Fill;
        _parameterGrid.AllowUserToAddRows = false;
        _parameterGrid.AllowUserToDeleteRows = false;
        _parameterGrid.AllowUserToResizeRows = false;
        _parameterGrid.RowHeadersVisible = false;
        _parameterGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _parameterGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _parameterGrid.MultiSelect = false;
        _parameterGrid.AutoGenerateColumns = false;

        _parameterGrid.Columns.AddRange(
        [
            new DataGridViewCheckBoxColumn
            {
                Name = "selected",
                HeaderText = "Use",
                Width = 48,
                ThreeState = false
            },
            TextColumn("display", "Display name", 205),
            TextColumn("parameter", "Parameter ID", 230),
            TextColumn("internal", "Internal name", 190),
            TextColumn("value", "Latest value", 100),
            TextColumn("unit", "Unit", 72),
            TextColumn("type", "Type", 62),
            TextColumn("support", "Support", 220),
            TextColumn("quality", "Quality", 90)
        ]);
    }

    private void WireEvents()
    {
        _stateMachine.Changed += (_, snapshot) =>
            RunOnUi(() => ApplySnapshot(snapshot));
        _selection.Changed += (_, snapshot) =>
            RunOnUi(() => ApplySelectionSnapshot(snapshot));
        _runtime.ProgressChanged += (_, progress) =>
            RunOnUi(() => ApplyProgress(progress));
        _runtime.Faulted += (_, safeError) => RunOnUi(() =>
        {
            LoggerUiSnapshot snapshot = _stateMachine.Snapshot;
            if (snapshot.State is not (
                    LoggerUiState.Disconnected or LoggerUiState.Faulted))
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
        _selectApprovedButton.Click += (_, _) => _selection.SelectAllApproved();
        _clearSelectionButton.Click += (_, _) => _selection.Clear();
        _searchTextBox.TextChanged += (_, _) => ApplySearchFilter();
        _hzInput.ValueChanged += (_, _) =>
            ApplySelectionSnapshot(_selection.Snapshot);
        _parameterGrid.CellBeginEdit += ParameterGridCellBeginEdit;
        _parameterGrid.CellValueChanged += ParameterGridCellValueChanged;
        _parameterGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_parameterGrid.IsCurrentCellDirty)
            {
                _parameterGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
    }

    private void InitializeParameterGrid()
    {
        var selected = new HashSet<ParameterId>(
            _selection.Snapshot.SelectedParameters);
        _updatingGrid = true;
        try
        {
            foreach (LoggerParameterDefinition definition in
                     Ms43ParameterCatalog.Entries)
            {
                bool isSelected = selected.Contains(definition.ParameterId);
                string quality = definition.IsSelectable
                    ? isSelected ? "missing" : "not selected"
                    : "withheld";
                int index = _parameterGrid.Rows.Add(
                    isSelected,
                    definition.DisplayNameHu,
                    definition.ParameterId.Value,
                    definition.InternalName,
                    "—",
                    definition.Unit,
                    definition.DataType,
                    definition.SupportStatus,
                    quality);
                DataGridViewRow row = _parameterGrid.Rows[index];
                row.Tag = definition;
                for (int column = 1; column < row.Cells.Count; column++)
                {
                    row.Cells[column].ReadOnly = true;
                }
                row.Cells[0].ReadOnly = !definition.IsSelectable;
                if (!definition.IsSelectable)
                {
                    row.DefaultCellStyle.ForeColor = Color.DimGray;
                    row.Cells[0].Style.BackColor = Color.Gainsboro;
                    row.Cells[7].Style.ForeColor = Color.DarkRed;
                }
                _parameterRows.Add(definition.ParameterId.Value, row);
            }
        }
        finally
        {
            _updatingGrid = false;
        }
        ApplySearchFilter();
    }

    private void ParameterGridCellBeginEdit(
        object? sender,
        DataGridViewCellCancelEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex != _parameterGrid.Columns["selected"]!.Index ||
            _parameterGrid.Rows[e.RowIndex].Tag is not
                LoggerParameterDefinition definition)
        {
            e.Cancel = true;
            return;
        }

        LoggerUiState state = _stateMachine.Snapshot.State;
        bool configurationState = state is
            LoggerUiState.Disconnected or LoggerUiState.Ready;
        e.Cancel = !configurationState ||
                   _selection.Snapshot.IsLocked ||
                   !definition.IsSelectable;
    }

    private void ParameterGridCellValueChanged(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (_updatingGrid ||
            e.RowIndex < 0 ||
            e.ColumnIndex != _parameterGrid.Columns["selected"]!.Index)
        {
            return;
        }

        DataGridViewRow row = _parameterGrid.Rows[e.RowIndex];
        if (row.Tag is not LoggerParameterDefinition definition)
        {
            return;
        }

        bool selected = row.Cells["selected"].Value is bool value && value;
        try
        {
            _selection.SetSelected(definition.ParameterId, selected);
        }
        catch (InvalidOperationException)
        {
            AppendEvent($"Selection rejected: {definition.ParameterId.Value}");
            ApplySelectionSnapshot(_selection.Snapshot);
        }
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
            IReadOnlyList<ParameterId> selected =
                _selection.CaptureForSession();
            ResetValuesForNewRun(selected);
            string output = _outputTextBox.Text.Trim();
            double hz = decimal.ToDouble(_hzInput.Value);
            SamplingBudgetEstimate budget = SamplingBudgetEstimator.Estimate(
                selected,
                hz);
            string runDirectory = await _runtime.StartAsync(
                output,
                hz,
                selected,
                _lifetime.Token);
            _lastRunDirectory = runDirectory;
            _stateMachine.CompleteStart(operation, succeeded: true);
            AppendEvent(
                $"Selection locked: {selected.Count} parameters; " +
                $"live-model jobs={budget.PlannedJobs}; " +
                $"ceiling={budget.EstimatedMaximumHz:F2} Hz.");
            AppendEvent($"Logging started: {runDirectory}");
        }
        catch (Exception exception)
        {
            _selection.ReleaseSession();
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
            _selection.ReleaseSession();
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
            _selection.ReleaseSession();
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
        ApplyCommandAvailability(snapshot, _selection.Snapshot);
    }

    private void ApplySelectionSnapshot(ParameterSelectionSnapshot snapshot)
    {
        if (_parameterRows.Count > 0)
        {
            var selected = new HashSet<ParameterId>(snapshot.SelectedParameters);
            _updatingGrid = true;
            try
            {
                foreach (LoggerParameterDefinition definition in
                         Ms43ParameterCatalog.Entries)
                {
                    DataGridViewRow row =
                        _parameterRows[definition.ParameterId.Value];
                    bool isSelected = selected.Contains(definition.ParameterId);
                    row.Cells["selected"].Value = isSelected;
                    if (!definition.IsSelectable)
                    {
                        row.Cells["value"].Value = "—";
                        row.Cells["quality"].Value = "withheld";
                    }
                    else if (!isSelected)
                    {
                        row.Cells["value"].Value = "—";
                        row.Cells["quality"].Value = "not selected";
                    }
                    else if (string.Equals(
                                 row.Cells["quality"].Value?.ToString(),
                                 "not selected",
                                 StringComparison.Ordinal))
                    {
                        row.Cells["quality"].Value = "missing";
                    }
                }
            }
            finally
            {
                _updatingGrid = false;
            }
        }

        double requestedHz = decimal.ToDouble(_hzInput.Value);
        SamplingBudgetEstimate budget = SamplingBudgetEstimator.Estimate(
            snapshot.SelectedParameters,
            requestedHz);
        string lockText = snapshot.IsLocked ? "LOCKED FOR ACTIVE RUN" : "editable";
        _selectionSummaryLabel.Text = budget.SelectedParameters == 0
            ? $"Selected 0/{snapshot.ApprovedCount} approved " +
              $"({snapshot.CatalogCount} catalog) — Start blocked — {lockText}"
            : $"Selected {budget.SelectedParameters}/{snapshot.ApprovedCount} approved " +
              $"({snapshot.CatalogCount} catalog) — LIVE BUDGET MODEL: " +
              $"jobs={budget.PlannedJobs}, results={budget.ResultRequests}, " +
              $"cycle≈{budget.EstimatedCycleMilliseconds:F0} ms, " +
              $"ceiling≈{budget.EstimatedMaximumHz:F2} Hz, " +
              $"estimated effective≈{budget.EstimatedEffectiveHz:F2} Hz — {lockText}";
        ApplyCommandAvailability(_stateMachine.Snapshot, snapshot);
    }

    private void ApplyCommandAvailability(
        LoggerUiSnapshot state,
        ParameterSelectionSnapshot selection)
    {
        _connectButton.Enabled = state.CanConnect;
        _startButton.Enabled = state.CanStart && selection.SelectedCount > 0;
        _stopButton.Enabled = state.CanStop;
        _disconnectButton.Enabled = state.CanDisconnect;
        bool configurationEnabled = state.State is
            LoggerUiState.Disconnected or LoggerUiState.Ready;
        bool selectionEnabled = configurationEnabled && !selection.IsLocked;
        _outputTextBox.Enabled = configurationEnabled;
        _browseButton.Enabled = configurationEnabled;
        _hzInput.Enabled = configurationEnabled;
        _selectApprovedButton.Enabled = selectionEnabled;
        _clearSelectionButton.Enabled = selectionEnabled;
        _openFolderButton.Enabled =
            _lastRunDirectory is not null && Directory.Exists(_lastRunDirectory);
    }

    private void ApplySearchFilter()
    {
        if (_parameterRows.Count == 0)
        {
            return;
        }

        _parameterGrid.CurrentCell = null;
        int visible = 0;
        foreach (LoggerParameterDefinition definition in Ms43ParameterCatalog.Entries)
        {
            bool match = definition.Matches(_searchTextBox.Text);
            _parameterRows[definition.ParameterId.Value].Visible = match;
            if (match)
            {
                visible++;
            }
        }
        _searchResultLabel.Text =
            $"Visible: {visible}/{Ms43ParameterCatalog.Entries.Count}";
    }

    private void ApplyProgress(SimulatedLoggerProgress progress)
    {
        foreach ((string parameterId, double value) in progress.LatestValues)
        {
            if (_parameterRows.TryGetValue(parameterId, out DataGridViewRow? row))
            {
                row.Cells["value"].Value = value.ToString(
                    "F3",
                    CultureInfo.InvariantCulture);
                row.Cells["quality"].Value = "valid";
            }
        }
        if (progress.CyclesWritten == 1 || progress.CyclesWritten % 25 == 0)
        {
            AppendEvent($"Progress: {progress.CyclesWritten} cycles");
        }
    }

    private void ResetValuesForNewRun(IReadOnlyCollection<ParameterId> selected)
    {
        var selectedSet = new HashSet<ParameterId>(selected);
        foreach (LoggerParameterDefinition definition in Ms43ParameterCatalog.Entries)
        {
            DataGridViewRow row = _parameterRows[definition.ParameterId.Value];
            row.Cells["value"].Value = "—";
            row.Cells["quality"].Value = !definition.IsSelectable
                ? "withheld"
                : selectedSet.Contains(definition.ParameterId)
                    ? "missing"
                    : "not selected";
        }
    }

    private void ResetDisplayedValues() =>
        ResetValuesForNewRun(_selection.Snapshot.SelectedParameters);

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
                    _selection.ReleaseSession();
                }
            }
            if (_runtime.IsConnected &&
                _stateMachine.Snapshot.State is
                    LoggerUiState.Ready or LoggerUiState.Faulted)
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
            _selection.ReleaseSession();
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

    private static DataGridViewTextBoxColumn TextColumn(
        string name,
        string header,
        int width) =>
        new()
        {
            Name = name,
            HeaderText = header,
            Width = width,
            ReadOnly = true
        };

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
