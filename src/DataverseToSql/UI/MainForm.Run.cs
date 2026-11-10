using System.Diagnostics;
using DataverseToSql.Sync;
using DataverseToSql.UI;
using Microsoft.Data.SqlClient;

namespace DataverseToSql;

// Step 5: job overview, options, interrupted-run choice, live run dashboard, run history and scheduling.
public sealed partial class MainForm
{
    private const int HistoryRows = 15;
    private const int RateWindowSeconds = 5;

    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly NumericUpDown _pageSize = new() { Minimum = 50, Maximum = 5000, Increment = 50, Value = 5000 };
    private readonly NumericUpDown _parallelism = new() { Minimum = 1, Maximum = 32, Value = 8 };
    private readonly NumericUpDown _maxAttempts = new() { Minimum = 1, Maximum = 50, Value = 5 };
    private readonly CheckBox _detectDeletes = new() { Text = "Detect deletes (compares all ids, slower)", AutoSize = true };

    private readonly Label _runCheck = WizardLayout.Helper("", Theme.Text, new Padding(3, 4, 3, 6));
    private readonly RadioButton _resumeContinue = new() { Text = "Continue the interrupted run from where it stopped (recommended)", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 0) };
    private readonly RadioButton _resumeRestart = new() { Text = "Start again from the beginning", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
    private readonly Label _noDeleteHelp = WizardLayout.Helper(
        "Nothing is deleted: existing rows in the destination stay and are updated or added again.", null, new Padding(20, 0, 3, 6));
    private readonly LinkLabel _recheck = new() { Text = "Check again", AutoSize = true, Margin = new Padding(3, 2, 3, 4) };

    // Job overview
    private readonly Label _jobSource = OverviewValue(), _jobSourceDetail = OverviewDetail();
    private readonly Label _jobDest = OverviewValue(), _jobDestDetail = OverviewDetail();
    private readonly FlowLayoutPanel _jobChips = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };

    // Live run dashboard
    private readonly Button _start = Theme.Primary(WizardLayout.MakeButton("▶  &Start sync", 150));
    private readonly Button _stop = WizardLayout.MakeButton("■  S&top", 100);
    private readonly Label _pStage = new() { AutoSize = true, Font = Theme.CardTitle, ForeColor = Theme.Text, Text = "Not started", UseMnemonic = false, Margin = new Padding(0, 10, 3, 4) };
    private readonly Label _pPercent = new() { AutoSize = true, Font = Theme.CardTitle, ForeColor = Theme.Text, Text = "", Anchor = AnchorStyles.Right, Margin = new Padding(3, 10, 0, 4) };
    private readonly ProgressStrip _progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 14) };
    private readonly KpiTile _kRows = new("Rows written"), _kSpeed = new("Throughput (average)"), _kElapsed = new("Elapsed"),
        _kEta = new("Time remaining"), _kWorkers = new("Workers busy"), _kSlices = new("Work items done");
    private readonly Sparkline _spark = new() { Unit = "rows/s", Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 8) };
    private readonly Label _summary = WizardLayout.Helper("", Theme.Text, new Padding(0, 4, 0, 4));
    private readonly TextBox _scheduleCmd = new() { ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.PageBack, Font = Theme.Mono };

    // History
    private readonly DataGridView _history = new() { Height = 230, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Run history" };
    private readonly Label _historyInfo = WizardLayout.Helper("Test the destination (step 3) to see earlier runs of this table.", null, new Padding(0, 6, 0, 0));
    private readonly LinkLabel _refreshHistory = new() { Text = "Refresh", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 1000 };
    private readonly Stopwatch _runWatch = new();
    private readonly Queue<(double Seconds, long Rows)> _rateWindow = new();
    private SyncProgress? _lastProgress;
    private Card _optionsGroup = null!;
    private Card _runChoiceGroup = null!;

    private InterruptedRunInfo? _interrupted;
    private bool _checkingRun;
    private int _checkVersion;

    private static Label OverviewValue() => new() { AutoSize = false, Height = 22, Dock = DockStyle.Fill, AutoEllipsis = true, Font = Theme.CardTitle, ForeColor = Theme.Text, UseMnemonic = false, Margin = new Padding(0, 2, 3, 0) };

    private static Label OverviewDetail() => new() { AutoSize = false, Height = 18, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.TextMuted, UseMnemonic = false, Margin = new Padding(0, 2, 3, 0) };

    private Control BuildRunPage()
    {
        var page = WizardLayout.CreatePage("Options and start",
            "Check what will run, choose the options and click Start sync. Progress is saved after every page, so a run can be stopped "
            + "and continued at any time – also after a crash or a network error.");
        _runPage = page;

        WizardLayout.AddBlock(page, WizardLayout.Group("What will run", BuildJobOverview()));

        _mode.Items.AddRange(new object[] { "Full - copy all rows", "Incremental - only rows changed since the last completed run" });
        var options = WizardLayout.FieldGrid();
        options.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, 120);
        WizardLayout.AddField(options, "Mode", _mode, "Full for the first run; Incremental afterwards (modifiedon watermark).");
        WizardLayout.AddField(options, "Page size", _pageSize);
        WizardLayout.AddField(options, "Parallelism", _parallelism);
        WizardLayout.AddField(options, "Max attempts", _maxAttempts);
        WizardLayout.AddWide(options, _detectDeletes);
        _toolTip.SetToolTip(_pageSize, "Rows per request (50-5000). Capped at 500 when file content (documentbody) is selected.");
        _toolTip.SetToolTip(_parallelism, "Parallel readers; capped by the server's recommended value.");
        _toolTip.SetToolTip(_maxAttempts, "Automatic retries after an error; each retry continues from the saved progress.");
        _toolTip.SetToolTip(_detectDeletes, "When on, rows deleted in the source are also removed from the destination table.");
        _optionsGroup = WizardLayout.Group("Options", options);
        _optionsGroup.Dock = DockStyle.Fill;

        var choice = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        choice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        choice.Controls.Add(_runCheck, 0, 0);
        choice.Controls.Add(_resumeContinue, 0, 1);
        choice.Controls.Add(_resumeRestart, 0, 2);
        choice.Controls.Add(_noDeleteHelp, 0, 3);
        choice.Controls.Add(_recheck, 0, 4);
        _runChoiceGroup = WizardLayout.Group("Interrupted run", choice);
        _runChoiceGroup.Dock = DockStyle.Fill;

        var top = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _optionsGroup.Margin = new Padding(0, 0, 6, 12);
        _runChoiceGroup.Margin = new Padding(6, 0, 0, 12);
        top.Controls.Add(_optionsGroup, 0, 0);
        top.Controls.Add(_runChoiceGroup, 1, 0);
        _optionsRow = WizardLayout.AddBlock(page, top);

        _dashboardCard = WizardLayout.AddBlock(page, WizardLayout.Group("Live details", BuildDashboard()));
        WizardLayout.AddBlock(page, WizardLayout.Group("Run history and scheduling", BuildHistory()));
        WizardLayout.AddFiller(page);

        // Fixed footer: Start / Stop, the current stage and the progress bar never scroll out of view.
        var actions = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _start.Margin = new Padding(0, 0, 6, 0);
        _stop.Margin = new Padding(0, 0, 16, 0);
        _pStage.Anchor = AnchorStyles.Left;
        _pStage.Margin = new Padding(0, 0, 3, 0);
        _pPercent.Margin = new Padding(3, 0, 0, 0);
        actions.Controls.Add(_start, 0, 0);
        actions.Controls.Add(_stop, 1, 0);
        actions.Controls.Add(_pStage, 2, 0);
        actions.Controls.Add(_pPercent, 3, 0);
        _toolTip.SetToolTip(_stop, "Stops after the current pages. Progress is kept; Start continues later.");
        _progress.Margin = new Padding(0, 8, 0, 0);
        _summary.Margin = new Padding(0, 6, 0, 0);
        return WizardLayout.WithFooter(page, actions, _progress, _summary);
    }

    private TableLayoutPanel _runPage = null!;
    private Card _dashboardCard = null!;
    private Control _optionsRow = null!;

    // Source -> destination with the key facts of the job as chips.
    private Control BuildJobOverview()
    {
        var flow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        static Control Endpoint(string caption, Label value, Label detail)
        {
            var box = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
                BackColor = Theme.PageBack, Padding = new Padding(12, 10, 12, 10), Margin = Padding.Empty,
            };
            box.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.TextMuted, Font = Theme.Small, Margin = Padding.Empty }, 0, 0);
            box.Controls.Add(value, 0, 1);
            box.Controls.Add(detail, 0, 2);
            return box;
        }

        flow.Controls.Add(Endpoint("FROM", _jobSource, _jobSourceDetail), 0, 0);
        flow.Controls.Add(new Label
        {
            Text = "➜", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = _arrowFont, ForeColor = Theme.Accent, AccessibleName = "copies to",
        }, 1, 0);
        flow.Controls.Add(Endpoint("TO", _jobDest, _jobDestDetail), 2, 0);
        flow.Controls.Add(_jobChips, 0, 1);
        flow.SetColumnSpan(_jobChips, 3);
        return flow;
    }

    private readonly Font _arrowFont = new("Segoe UI Symbol", 18f);

    private Control BuildDashboard()
    {
        var run = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        run.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var tiles = new TableLayoutPanel { ColumnCount = 6, RowCount = 1, Height = 96, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        tiles.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        var all = new[] { _kRows, _kSpeed, _kElapsed, _kEta, _kWorkers, _kSlices };
        for (var i = 0; i < all.Length; i++)
        {
            tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / all.Length));
            all[i].Dock = DockStyle.Fill;
            all[i].Margin = new Padding(i == 0 ? 0 : 5, 0, i == all.Length - 1 ? 0 : 5, 0);
            tiles.Controls.Add(all[i], i, 0);
        }
        run.Controls.Add(tiles);

        run.Controls.Add(new Label { Text = "Throughput (rows per second, 5-second average)", AutoSize = true, Font = Theme.BaseSemibold, ForeColor = Theme.Text, Margin = new Padding(0, 4, 3, 0) });
        _spark.Height = 96;
        run.Controls.Add(_spark);
        ResetDashboard();
        return run;
    }

    private Control BuildHistory()
    {
        Theme.StyleGrid(_history);
        _history.ReadOnly = true;
        _history.AllowUserToAddRows = false;
        _history.AllowUserToDeleteRows = false;
        _history.AllowUserToResizeRows = false;
        _history.RowHeadersVisible = false;
        _history.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _history.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _history.StandardTab = true;
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Started", FillWeight = 22 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode", FillWeight = 12 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 18 });
        _history.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Rows", FillWeight = 14, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Duration", FillWeight = 12 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Note", FillWeight = 40 });
        foreach (DataGridViewColumn c in _history.Columns)
        {
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        _history.CellPainting += (_, e) => PaintPillCell(_history, 2, e);

        var box = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _history.Height = 190;
        box.Controls.Add(_history, 0, 0);
        box.SetColumnSpan(_history, 2);
        box.Controls.Add(_historyInfo, 0, 1);
        box.Controls.Add(_refreshHistory, 1, 1);

        // Scheduling: the command line for Task Scheduler.
        box.Controls.Add(new Label { Text = "Run on a schedule", AutoSize = true, Font = Theme.BaseSemibold, ForeColor = Theme.Text, Margin = new Padding(0, 12, 3, 4) }, 0, 2);
        _scheduleCmd.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _scheduleCmd.AccessibleName = "Command line for scheduled runs";
        box.Controls.Add(_scheduleCmd, 0, 3);
        box.SetColumnSpan(_scheduleCmd, 2);
        var scheduleHelp = WizardLayout.Helper(
            $"Runs the saved configuration without a window, e.g. from Task Scheduler (exit code 0 = success). Needs non-interactive sign-in: "
            + $"Client secret auth with {SecretEnvVar} set, and a SQL login or Entra default for the destination.", null, new Padding(0, 4, 3, 0));
        box.Controls.Add(scheduleHelp, 0, 4);
        box.SetColumnSpan(scheduleHelp, 2);
        return box;
    }

    /// <summary>Paints the cells of <paramref name="column"/> whose Tag is a tone as a pill with icon + word (colour is never the only cue).</summary>
    private static void PaintPillCell(DataGridView grid, int column, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != column || e.Graphics is null || grid.Rows[e.RowIndex].Cells[column].Tag is not Theme.Tone tone)
        {
            return;
        }
        e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
        var bounds = new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y, e.CellBounds.Width - 8, e.CellBounds.Height);
        var clip = e.Graphics.Clip;
        e.Graphics.SetClip(e.CellBounds);
        Theme.DrawPill(e.Graphics, bounds, e.FormattedValue as string ?? "", tone);
        e.Graphics.Clip = clip;
        e.Handled = true;
    }

    private void WireRunEvents()
    {
        HookInputs(_mode, _pageSize, _parallelism, _maxAttempts, _detectDeletes);
        _start.Click += async (_, _) => await StartSyncAsync();
        _stop.Click += (_, _) => CancelRun();
        _recheck.LinkClicked += async (_, _) => await CheckInterruptedRunAsync();
        _refreshHistory.LinkClicked += async (_, _) => await CheckInterruptedRunAsync();
        _elapsedTimer.Tick += (_, _) => OnRunTick();
        ShowInterruptedChoice(null, message: "Open this step to check for an interrupted run.");
    }

    private void ApplyRunSettings(AppSettings s)
    {
        _mode.SelectedIndex = s.Mode == SyncMode.Incremental ? 1 : 0;
        _pageSize.Value = Math.Clamp(s.PageSize, (int)_pageSize.Minimum, (int)_pageSize.Maximum);
        _parallelism.Value = Math.Clamp(s.Parallelism, (int)_parallelism.Minimum, (int)_parallelism.Maximum);
        _maxAttempts.Value = Math.Clamp(s.MaxAttempts, (int)_maxAttempts.Minimum, (int)_maxAttempts.Maximum);
        _detectDeletes.Checked = s.DetectDeletes;
    }

    /// <summary>Enables the step 5 controls for the current state and refreshes the job overview.</summary>
    private void UpdateRunUi()
    {
        if (_optionsGroup is null)
        {
            return;
        }
        var idle = IsIdle;
        _optionsGroup.Enabled = idle;
        _optionsRow.Visible = !_running; // while running, the live figures move up into view
        _runChoiceGroup.Enabled = idle && !_checkingRun;
        _start.Enabled = idle && !_checkingRun;
        _stop.Enabled = _running && _runCts is { IsCancellationRequested: false };
        _scheduleCmd.Text = $"\"{Environment.ProcessPath}\" --run \"{_configPath ?? "<save the configuration first>"}\"";
        if (_step == StepRun)
        {
            RefreshJobOverview();
        }
    }

    private void RefreshJobOverview()
    {
        var entity = _entity.Text.Trim();
        _jobSource.Text = entity.Length > 0 ? entity : "(no table)";
        if (SelectedSource == SourceKind.SqlTable)
        {
            _jobSourceDetail.Text = $"SQL table in [{_srcDatabase.Text.Trim()}] on {ServerName(_srcSqlConn.Text)}";
        }
        else
        {
            var host = Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var u) ? u.Host : "(no environment)";
            _jobSourceDetail.Text = $"Dataverse {host} · read via {(SelectedSource == SourceKind.Tds ? "TDS endpoint" : "SDK (Web API)")}";
        }
        _jobDest.Text = $"{_schema.Text.Trim()}.{_table.Text.Trim()}";
        _jobDestDetail.Text = $"[{_database.Text.Trim()}] on {ServerName(_sqlConn.Text)}"
            + (!_destLoaded ? "" : _destColumns is null ? " · new table, created on the first run" : $" · existing table, {_destColumns.Count} columns");

        var chips = new List<(string Text, Theme.Tone Tone)>
        {
            (_mode.SelectedIndex == 1 ? "Incremental (changed rows only)" : "Full copy", Theme.Tone.Info),
        };
        if (_loadedEntity is not null)
        {
            chips.Add(($"{_checked.Count(kv => kv.Value)} of {_checked.Count} columns", Theme.Tone.Neutral));
            var renamed = _mapping.Count(kv => !kv.Key.Equals(kv.Value, StringComparison.OrdinalIgnoreCase));
            if (renamed > 0) chips.Add(($"{renamed} renamed", Theme.Tone.Neutral));
            if (_checked.TryGetValue(DocumentBody, out var doc) && doc) chips.Add(("Includes file content", Theme.Tone.Warning));
        }
        chips.Add(($"{_parallelism.Value} parallel readers", Theme.Tone.Neutral));
        if (_detectDeletes.Checked) chips.Add(("Delete detection on", Theme.Tone.Warning));

        var key = string.Join("|", chips.Select(c => c.Text + c.Tone));
        if (Equals(_jobChips.Tag, key)) return;
        _jobChips.Tag = key;
        _jobChips.SuspendLayout();
        foreach (var c in _jobChips.Controls.Cast<Control>().ToList()) c.Dispose();
        foreach (var (text, tone) in chips)
        {
            var pill = new Pill();
            _jobChips.Controls.Add(pill);
            pill.Set(text, tone);
        }
        _jobChips.ResumeLayout();
    }

    private static string ServerName(string connectionString)
    {
        try
        {
            var ds = new SqlConnectionStringBuilder(connectionString).DataSource;
            return string.IsNullOrWhiteSpace(ds) ? "(no server)" : ds.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0];
        }
        catch (ArgumentException)
        {
            return "(server)";
        }
    }

    private async Task CheckInterruptedRunAsync()
    {
        if (_running)
        {
            return;
        }
        var version = ++_checkVersion;
        if (DestinationProblem() is not null)
        {
            _interrupted = null;
            ShowInterruptedChoice(null, message: "Test the destination (step 3) first.");
            _history.Rows.Clear();
            _historyInfo.Text = "Test the destination (step 3) to see earlier runs of this table.";
            return;
        }
        var s = CollectSettings();
        _checkingRun = true;
        _interrupted = null;
        ShowInterruptedChoice(null, message: "Checking the destination for an interrupted run...");
        UpdateRunUi();
        try
        {
            var ct = _formCts.Token;
            var run = await Task.Run(() => SyncEngine.FindInterruptedRunAsync(s, ct), ct);
            if (IsDisposed || version != _checkVersion)
            {
                return;
            }
            _interrupted = run;
            ShowInterruptedChoice(run, null);
            var history = await Task.Run(() => SyncEngine.GetRunHistoryAsync(s, HistoryRows, ct), ct);
            if (!IsDisposed && version == _checkVersion)
            {
                ShowHistory(history, s);
            }
        }
        catch (OperationCanceledException) when (_formCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Checking for an interrupted run failed.", ex);
            if (!IsDisposed && version == _checkVersion)
            {
                _interrupted = null;
                ShowInterruptedChoice(null, $"Could not check for an interrupted run: {ex.Message}\nIf one exists, Start continues it.", error: true);
            }
        }
        finally
        {
            if (!IsDisposed && version == _checkVersion)
            {
                _checkingRun = false;
                UpdateRunUi();
            }
        }
    }

    private void ShowHistory(IReadOnlyList<RunHistoryItem> runs, AppSettings s)
    {
        _history.Rows.Clear();
        foreach (var r in runs)
        {
            var (status, tone) = r.Status switch
            {
                "Completed" => ("Completed", Theme.Tone.Success),
                "Running" => ("Interrupted", Theme.Tone.Warning),
                "Abandoned" => ("Replaced", Theme.Tone.Muted),
                _ => (r.Status, Theme.Tone.Danger),
            };
            var note = r.Status == "Abandoned" ? "A newer run started from the beginning."
                : !string.IsNullOrWhiteSpace(r.LastError) ? r.LastError!
                : r.Status == "Running" ? "Start continues this run." : "";
            var index = _history.Rows.Add(r.StartedUtc.ToLocalTime().ToString("g"), r.Mode, status, r.RowsWritten.ToString("N0"),
                FormatElapsed(r.Duration), note);
            _history.Rows[index].Cells[2].Tag = tone;
            _history.Rows[index].Cells[5].ToolTipText = note;
        }
        _historyInfo.Text = runs.Count == 0
            ? $"No runs yet for [{s.DatabaseName}].[{s.SchemaName}].[{s.TableName}]."
            : $"Last {runs.Count} run(s) of [{s.DatabaseName}].[{s.SchemaName}].[{s.TableName}], newest first.";
        if (runs.FirstOrDefault() is { } latest && _lastRunOutcome.Text.Length == 0)
        {
            _lastRunOutcome = latest.Status switch
            {
                "Completed" => ($"Last run completed {latest.UpdatedUtc.ToLocalTime():g}", Theme.Tone.Success),
                "Running" => ("Last run interrupted", Theme.Tone.Warning),
                _ => ("", Theme.Tone.Muted),
            };
            UpdateHeaderChips();
        }
    }

    private void ShowInterruptedChoice(InterruptedRunInfo? run, string? message, bool error = false)
    {
        var hasRun = run is not null;
        _resumeContinue.Visible = hasRun;
        _resumeRestart.Visible = hasRun;
        _noDeleteHelp.Visible = hasRun;
        if (hasRun)
        {
            _resumeContinue.Checked = true; // default: continue
            var started = run!.StartedUtc == DateTime.MinValue ? "unknown time" : run.StartedUtc.ToLocalTime().ToString("g");
            SetResult(_runCheck, $"An interrupted {run.Mode} run was found (started {started}): {run.RowsWritten:N0} rows written, "
                + $"{run.SlicesLeft:N0} work item(s) left."
                + (string.IsNullOrWhiteSpace(run.LastError) ? "" : $"\nLast error: {run.LastError}"), Theme.WarningText);
        }
        else if (message is not null)
        {
            SetResult(_runCheck, message, error ? Theme.DangerText : Theme.TextMuted);
        }
        else
        {
            SetResult(_runCheck, "No interrupted run – Start begins a new run.", Theme.SuccessText);
        }
        WizardLayout.UpdateWrapping(_pages[StepRun]);
    }

    private void ResetDashboard()
    {
        _pStage.Text = "Not started";
        _pPercent.Text = "";
        _progress.SetValue(0, false);
        _kRows.Set("-");
        _kSpeed.Set("-");
        _kElapsed.Set("-");
        _kEta.Set("-");
        _kWorkers.Set("-");
        _kWorkers.SetMeter(0, 0);
        _kSlices.Set("-");
        _spark.Clear();
        _rateWindow.Clear();
        _lastProgress = null;
        _runStatusText = "";
    }

    private static string StageText(string? stage) => stage switch
    {
        null or "" or "copying" => "Copying rows",
        "starting" => "Starting",
        "connecting" => "Connecting to the source",
        "metadata" => "Reading the table definition",
        "schema" => "Preparing the destination table",
        "planning ranges" => "Planning parallel work (reading all ids once)",
        "deletes" => "Detecting deleted rows",
        "done" => "Finished",
        _ when stage.StartsWith("retry", StringComparison.OrdinalIgnoreCase) => "Waiting to retry: " + stage,
        _ => char.ToUpperInvariant(stage[0]) + stage[1..],
    };

    private void OnProgress(SyncProgress p)
    {
        if (IsDisposed)
        {
            return;
        }
        _pStage.Text = StageText(p.Stage);
        if (p.RowsWritten <= 0 && p.SlicesDone <= 0 && p.ActiveWorkers <= 0 && p.Workers <= 0)
        {
            return; // setup stages carry no numbers
        }
        _lastProgress = p;
        var total = p.EstimatedTotalRows is > 0 ? p.EstimatedTotalRows : null;
        double? fraction = total is { } t ? Math.Min(p.Stage == "done" ? 1 : 0.999, p.RowsWritten / (double)t) : null;
        _progress.SetValue(fraction, animate: _running);
        _pPercent.Text = fraction is { } f ? $"{f:P0}" : $"{p.RowsWritten:N0} rows";
        _kRows.Set(p.RowsWritten.ToString("N0"), total is { } tt ? $"of about {tt:N0}" : "total not known for this run");
        _kSpeed.Set($"{p.RowsPerSecond:N0}/s", "rows per second");
        if (p.Workers > 0)
        {
            _kWorkers.Set($"{p.ActiveWorkers} of {p.Workers}", p.ActiveWorkers < p.Workers && p.SlicesQueued == 0 ? "finishing last items" : "reading in parallel");
            _kWorkers.SetMeter(p.ActiveWorkers, p.Workers);
        }
        _kSlices.Set(p.SlicesDone.ToString("N0"), $"{p.SlicesQueued:N0} waiting");
        if (fraction is { } fr && fr < 1 && p.RowsPerSecond > 0 && total is { } tot)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, tot - p.RowsWritten) / p.RowsPerSecond);
            _kEta.Set(FormatElapsed(remaining), $"done around {DateTime.Now + remaining:t}");
        }
        else if (p.Stage != "done")
        {
            _kEta.Set("-", total is null ? "unknown for this run" : "calculating...");
        }
        _runStatusText = fraction is { } pct ? pct.ToString("P0") : $"{p.RowsWritten:N0} rows";
    }

    // Once a second: elapsed time, a 5-second throughput sample for the chart, and the step list / header.
    private void OnRunTick()
    {
        var now = _runWatch.Elapsed;
        _kElapsed.Set(FormatElapsed(now), "since Start");
        if (_lastProgress is { } p && p.RowsWritten > 0)
        {
            _rateWindow.Enqueue((now.TotalSeconds, p.RowsWritten));
            while (_rateWindow.Count > 1 && now.TotalSeconds - _rateWindow.Peek().Seconds > RateWindowSeconds)
            {
                _rateWindow.Dequeue();
            }
            var first = _rateWindow.Peek();
            var span = now.TotalSeconds - first.Seconds;
            _spark.Add(span > 0 ? (p.RowsWritten - first.Rows) / span : 0);
        }
        UpdateNav();
    }

    private async Task StartSyncAsync()
    {
        if (!IsIdle || _checkingRun)
        {
            return;
        }
        for (var i = 0; i < StepRun; i++)
        {
            if (StepProblem(i) is { } problem)
            {
                MessageBox.Show(this, $"Step {i + 1} ({StepTitles[i]}) is not complete:\n{problem}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowStep(i);
                return;
            }
        }
        var restart = _interrupted is not null && _resumeRestart.Checked;
        if (restart && MessageBox.Show(this,
                "Start again from the beginning?\n\nThe interrupted run is not continued; a new run reads all rows again.\n"
                + "Nothing is deleted: existing rows in the destination stay and are updated or added again.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        // Auto-save to the current file (asks for a file name when the configuration is new).
        if (!SaveConfiguration(saveAs: false))
        {
            AppLog.Info("Start cancelled: the configuration was not saved.");
            return;
        }

        var s = CollectSettings();
        if (!ValidateForConnect(s))
        {
            return;
        }
        s.ResumeInterruptedRun = !restart;
        var secret = GetSecret();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _running = true;
        _lastRunOutcome = ("", Theme.Tone.Muted);
        SetResult(_summary, "", Theme.Text);
        ResetDashboard();
        _pStage.Text = StageText("starting");
        _progress.SetValue(null, animate: true);
        _kElapsed.Set(FormatElapsed(TimeSpan.Zero), "since Start");
        _runWatch.Restart();
        _elapsedTimer.Start();
        WizardLayout.ScrollIntoView(_runPage, _dashboardCard); // bring the live figures into view
        UpdateNav();
        AppLog.Info(restart ? "Starting a new run from the beginning (existing rows are kept)." : "Starting sync (an interrupted run is continued if one exists).");

        var progress = new Progress<SyncProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => new SyncEngine().RunAsync(s, secret, progress, token), token);
            var summary = $"Sync finished: {result.RowsWritten:N0} rows written, {result.RowsDeleted:N0} deleted in {FormatElapsed(result.Elapsed)}. "
                + $"Correlation id: {result.CorrelationId}";
            if (!IsDisposed)
            {
                _pStage.Text = StageText("done");
                _progress.SetValue(1, false);
                _pPercent.Text = "100%";
                _kRows.Set(result.RowsWritten.ToString("N0"), result.RowsDeleted > 0 ? $"{result.RowsDeleted:N0} deleted" : "all rows copied");
                _kEta.Set("0:00", "done");
                _kWorkers.SetMeter(0, _lastProgress?.Workers ?? 0);
                _kWorkers.Set("0", "all workers finished");
                _lastRunOutcome = ($"Finished · {result.RowsWritten:N0} rows", Theme.Tone.Success);
                SetResult(_summary, summary, Theme.SuccessText);
                if (!_closeAfterRun)
                {
                    MessageBox.Show(this, summary, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Info("Sync stopped by user. Progress is saved; the next start can continue where it stopped.");
            if (!IsDisposed)
            {
                _pStage.Text = "Stopped";
                _progress.SetValue(_lastProgress is { EstimatedTotalRows: > 0 } lp ? lp.RowsWritten / (double)lp.EstimatedTotalRows!.Value : 0, false);
                _lastRunOutcome = ("Stopped · can continue", Theme.Tone.Warning);
                SetResult(_summary, "Sync stopped. Progress is saved: choose \"Continue the interrupted run\" and Start to continue later.", Theme.WarningText);
                if (!_closeAfterRun)
                {
                    MessageBox.Show(this, "Sync stopped. Progress is saved, so you can continue later from where it stopped.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Sync failed.", ex);
            if (!IsDisposed)
            {
                _pStage.Text = "Failed";
                _progress.SetValue(0, false);
                _lastRunOutcome = ("Last run failed", Theme.Tone.Danger);
                SetResult(_summary, $"Sync failed: {ex.Message}\nProgress up to the error is saved; Start continues the run. Log folder: {AppLog.LogDirectory}", Theme.DangerText);
                if (!_closeAfterRun)
                {
                    MessageBox.Show(this, "Sync failed: " + ex.Message + "\n\nProgress up to the error is saved; Start continues the run.\nSee the log for details: "
                        + AppLog.LogDirectory, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        finally
        {
            _running = false;
            _elapsedTimer.Stop();
            _runWatch.Stop();
            _runCts.Dispose();
            _runCts = null;
            _runStatusText = "";
            if (!IsDisposed)
            {
                _kElapsed.Set(FormatElapsed(_runWatch.Elapsed), "total");
                UpdateNav();
                if (_closeAfterRun)
                {
                    BeginInvoke(Close);
                }
                else
                {
                    WizardLayout.UpdateWrapping(_pages[StepRun]);
                    _ = CheckInterruptedRunAsync();
                }
            }
        }
    }

    private void CancelRun()
    {
        if (_runCts is { IsCancellationRequested: false } cts)
        {
            _pStage.Text = "Stopping – finishing the current pages...";
            AppLog.Info("Stop requested. Finishing the current pages; progress is saved.");
            cts.Cancel();
            UpdateRunUi();
        }
    }

    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : t.ToString(@"m\:ss");
}
