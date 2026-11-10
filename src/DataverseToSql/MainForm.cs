using System.Diagnostics;
using System.Text.Json;
using DataverseToSql.Sync;
using DataverseToSql.UI;

namespace DataverseToSql;

/// <summary>
/// Step-by-step wizard: configuration file, Dataverse source, SQL destination, column mapping, options and run.
/// This file holds the shell (step list, navigation, configuration files, log). The steps live in UI/MainForm.*.cs.
/// </summary>
public sealed partial class MainForm : Form
{
    private const string AppTitle = "Dataverse to SQL Sync";
    private const string SecretEnvVar = "DATAVERSE_CLIENT_SECRET";
    private const string DocumentBody = "documentbody";
    private const int MaxLogLines = 5000;
    private const int TrimLogTo = 4000;

    private const int StepConfig = 0, StepSource = 1, StepDestination = 2, StepMapping = 3, StepRun = 4;
    private static readonly string[] StepTitles = { "Configuration", "Source", "Destination (SQL)", "Column mapping", "Options and start" };

    private AppSettings _settings = new();
    private string? _configPath;
    private string _savedSnapshot = "";
    private int _step;

    // Shell
    private readonly Control[] _pages = new Control[StepTitles.Length];
    private readonly StepNav _stepNav = new() { Dock = DockStyle.Fill };
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = Theme.PageBack };
    private readonly Button _back = WizardLayout.MakeButton("< &Back", 95);
    private readonly Button _next = Theme.Primary(WizardLayout.MakeButton("&Next >", 110));
    private readonly Button _close = WizardLayout.MakeButton("Close", 95);
    private readonly Button _save = WizardLayout.MakeButton("&Save", 80);
    private readonly Button _saveAs = WizardLayout.MakeButton("Save &as...", 95);
    private readonly Button _toggleLog = WizardLayout.MakeButton("Show log", 105);
    private readonly Button _update = WizardLayout.MakeButton("Updates", 95);
    private readonly Label _nextHint = new() { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Theme.DangerText, UseMnemonic = false, Margin = new Padding(8, 9, 8, 3) };
    private readonly Label _fileLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.TextMuted, UseMnemonic = false, Margin = new Padding(8, 9, 3, 3) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, BackColor = Theme.PageBack };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill, Font = Theme.Mono, BackColor = Color.White, ForeColor = Theme.Text,
    };
    private readonly ToolTip _toolTip = new();

    // Header: product name, current configuration and live status chips.
    private readonly Label _headerConfig = new() { AutoSize = true, ForeColor = Theme.TextMuted, UseMnemonic = false, Margin = new Padding(0, 0, 3, 0) };
    private readonly Pill _chipSource = new();
    private readonly Pill _chipDestination = new();
    private readonly Pill _chipRun = new();
    private readonly Font _logoFont = new("Segoe UI Symbol", 15f);
    private string _runStatusText = "";
    private (string Text, Theme.Tone Tone) _lastRunOutcome = ("", Theme.Tone.Muted);

    // Updates: a quiet check on startup fills this in, so the button can offer the new version right away.
    private ReleaseInfo? _availableUpdate;

    private CancellationTokenSource? _runCts;
    private readonly CancellationTokenSource _formCts = new();
    private bool _running;
    private bool _loading;
    private bool _applying;
    private bool _closeAfterRun;
    private int _logLines;

    /// <summary>Creates the wizard and opens the most recently used configuration.</summary>
    public MainForm()
    {
        Text = AppTitle;
        Font = Theme.Base;
        ForeColor = Theme.Text;
        BackColor = Theme.PageBack;
        // Large enough for every step but never bigger than the screen; resizable (pages scroll if the window is small).
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 728);
        Size = new Size(Math.Min(1320, area.Width - 20), Math.Min(920, area.Height - 20));
        MinimumSize = new Size(Math.Min(1040, area.Width - 20), Math.Min(700, area.Height - 20));
        StartPosition = FormStartPosition.CenterScreen;

        SuspendLayout();
        ConfigureGrid();
        _pages[StepConfig] = BuildConfigPage();
        _pages[StepSource] = BuildSourcePage();
        _pages[StepDestination] = BuildDestinationPage();
        _pages[StepMapping] = BuildMappingPage();
        _pages[StepRun] = BuildRunPage();
        BuildShell();
        Theme.ApplyTree(this);
        ResumeLayout(true);

        WireShellEvents();
        WireSourceEvents();
        WireDestinationEvents();
        WireMappingEvents();
        WireRunEvents();

        AppLog.Message += OnLogMessage;
        OpenInitialConfiguration();
        ShowStep(StepConfig);
    }

    // ---------------------------------------------------------------- shell layout

    private void BuildShell()
    {
        // Header: logo mark, product name, configuration file and live status chips.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.White, Padding = new Padding(16, 10, 16, 8), Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new Panel { Size = new Size(36, 36), Margin = new Padding(0, 2, 12, 0), AccessibleName = AppTitle, AccessibleRole = AccessibleRole.Graphic };
        logo.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.Rounded(new RectangleF(0, 0, 35, 35), 8);
            using var brush = new SolidBrush(Theme.Accent);
            e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, "⇄", _logoFont, new Rectangle(0, 0, 36, 36), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        var titleRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        titleRow.Controls.Add(new Label { Text = AppTitle, AutoSize = true, Font = Theme.AppTitle, ForeColor = Theme.Text, Margin = new Padding(0, 0, 3, 0), UseMnemonic = false });
        titleRow.Controls.Add(new Label
        {
            Text = $"v{UpdateService.CurrentVersionText}", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextMuted,
            Margin = new Padding(0, 9, 0, 0), UseMnemonic = false,
        });
        titles.Controls.Add(titleRow);
        titles.Controls.Add(_headerConfig);
        var chips = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        chips.Controls.AddRange(new Control[] { _chipSource, _chipDestination, _chipRun });
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(chips, 2, 0);
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        // Left: step list with a one-line status per step.
        _stepNav.StepClicked += TryJumpTo;
        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.NavBack, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 0) };
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nav.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nav.Controls.Add(_stepNav, 0, 0);
        nav.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(215, 0), ForeColor = Theme.TextMuted, Font = Theme.Small, Margin = new Padding(18, 8, 12, 16),
            Text = "Secrets (client secret, SQL password) are never saved to a configuration file.",
        }, 0, 1);

        // Right: page host above the (hidden by default) technical log.
        foreach (var page in _pages)
        {
            page.Visible = false;
            _pageHost.Controls.Add(page);
        }
        var logBox = new Card("Technical log") { Dock = DockStyle.Fill, AutoSize = false, Margin = Padding.Empty };
        logBox.Controls.Add(_log);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 0, 28, 8), BackColor = Theme.PageBack };
        logHost.Controls.Add(logBox);
        _split.Panel1.Controls.Add(_pageHost);
        _split.Panel2.Controls.Add(logHost);
        _split.Panel1MinSize = 300;

        // Bottom: Save / Save as / file name ... hint, Log, Back, Next, Close.
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 9, RowCount = 1, Padding = new Padding(20, 10, 20, 10), BackColor = Color.White };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
        };
        _fileLabel.AutoEllipsis = true;
        _fileLabel.AutoSize = false;
        _fileLabel.Dock = DockStyle.Fill;
        _fileLabel.TextAlign = ContentAlignment.MiddleLeft;
        _nextHint.AutoEllipsis = true;
        _nextHint.AutoSize = false;
        _nextHint.Dock = DockStyle.Fill;
        _nextHint.TextAlign = ContentAlignment.MiddleRight;
        _back.Margin = new Padding(12, 3, 3, 3);
        _close.Margin = new Padding(12, 3, 3, 3);
        bottom.Controls.Add(_save, 0, 0);
        bottom.Controls.Add(_saveAs, 1, 0);
        bottom.Controls.Add(_fileLabel, 2, 0);
        bottom.Controls.Add(_nextHint, 3, 0);
        bottom.Controls.Add(_update, 4, 0);
        bottom.Controls.Add(_toggleLog, 5, 0);
        bottom.Controls.Add(_back, 6, 0);
        bottom.Controls.Add(_next, 7, 0);
        bottom.Controls.Add(_close, 8, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.SetColumnSpan(header, 2);
        root.Controls.Add(nav, 0, 1);
        root.SetRowSpan(nav, 2);
        root.Controls.Add(_split, 1, 1);
        root.Controls.Add(bottom, 1, 2);
        Controls.Add(root);

        _toolTip.SetToolTip(_save, "Save the configuration to the current file");
        _toolTip.SetToolTip(_saveAs, "Save the configuration to a new file");
        _toolTip.SetToolTip(_toggleLog, "Show or hide the technical log");
        _toolTip.SetToolTip(_update, $"Version {UpdateService.CurrentVersionText} - check GitHub for a newer version");

        Load += (_, _) =>
        {
            // Entra interactive (Azure SQL MFA) sign-in needs a parent window for the Windows WAM broker.
            Sync.SqlEntraAuth.UseParentWindow(Handle);
            try { _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Height - 220); } catch (InvalidOperationException) { }
            SetLogVisible(false); // technical log is hidden by default; "Show log" opens it
        };
        Shown += (_, _) =>
        {
            AppLog.Info($"Version {UpdateService.CurrentVersionText} ({UpdateService.Kind}).");
            AppLog.Info($"Configurations folder: {RecentConfigurations.ConfigFolder}");
            AppLog.Info($"Log directory: {AppLog.LogDirectory}");
            WizardLayout.UpdateWrapping(_pages[_step]);
            _ = CheckForUpdatesQuietlyAsync();
        };
    }

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Asks GitHub once on startup whether a newer release exists. It never interrupts: on success the
    /// Updates button becomes a call to action, and any failure only goes to the technical log.
    /// </summary>
    private async Task CheckForUpdatesQuietlyAsync()
    {
        try
        {
            var release = await UpdateService.GetLatestReleaseAsync(_formCts.Token);
            if (!UpdateService.IsNewer(release) || IsDisposed)
            {
                AppLog.Info(release is null
                    ? "Update check: no release published yet."
                    : $"Update check: {release.Version.ToString(3)} is the latest; this copy is current.");
                return;
            }
            _availableUpdate = release;
            _update.Text = "Update available";
            Theme.Primary(_update);
            _toolTip.SetToolTip(_update, $"Version {release!.Version.ToString(3)} is available (you have {UpdateService.CurrentVersionText})");
            AppLog.Info($"Update check: version {release.Version.ToString(3)} is available.");
        }
        catch (OperationCanceledException)
        {
            // the window is closing
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Opens the update dialog. When it reports an update was applied, the app closes so it can finish.</summary>
    private void ShowUpdateDialog()
    {
        if (_running)
        {
            MessageBox.Show(this, "A sync is running. Stop it, or wait for it to finish, before updating.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new UpdateForm(_availableUpdate);
        var result = dialog.ShowDialog(this);
        if (result != DialogResult.OK)
        {
            return;
        }
        AppLog.Info("Update applied; closing so the new version can start.");
        _savedSnapshot = Snapshot(CollectSettings()); // the update restarts the app, so do not ask about unsaved changes
        Close();
    }

    private void WireShellEvents()
    {
        _back.Click += (_, _) => GoBack();
        _next.Click += async (_, _) => await GoNextAsync();
        _close.Click += (_, _) => Close();
        _save.Click += (_, _) => SaveConfiguration(saveAs: false);
        _saveAs.Click += (_, _) => SaveConfiguration(saveAs: true);
        _toggleLog.Click += (_, _) => SetLogVisible(_split.Panel2Collapsed);
        _update.Click += (_, _) => ShowUpdateDialog();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            AppLog.Message -= OnLogMessage;
            _formCts.Cancel();
        };
    }

    private void SetLogVisible(bool visible)
    {
        _split.Panel2Collapsed = !visible;
        _toggleLog.Text = visible ? "Hide log" : "Show log";
    }

    // ---------------------------------------------------------------- navigation

    private void ShowStep(int step)
    {
        _step = step;
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visible = i == step;
        }
        WizardLayout.UpdateWrapping(_pages[step]);
        if (step == StepRun)
        {
            _ = CheckInterruptedRunAsync();
        }
        UpdateNav();
        _pages[step].SelectNextControl(_pages[step], true, true, true, false);
    }

    private void GoBack()
    {
        if (_step > 0 && IsIdle)
        {
            ShowStep(_step - 1);
        }
    }

    private async Task GoNextAsync()
    {
        if (_step >= StepRun || !IsIdle)
        {
            return;
        }

        // Friendly path: Next on the Source and Destination steps runs the connection test itself.
        if (_step == StepSource && SourceProblem() is not null)
        {
            await TestSourceAsync();
        }
        else if (_step == StepDestination && DestinationProblem() is not null)
        {
            await TestDestinationAsync();
        }
        if (StepProblem(_step) is not null)
        {
            return; // the test shows what is wrong on the page
        }
        if (_step == StepMapping && RequiredUnmappedColumns() is { Count: > 0 } required)
        {
            var answer = MessageBox.Show(this,
                "These destination columns are NOT NULL without a default and are not filled by the sync, so inserts will fail:\n\n"
                + string.Join(", ", required) + "\n\nContinue anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }
        ShowStep(_step + 1);
    }

    private void TryJumpTo(int target)
    {
        if (!IsIdle || target == _step)
        {
            return;
        }
        for (var i = 0; i < target; i++)
        {
            if (StepProblem(i) is not null)
            {
                ShowStep(i);
                return;
            }
        }
        ShowStep(target);
    }

    private bool IsIdle => !_running && !_loading;

    /// <summary>Returns why <paramref name="step"/> is not complete, or null when Next may be used.</summary>
    private string? StepProblem(int step) => step switch
    {
        StepSource => SourceProblem(),
        StepDestination => DestinationProblem(),
        StepMapping => MappingProblem(),
        _ => null,
    };

    /// <summary>Refreshes the navigation buttons, step list and per-page enabled state.</summary>
    private void UpdateNav()
    {
        var idle = IsIdle;
        var problem = _step < StepRun ? StepProblem(_step) : null;
        _back.Enabled = idle && _step > 0;
        _next.Visible = _step < StepRun;
        var testsOnNext = _step is StepSource or StepDestination;
        _next.Enabled = idle && (problem is null || testsOnNext);
        _next.Text = testsOnNext && problem is not null ? "Test && &Next >" : "&Next >";
        if (testsOnNext && problem is not null)
        {
            problem = "Click Next: the connection is tested first.";
        }
        _save.Enabled = idle;
        _saveAs.Enabled = idle;
        _nextHint.ForeColor = _running || _loading ? Theme.TextMuted : Theme.DangerText;
        _nextHint.Text = _running ? "Sync running..." : _loading ? "Working..." : problem ?? "";
        _toolTip.SetToolTip(_nextHint, _nextHint.Text);
        for (var i = 0; i < StepRun; i++)
        {
            _pages[i].Enabled = idle;
        }
        UseWaitCursor = _loading;

        var reachable = FurthestReachable();
        var items = new StepNav.Item[StepTitles.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var state = i == _step ? StepNav.State.Current
                : i < StepRun && StepProblem(i) is null && i <= reachable ? StepNav.State.Done
                : i > reachable ? StepNav.State.Blocked
                : StepNav.State.Upcoming;
            items[i] = new StepNav.Item(StepTitles[i], StepSubtitle(i), state);
        }
        _stepNav.SetItems(items);
        UpdateHeaderChips();
        UpdateRunUi();
    }

    /// <summary>One-line status under each step in the step list.</summary>
    private string StepSubtitle(int step)
    {
        switch (step)
        {
            case StepConfig:
                return _configPath is null ? "Not saved yet" : Path.GetFileNameWithoutExtension(_configPath) + (IsDirty ? " (unsaved changes)" : "");
            case StepSource:
                return SourceProblem() is null
                    ? $"{_loadedEntity} · {SelectedSource switch { SourceKind.Tds => "TDS endpoint", SourceKind.SqlTable => "SQL table", _ => "Dataverse SDK" }}"
                    : _sourceOkKey is null ? "Not connected" : "Settings changed – test again";
            case StepDestination:
                return DestinationProblem() is null ? $"{_schema.Text.Trim()}.{_table.Text.Trim()}" : _destOkKey is null ? "Not tested" : "Settings changed – test again";
            case StepMapping:
                if (_loadedEntity is null) return "Waiting for the source";
                var selected = _checked.Count(kv => kv.Value);
                return MappingProblem() is null ? $"{selected} of {_checked.Count} columns selected" : "Needs attention";
            default:
                return _running ? (_runStatusText.Length > 0 ? _runStatusText : "Running") : FurthestReachable() >= StepRun ? "Ready to start" : "Complete the steps above";
        }
    }

    /// <summary>Header chips: source, destination and run state at a glance.</summary>
    private void UpdateHeaderChips()
    {
        var sourceOk = SourceProblem() is null;
        _chipSource.Set(sourceOk ? $"Source: {_loadedEntity}" : "Source not connected", sourceOk ? Theme.Tone.Success : Theme.Tone.Muted);
        var destOk = DestinationProblem() is null;
        _chipDestination.Set(destOk ? $"Destination: {_table.Text.Trim()}" : "Destination not tested", destOk ? Theme.Tone.Success : Theme.Tone.Muted);
        if (_running)
        {
            _chipRun.Set(_runStatusText.Length > 0 ? $"Running · {_runStatusText}" : "Running", Theme.Tone.Info);
        }
        else
        {
            _chipRun.Set(_lastRunOutcome.Text, _lastRunOutcome.Tone);
        }
        _chipRun.Visible = _running || _lastRunOutcome.Text.Length > 0;
    }

    private int FurthestReachable()
    {
        var i = 0;
        while (i < StepRun && StepProblem(i) is null)
        {
            i++;
        }
        return i;
    }

    /// <summary>Common handler for every input: refresh validation and the unsaved-changes marker.</summary>
    private void OnInputChanged()
    {
        if (_applying)
        {
            return;
        }
        UpdateTitle();
        UpdateNav();
    }

    private void HookInputs(params Control[] controls)
    {
        foreach (var c in controls)
        {
            switch (c)
            {
                case TextBox t: t.TextChanged += (_, _) => OnInputChanged(); break;
                case ComboBox cb: cb.SelectedIndexChanged += (_, _) => OnInputChanged(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => OnInputChanged(); break;
                case CheckBox ch: ch.CheckedChanged += (_, _) => OnInputChanged(); break;
                case RadioButton r: r.CheckedChanged += (_, _) => OnInputChanged(); break;
            }
        }
    }

    // ---------------------------------------------------------------- configuration files

    private void OpenInitialConfiguration()
    {
        var recent = RecentConfigurations.Load();
        if (recent.Count == 0 && File.Exists(AppSettings.DefaultPath))
        {
            RecentConfigurations.Add(AppSettings.DefaultPath); // settings of the previous (non-wizard) version
            recent = RecentConfigurations.Load();
        }
        var loaded = false;
        foreach (var path in recent.Take(1))
        {
            try
            {
                LoadIntoUi(AppSettings.Load(path), path);
                loaded = true;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not open {path}.", ex);
            }
        }
        if (!loaded)
        {
            LoadIntoUi(new AppSettings(), null);
        }
        RefreshRecentList();
    }

    private void NewConfiguration()
    {
        if (!IsIdle || !ConfirmDiscardChanges())
        {
            return;
        }
        LoadIntoUi(new AppSettings(), null);
        AppLog.Info("New configuration.");
    }

    private void OpenConfigurationDialog()
    {
        if (!IsIdle)
        {
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = "Open configuration", Filter = "Configuration (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = EnsureConfigFolder(), CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            OpenConfiguration(dlg.FileName);
        }
    }

    private void OpenConfiguration(string path)
    {
        if (!IsIdle || !ConfirmDiscardChanges())
        {
            return;
        }
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The file does not exist.", path);
            }
            LoadIntoUi(AppSettings.Load(path), path);
            RecentConfigurations.Add(path);
            RefreshRecentList();
            AppLog.Info($"Configuration opened: {path}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not open {path}.", ex);
            MessageBox.Show(this, $"Could not open the configuration:\n{path}\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Saves to the current file (or asks for one). Returns false when cancelled or failed.</summary>
    private bool SaveConfiguration(bool saveAs)
    {
        var s = CollectSettings();
        var path = _configPath;
        if (saveAs || path is null)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save configuration", Filter = "Configuration (*.json)|*.json", DefaultExt = "json", AddExtension = true,
                InitialDirectory = path is null ? EnsureConfigFolder() : Path.GetDirectoryName(path),
                FileName = path is null ? SuggestedFileName(s) : Path.GetFileName(path), OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }
            path = dlg.FileName;
        }
        try
        {
            s.Save(path); // STD-INT-006: Save strips SQL passwords; the client secret has no property at all.
            _settings = s;
            _configPath = path;
            _savedSnapshot = Snapshot(s);
            RecentConfigurations.Add(path);
            RefreshRecentList();
            UpdateTitle();
            AppLog.Info($"Configuration saved to {path}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save the configuration.", ex);
            MessageBox.Show(this, "Could not save the configuration: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static string SuggestedFileName(AppSettings s)
    {
        var name = $"{s.EntityName}-to-{s.DatabaseName}-{s.TableName}".Trim('-');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return (name.Length == 0 ? "sync" : name) + ".json";
    }

    private static string EnsureConfigFolder()
    {
        try { Directory.CreateDirectory(RecentConfigurations.ConfigFolder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return RecentConfigurations.ConfigFolder;
    }

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
        {
            return true;
        }
        var name = _configPath is null ? "the new configuration" : Path.GetFileName(_configPath);
        var answer = MessageBox.Show(this, $"Save changes to {name}?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => SaveConfiguration(saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    private bool IsDirty => Snapshot(CollectSettings()) != _savedSnapshot;

    /// <summary>Order-insensitive fingerprint of the settings, used for the unsaved-changes marker.</summary>
    private static string Snapshot(AppSettings s)
    {
        var selected = string.Join(",", s.SelectedAttributes.Select(a => a.ToLowerInvariant()).Order(StringComparer.Ordinal));
        var mappings = string.Join(",", s.ColumnMappings.Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value).Order(StringComparer.Ordinal));
        var copy = new AppSettings
        {
            DataverseUrl = s.DataverseUrl, AuthType = s.AuthType, ClientId = s.ClientId, Source = s.Source, EntityName = s.EntityName,
            SourceSqlConnectionString = s.SourceSqlConnectionString, SourceSqlAuth = s.SourceSqlAuth, SourceSqlUser = s.SourceSqlUser,
            SourceDatabaseName = s.SourceDatabaseName,
            SqlConnectionString = s.SqlConnectionString, SqlAuth = s.SqlAuth, SqlUser = s.SqlUser, DatabaseName = s.DatabaseName,
            SchemaName = s.SchemaName, TableName = s.TableName, Mode = s.Mode, PageSize = s.PageSize, Parallelism = s.Parallelism,
            DetectDeletes = s.DetectDeletes, ResumeInterruptedRun = s.ResumeInterruptedRun, MaxAttempts = s.MaxAttempts,
        };
        return JsonSerializer.Serialize(copy) + "|" + selected + "|" + mappings;
    }

    private void UpdateTitle()
    {
        var name = _configPath is null ? "New configuration" : Path.GetFileName(_configPath);
        var dirty = IsDirty ? " *" : "";
        Text = $"{name}{dirty} - {AppTitle}";
        _fileLabel.Text = (_configPath ?? "Not saved yet") + dirty;
        _headerConfig.Text = (_configPath is null ? "New configuration (not saved yet)" : $"Configuration: {Path.GetFileName(_configPath)}")
            + (dirty.Length > 0 ? "  •  unsaved changes" : "");
        _toolTip.SetToolTip(_fileLabel, _configPath ?? "This configuration has not been saved yet.");
        UpdateConfigPage(dirty.Length > 0);
    }

    /// <summary>Replaces the whole wizard state with <paramref name="s"/>.</summary>
    private void LoadIntoUi(AppSettings s, string? path)
    {
        _settings = s;
        _configPath = path is null ? null : Path.GetFullPath(path);
        _secret.Clear();
        ResetSourceState();
        ResetDestinationState();
        _interrupted = null;
        _summary.Text = "";

        ApplySettingsToControls(s);
        RebuildGrid();
        _savedSnapshot = Snapshot(CollectSettings());
        UpdateTitle();
        UpdateNav();
    }

    private void ApplySettingsToControls(AppSettings s)
    {
        _applying = true;
        try
        {
            ApplySourceSettings(s);
            ApplyDestinationSettings(s);
            ApplyRunSettings(s);
            _mapping.Clear();
            foreach (var kv in s.ColumnMappings)
            {
                _mapping[kv.Key] = kv.Value;
            }
            _filter.Clear();
            UpdateStatuses();
            _attrInfo.Text = s.SelectedAttributes.Count > 0
                ? $"{s.SelectedAttributes.Count} saved attribute(s). Test the source in step 2 to edit the selection."
                : "All syncable attributes except documentbody (default). Test the source in step 2 to edit the selection.";
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Builds settings from the controls. ResumeInterruptedRun is always saved as true; restart is a per-run choice.</summary>
    private AppSettings CollectSettings()
    {
        var entity = _entity.Text.Trim();
        List<string> selected;
        Dictionary<string, string> mappings;
        if (_loadedEntity is not null && string.Equals(_loadedEntity, entity, StringComparison.OrdinalIgnoreCase))
        {
            selected = _checked.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            var known = _rows.Select(r => r.DefaultColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
            mappings = _mapping.Where(kv => known.Contains(kv.Key) && !kv.Key.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        else if (string.Equals(_settings.EntityName, entity, StringComparison.OrdinalIgnoreCase))
        {
            selected = new List<string>(_settings.SelectedAttributes);
            mappings = new Dictionary<string, string>(_mapping, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            selected = new List<string>(); // different entity, nothing chosen yet => defaults
            mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return new AppSettings
        {
            DataverseUrl = _url.Text.Trim(),
            AuthType = SelectedAuth,
            ClientId = _clientId.Text.Trim(),
            Source = SelectedSource,
            EntityName = entity,
            SourceSqlConnectionString = _srcSqlConn.Text.Trim(),
            SourceSqlAuth = SelectedSourceSqlAuth,
            SourceSqlUser = _srcSqlUser.Text.Trim(),
            SourceDatabaseName = _srcDatabase.Text.Trim(),
            SelectedAttributes = selected,
            SqlConnectionString = _sqlConn.Text.Trim(),
            SqlAuth = SelectedSqlAuth,
            SqlUser = _sqlUser.Text.Trim(),
            DatabaseName = _database.Text.Trim(),
            SchemaName = _schema.Text.Trim(),
            TableName = _table.Text.Trim(),
            Mode = _mode.SelectedIndex == 1 ? SyncMode.Incremental : SyncMode.Full,
            PageSize = (int)_pageSize.Value,
            Parallelism = (int)_parallelism.Value,
            DetectDeletes = _detectDeletes.Checked,
            ColumnMappings = mappings,
            ResumeInterruptedRun = true,
            MaxAttempts = (int)_maxAttempts.Value,
            // Settings-file only (no UI): keep what the loaded configuration says.
            SqlWriteParallelism = _settings.SqlWriteParallelism,
            SqlWriteBatchSize = _settings.SqlWriteBatchSize,
            DeadlockMaxRetries = _settings.DeadlockMaxRetries,
            DeadlockRetryBaseDelayMs = _settings.DeadlockRetryBaseDelayMs,
            UpsertMode = _settings.UpsertMode,
        };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_running)
        {
            var answer = MessageBox.Show(this, "A sync is running. Stop it and close?\n\nProgress is saved; you can continue later.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            e.Cancel = true; // close once the run has unwound
            if (answer == DialogResult.Yes)
            {
                _closeAfterRun = true;
                CancelRun();
            }
            return;
        }
        if (!_closeAfterRun && !ConfirmDiscardChanges())
        {
            e.Cancel = true;
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not open {folder}.", ex);
        }
    }

    // ---------------------------------------------------------------- busy state

    private void BeginBusy()
    {
        _loading = true;
        UpdateNav();
    }

    private void EndBusy()
    {
        _loading = false;
        if (!IsDisposed)
        {
            UpdateNav();
        }
    }

    // ---------------------------------------------------------------- log

    private void OnLogMessage(string line)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(() => AppendLog(line));
        }
        catch (InvalidOperationException) { /* handle gone while closing */ }
    }

    private void AppendLog(string line)
    {
        if (_log.IsDisposed)
        {
            return;
        }
        _log.AppendText(line + Environment.NewLine);
        if (++_logLines > MaxLogLines)
        {
            var lines = _log.Lines;
            var keep = lines.Skip(Math.Max(0, lines.Length - TrimLogTo)).ToArray();
            _log.Lines = keep;
            _logLines = keep.Length;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppLog.Message -= OnLogMessage;
            _toolTip.Dispose();
            _elapsedTimer.Dispose();
            _logoFont.Dispose();
            _arrowFont.Dispose();
            _formCts.Dispose();
            _runCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
