using DataverseToSql.Sync;
using DataverseToSql.UI;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseToSql;

// Steps 1-3: configuration file, source (Dataverse or SQL table), SQL destination.
public sealed partial class MainForm
{
    // ---------------------------------------------------------------- step 1: configuration

    private readonly Label _configCurrent = new() { AutoSize = true, Font = Theme.BaseSemibold, UseMnemonic = false, Margin = new Padding(3, 7, 3, 2) };
    private readonly Label _configState = WizardLayout.Helper("");
    private readonly Button _newConfig = Theme.Primary(WizardLayout.MakeButton("&New configuration", 160));
    private readonly Button _openConfig = WizardLayout.MakeButton("&Open configuration...", 180);
    private readonly ListBox _recentList = new()
    {
        Height = 200, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BorderStyle = BorderStyle.None,
        AccessibleName = "Recent configurations",
    };
    private readonly Button _openRecent = WizardLayout.MakeButton("Open selected", 120);
    private readonly Button _removeRecent = WizardLayout.MakeButton("Remove from list", 130);
    private readonly LinkLabel _openFolder = new() { Text = "Open configurations folder", AutoSize = true, Margin = new Padding(12, 9, 3, 3) };

    private Control BuildConfigPage()
    {
        var page = WizardLayout.CreatePage("Configuration",
            "A configuration file stores one sync job: source, destination, column mapping and options. Create a new one or open an existing one; "
            + "it is saved automatically when you start the sync.");

        var current = WizardLayout.FieldGrid();
        WizardLayout.AddField(current, "Current configuration", _configCurrent, helpLabel: _configState);
        WizardLayout.AddWide(current, WizardLayout.ButtonRow(_newConfig, _openConfig));
        WizardLayout.AddBlock(page, WizardLayout.Group("Configuration file", current));

        var recent = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        recent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _recentList.Dock = DockStyle.Fill;
        recent.Controls.Add(_recentList, 0, 0);
        recent.Controls.Add(WizardLayout.Helper(
            $"Double-click a file to open it. Removing a file from this list does not delete it. Default folder: {RecentConfigurations.ConfigFolder}"), 0, 1);
        recent.Controls.Add(WizardLayout.ButtonRow(_openRecent, _removeRecent, _openFolder), 0, 2);
        WizardLayout.AddBlock(page, WizardLayout.Group("Recent configurations", recent));
        WizardLayout.AddFiller(page);

        _newConfig.Click += (_, _) => NewConfiguration();
        _openConfig.Click += (_, _) => OpenConfigurationDialog();
        _openRecent.Click += (_, _) => OpenSelectedRecent();
        _recentList.DoubleClick += (_, _) => OpenSelectedRecent();
        _recentList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                OpenSelectedRecent();
                e.Handled = true;
            }
        };
        _recentList.SelectedIndexChanged += (_, _) => UpdateRecentButtons();
        _recentList.DrawItem += DrawRecentItem;
        _removeRecent.Click += (_, _) =>
        {
            if (_recentList.SelectedItem is RecentItem item)
            {
                RecentConfigurations.Remove(item.Path);
                RefreshRecentList();
            }
        };
        _openFolder.LinkClicked += (_, _) => OpenFolder(RecentConfigurations.ConfigFolder);
        return WizardLayout.Root(page);
    }

    // Two lines per file: name (semibold) with the last-saved time, and the folder in muted ink.
    private void DrawRecentItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _recentList.Items[e.Index] is not RecentItem item)
        {
            return;
        }
        var selected = (e.State & DrawItemState.Selected) != 0;
        var current = _configPath is not null && item.Path.Equals(_configPath, StringComparison.OrdinalIgnoreCase);
        using (var back = new SolidBrush(selected ? Theme.AccentSoft : Color.White))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }
        if (selected)
        {
            using var bar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12);
        }
        var left = e.Bounds.X + 14;
        var width = e.Bounds.Width - 20;
        DateTime? saved = null;
        try { saved = File.GetLastWriteTime(item.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var name = Path.GetFileNameWithoutExtension(item.Path) + (current ? "   (open)" : "");
        TextRenderer.DrawText(e.Graphics, name, Theme.BaseSemibold, new Rectangle(left, e.Bounds.Y + 5, width - 170, 20), Theme.Text, flags);
        if (saved is { } when)
        {
            TextRenderer.DrawText(e.Graphics, $"saved {when:g}", Theme.Small, new Rectangle(left, e.Bounds.Y + 6, width, 20), Theme.TextMuted,
                flags | TextFormatFlags.Right);
        }
        TextRenderer.DrawText(e.Graphics, Path.GetDirectoryName(item.Path) ?? "", Theme.Small,
            new Rectangle(left, e.Bounds.Y + 25, width, 18), Theme.TextMuted, flags | TextFormatFlags.PathEllipsis);
        using var line = new Pen(Theme.Border);
        e.Graphics.DrawLine(line, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private void OpenSelectedRecent()
    {
        if (_recentList.SelectedItem is RecentItem item)
        {
            OpenConfiguration(item.Path);
        }
    }

    private void RefreshRecentList()
    {
        _recentList.BeginUpdate();
        try
        {
            _recentList.Items.Clear();
            foreach (var path in RecentConfigurations.Load())
            {
                _recentList.Items.Add(new RecentItem(path));
            }
        }
        finally
        {
            _recentList.EndUpdate();
        }
        UpdateRecentButtons();
    }

    private void UpdateRecentButtons()
    {
        _openRecent.Enabled = _recentList.SelectedItem is not null;
        _removeRecent.Enabled = _recentList.SelectedItem is not null;
    }

    private void UpdateConfigPage(bool dirty)
    {
        _configCurrent.Text = _configPath ?? "New configuration (not saved yet)";
        _configState.Text = dirty
            ? "Has unsaved changes. Use Save at the bottom, or start the sync (saves automatically)."
            : _configPath is null
                ? "Use Save as to choose a file name, or start the sync and you are asked for one."
                : "Saved.";
        _configState.ForeColor = dirty ? Theme.WarningText : Theme.TextMuted;
    }

    private sealed record RecentItem(string Path)
    {
        public override string ToString() => $"{System.IO.Path.GetFileName(Path)}    ({System.IO.Path.GetDirectoryName(Path)})";
    }

    // ---------------------------------------------------------------- step 2: source

    private readonly TextBox _url = new() { PlaceholderText = "https://yourorg.crm.dynamics.com" };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _clientId = new() { PlaceholderText = "empty = default public app (Interactive only)" };
    private readonly Label _clientIdHelp = WizardLayout.Helper("");
    private readonly TextBox _secret = new() { UseSystemPasswordChar = true };
    private readonly TextBox _entity = new() { PlaceholderText = "annotation" };
    private readonly RadioButton _readSdk = new() { Text = "Dataverse SDK (recommended)", AutoSize = true };
    private readonly RadioButton _readTds = new() { Text = "Dataverse TDS endpoint", AutoSize = true };
    private readonly RadioButton _readSql = new() { Text = "SQL Server table", AutoSize = true };
    private readonly Label _readHelp = WizardLayout.Helper("", margin: new Padding(0, 4, 3, 0));
    private readonly ComboBox _srcSqlAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _srcSqlConn = new() { PlaceholderText = "Server=tcp:myserver.database.windows.net,1433;" };
    private readonly TextBox _srcSqlUser = new() { PlaceholderText = "name@contoso.com (optional)" };
    private readonly TextBox _srcDatabase = new();
    private readonly Label _entityHelp = WizardLayout.Helper("");
    private Card? _dataverseGroup;
    private Card? _sqlSourceGroup;
    private readonly Button _testSource = Theme.Primary(WizardLayout.MakeButton("&Connect && test", 150));
    private readonly Label _sourceResult = WizardLayout.Helper("Not tested yet.", margin: new Padding(3, 6, 3, 8));

    private EntityMetadata? _meta;
    private string? _sourceOkKey;
    private string _sourceOkText = "";

    private AuthKind SelectedAuth => _auth.SelectedIndex == 1 ? AuthKind.ClientSecret : AuthKind.Interactive;

    private SourceKind SelectedSource => _readSql.Checked ? SourceKind.SqlTable : _readTds.Checked ? SourceKind.Tds : SourceKind.Sdk;

    private SqlAuthKind SelectedSourceSqlAuth => _srcSqlAuth.SelectedIndex switch
    {
        1 => SqlAuthKind.EntraInteractive,
        2 => SqlAuthKind.EntraDefault,
        _ => SqlAuthKind.ConnectionString,
    };

    private Control BuildSourcePage()
    {
        var page = WizardLayout.CreatePage("Source",
            "Where the rows come from. Choose how to read, enter the connection and the table, then click \"Connect & test\" "
            + "(or Next) to load the table's columns.", scroll: true);
        _testSource.Text = "&Connect && test";

        _auth.Items.AddRange(new object[]
        {
            "Interactive sign-in (browser, your own account)",
            "Client secret (app registration / application user)",
        });

        // The three methods side by side; one line below explains the chosen one.
        var read = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        read.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var radios = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        foreach (var r in new[] { _readSdk, _readTds, _readSql })
        {
            r.Margin = new Padding(0, 2, 28, 2);
            radios.Controls.Add(r);
        }
        read.Controls.Add(radios, 0, 0);
        read.Controls.Add(_readHelp, 0, 1);
        WizardLayout.AddBlock(page, WizardLayout.Group("Read method", read));

        var conn = WizardLayout.FieldGrid();
        WizardLayout.AddField(conn, "Environment URL", _url);
        WizardLayout.AddField(conn, "Authentication", _auth, "Interactive opens a sign-in window. Client secret is for unattended / scheduled runs.");
        WizardLayout.AddField(conn, "Client Id", _clientId, helpLabel: _clientIdHelp);
        WizardLayout.AddField(conn, "Client secret", _secret, $"Never saved. Leave empty to use the environment variable {SecretEnvVar}.");
        _dataverseGroup = WizardLayout.AddBlock(page, WizardLayout.Group("Dataverse connection", conn));

        _srcSqlAuth.Items.AddRange(new object[]
        {
            "Connection string as typed (Windows / SQL login)",
            "Microsoft Entra interactive (MFA) - Azure SQL",
            "Microsoft Entra default (managed identity / az login) - scheduled runs",
        });
        var sql = WizardLayout.FieldGrid();
        WizardLayout.AddField(sql, "Authentication", _srcSqlAuth);
        WizardLayout.AddField(sql, "Server / connection string", _srcSqlConn,
            "Server-level connection string, e.g. Server=tcp:myserver.database.windows.net,1433; A password is used for this session only and never saved.");
        WizardLayout.AddField(sql, "Entra user (UPN)", _srcSqlUser, "Optional, Entra interactive only: pre-fills the sign-in window.");
        WizardLayout.AddField(sql, "Database", _srcDatabase);
        _sqlSourceGroup = WizardLayout.AddBlock(page, WizardLayout.Group("SQL source connection", sql));

        var table = WizardLayout.FieldGrid();
        WizardLayout.AddField(table, "Table", _entity, helpLabel: _entityHelp);
        WizardLayout.AddBlock(page, WizardLayout.Group("Source table", table));
        WizardLayout.AddFiller(page);

        // Fixed footer: the test button and its result are always visible, whatever the window size.
        _sourceResult.Margin = new Padding(0, 6, 0, 2);
        return WizardLayout.WithFooter(page, WizardLayout.ButtonRow(_testSource), _sourceResult);
    }

    private void WireSourceEvents()
    {
        _auth.SelectedIndexChanged += (_, _) => UpdateAuthUi();
        _srcSqlAuth.SelectedIndexChanged += (_, _) => UpdateAuthUi();
        _testSource.Click += async (_, _) => await TestSourceAsync();
        foreach (var c in new Control[] { _url, _auth, _clientId, _entity, _readSdk, _readTds, _readSql, _srcSqlAuth, _srcSqlConn, _srcSqlUser, _srcDatabase })
        {
            HookInputs(c);
        }
        foreach (var t in new[] { _srcSqlConn, _srcSqlUser, _srcDatabase })
        {
            t.TextChanged += (_, _) => UpdateSourceStale();
        }
        _srcSqlAuth.SelectedIndexChanged += (_, _) => UpdateSourceStale();
        _readSql.CheckedChanged += (_, _) =>
        {
            UpdateAuthUi();
            UpdateSourceStale();
            UpdateStatuses();
        };
        _url.TextChanged += (_, _) => UpdateSourceStale();
        _auth.SelectedIndexChanged += (_, _) => UpdateSourceStale();
        _clientId.TextChanged += (_, _) => UpdateSourceStale();
        _entity.TextChanged += (_, _) => UpdateSourceStale();
        _readTds.CheckedChanged += (_, _) =>
        {
            UpdateSourceStale();
            UpdateStatuses();
        };
    }

    private void ApplySourceSettings(AppSettings s)
    {
        _url.Text = s.DataverseUrl;
        _auth.SelectedIndex = s.AuthType == AuthKind.ClientSecret ? 1 : 0;
        _clientId.Text = s.ClientId;
        _entity.Text = s.EntityName;
        _readTds.Checked = s.Source == SourceKind.Tds;
        _readSql.Checked = s.Source == SourceKind.SqlTable;
        _readSdk.Checked = s.Source == SourceKind.Sdk;
        _srcSqlAuth.SelectedIndex = s.SourceSqlAuth switch
        {
            SqlAuthKind.EntraInteractive => 1,
            SqlAuthKind.EntraDefault => 2,
            _ => 0,
        };
        _srcSqlConn.Text = s.SourceSqlConnectionString;
        _srcSqlUser.Text = s.SourceSqlUser;
        _srcDatabase.Text = s.SourceDatabaseName;
        UpdateAuthUi();
    }

    private void ResetSourceState()
    {
        _meta = null;
        _sourceOkKey = null;
        _sourceOkText = "";
        _rows.Clear();
        _checked.Clear();
        _forced.Clear();
        _loadedEntity = null;
        SetResult(_sourceResult, "Not tested yet.", Theme.TextMuted);
    }

    private void UpdateAuthUi()
    {
        var isSql = _readSql.Checked;
        _readHelp.Text = SelectedSource switch
        {
            SourceKind.Tds => "Reads through the SQL (TDS) endpoint on port 5558; often faster. It must be switched on for the environment "
                + "(Power Platform admin center > environment > Settings > Features). File content (documentbody) is not available.",
            SourceKind.SqlTable => "Copies from a SQL Server / Azure SQL table with a uniqueidentifier key, e.g. a table this app filled before. "
                + "A modifiedon column enables incremental runs; documentbody is converted between varbinary and base64 text as needed.",
            _ => "Works in every environment. Pages through the Web API and copies all columns, including file content (documentbody).",
        };
        if (_dataverseGroup is not null) _dataverseGroup.Visible = !isSql;
        if (_sqlSourceGroup is not null) _sqlSourceGroup.Visible = isSql;
        _srcSqlUser.Enabled = SelectedSourceSqlAuth == SqlAuthKind.EntraInteractive;
        _entity.PlaceholderText = isSql ? "dbo.annotation" : "annotation";
        _entityHelp.Text = isSql
            ? "Source table as schema.table, e.g. dbo.annotation. Needs a uniqueidentifier primary key and a modifiedon column."
            : "Logical name of the Dataverse table to copy, e.g. annotation (Notes, incl. attachments).";
        var isSecret = SelectedAuth == AuthKind.ClientSecret;
        _secret.Enabled = isSecret;
        _clientIdHelp.Text = isSecret
            ? "Required: the application (client) id of the app registration."
            : "Optional. Empty uses the default public app for interactive sign-in.";
    }

    private string SourceKey() => SelectedSource == SourceKind.SqlTable
        ? string.Join("|", "sql", _srcSqlConn.Text.Trim(), SelectedSourceSqlAuth, _srcSqlUser.Text.Trim().ToLowerInvariant(),
            _srcDatabase.Text.Trim().ToLowerInvariant(), _entity.Text.Trim().ToLowerInvariant())
        : string.Join("|",
            _url.Text.Trim().TrimEnd('/').ToLowerInvariant(), SelectedAuth, _clientId.Text.Trim().ToLowerInvariant(),
            _entity.Text.Trim().ToLowerInvariant(), _readTds.Checked);

    private string? SourceProblem()
    {
        if (_sourceOkKey is null)
        {
            return "Click \"Connect & test\" to continue.";
        }
        return _sourceOkKey == SourceKey() ? null : "Source settings changed - click \"Connect & test\" again.";
    }

    private void UpdateSourceStale()
    {
        if (_applying || _sourceOkKey is null || _loading)
        {
            return;
        }
        if (_sourceOkKey == SourceKey())
        {
            SetResult(_sourceResult, _sourceOkText, Theme.SuccessText);
        }
        else
        {
            SetResult(_sourceResult, "Settings changed since the last successful test. Click \"Connect & test\" again.", Theme.WarningText);
        }
    }

    /// <summary>STD-INT-006: secret from the textbox (memory only) or the environment; never persisted or logged.</summary>
    private string? GetSecret()
    {
        var typed = _secret.Text;
        return !string.IsNullOrEmpty(typed) ? typed : Environment.GetEnvironmentVariable(SecretEnvVar);
    }

    private bool ValidateForConnect(AppSettings s)
    {
        string? problem = null;
        if (s.Source == SourceKind.SqlTable)
        {
            if (string.IsNullOrWhiteSpace(s.SourceSqlConnectionString) || string.IsNullOrWhiteSpace(s.SourceDatabaseName))
            {
                problem = "Enter the source server / connection string and the database.";
            }
            else if (string.IsNullOrWhiteSpace(s.EntityName))
            {
                problem = "Enter the source table as schema.table, e.g. dbo.annotation.";
            }
        }
        else if (string.IsNullOrWhiteSpace(s.DataverseUrl) || !Uri.TryCreate(s.DataverseUrl, UriKind.Absolute, out _))
        {
            problem = "Enter a valid Dataverse URL, e.g. https://yourorg.crm.dynamics.com";
        }
        else if (string.IsNullOrWhiteSpace(s.EntityName))
        {
            problem = "Enter the table logical name, e.g. annotation.";
        }
        else if (s.AuthType == AuthKind.ClientSecret && (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrEmpty(GetSecret())))
        {
            problem = $"Client secret authentication needs a Client Id and a client secret (textbox or environment variable {SecretEnvVar}).";
        }
        if (problem is null)
        {
            return true;
        }
        MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private async Task TestSourceAsync()
    {
        if (!IsIdle)
        {
            return;
        }
        var s = CollectSettings();
        if (!ValidateForConnect(s))
        {
            return;
        }
        var secret = GetSecret();
        var key = SourceKey();
        BeginBusy();
        var isSql = s.Source == SourceKind.SqlTable;
        var where = isSql ? $"database [{s.SourceDatabaseName}]" : s.DataverseUrl;
        var signIn = !isSql || s.SourceSqlAuth == SqlAuthKind.EntraInteractive ? " A sign-in window may open." : "";
        SetResult(_sourceResult, $"Connecting to {where} and loading '{s.EntityName}'...{signIn}", Theme.Text);
        AppLog.Info($"Testing source {where}, table '{s.EntityName}', read method {s.Source}.");
        try
        {
            var ct = _formCts.Token;
            var (meta, tdsMessage) = await Task.Run(async () =>
            {
                if (isSql)
                {
                    using var sqlSource = await SqlTableSource.ConnectAsync(s, nonInteractive: false, ct).ConfigureAwait(false);
                    return (await sqlSource.GetMetadataAsync(ct).ConfigureAwait(false), (string?)null);
                }
                using var source = DataverseSource.Connect(s, secret, Guid.NewGuid());
                var m = await source.GetMetadataAsync(s.EntityName, ct).ConfigureAwait(false);
                string? tds = null;
                if (s.Source == SourceKind.Tds)
                {
                    try
                    {
                        tds = await TdsReader.TestConnectionAsync(s, source, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidOperationException("Connected to Dataverse, but the TDS (SQL) endpoint did not work. " + ex.Message
                            + " Or choose the Dataverse SDK read method.", ex);
                    }
                }
                return (m, tds);
            }, ct);

            if (IsDisposed)
            {
                return;
            }
            _meta = meta;
            PopulateAttributes(meta, s);
            _sourceOkKey = key;
            var display = meta.DisplayName?.UserLocalizedLabel?.Label;
            var attrs = _rows.Select(r => r.Attribute).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _sourceOkText = $"Connected. Table '{meta.LogicalName}'{(string.IsNullOrEmpty(display) ? "" : $" ({display})")}: {attrs} syncable "
                + (isSql ? $"columns loaded (key: {meta.PrimaryIdAttribute})." : "attributes loaded.")
                + (tdsMessage is null ? "" : "\nTDS endpoint: " + tdsMessage);
            SetResult(_sourceResult, _sourceOkText, Theme.SuccessText);
            AppLog.Info(_sourceOkText.Replace('\n', ' '));
        }
        catch (OperationCanceledException) when (_formCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Source test failed.", ex);
            if (!IsDisposed)
            {
                _sourceOkKey = null;
                SetResult(_sourceResult, "Failed: " + ex.Message, Theme.DangerText);
            }
        }
        finally
        {
            EndBusy();
        }
    }

    // ---------------------------------------------------------------- step 3: destination

    private readonly ComboBox _sqlAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _sqlConn = new();
    private readonly Label _sqlConnHelp = WizardLayout.Helper("");
    private readonly TextBox _sqlUser = new() { PlaceholderText = "name@contoso.com (optional)" };
    private readonly TextBox _database = new();
    private readonly TextBox _schema = new();
    private readonly TextBox _table = new();
    private readonly Button _testDest = Theme.Primary(WizardLayout.MakeButton("&Test && load destination table", 220));
    private readonly Label _destResult = WizardLayout.Helper("Not tested yet.", margin: new Padding(3, 6, 3, 8));

    private string? _destOkKey;
    private string _destOkText = "";

    private SqlAuthKind SelectedSqlAuth => _sqlAuth.SelectedIndex switch
    {
        1 => SqlAuthKind.EntraInteractive,
        2 => SqlAuthKind.EntraDefault,
        _ => SqlAuthKind.ConnectionString,
    };

    private Control BuildDestinationPage()
    {
        var page = WizardLayout.CreatePage("Destination (SQL)",
            "Where the rows go: SQL Server or Azure SQL Database. \"Test & load destination table\" (or Next) checks the connection "
            + "and reads the table's columns, so the next step can map onto an existing table.");

        _sqlAuth.Items.AddRange(new object[]
        {
            "Connection string as typed (Windows / SQL login)",
            "Microsoft Entra interactive (MFA) - Azure SQL",
            "Microsoft Entra default (managed identity / az login) - scheduled runs",
        });

        var conn = WizardLayout.FieldGrid();
        WizardLayout.AddField(conn, "Authentication", _sqlAuth);
        WizardLayout.AddField(conn, "Server / connection string", _sqlConn, helpLabel: _sqlConnHelp);
        WizardLayout.AddField(conn, "Entra user (UPN)", _sqlUser, "Optional, Entra interactive only: pre-fills the sign-in window.");
        WizardLayout.AddBlock(page, WizardLayout.Group("Server", conn));

        var target = WizardLayout.FieldGrid();
        WizardLayout.AddField(target, "Database", _database,
            "Azure SQL: create the database in the Azure portal first (to choose size and tier). SQL Server: a missing database is created.");
        WizardLayout.AddField(target, "Schema", _schema);
        WizardLayout.AddField(target, "Table", _table, "Existing table: rows are added and updated, nothing is removed. Missing table: it is created.");
        WizardLayout.AddBlock(page, WizardLayout.Group("Target table", target));
        WizardLayout.AddFiller(page);

        _destResult.Margin = new Padding(0, 6, 0, 2);
        return WizardLayout.WithFooter(page, WizardLayout.ButtonRow(_testDest), _destResult);
    }

    private void WireDestinationEvents()
    {
        _sqlAuth.SelectedIndexChanged += (_, _) => UpdateSqlAuthUi();
        _testDest.Click += async (_, _) => await TestDestinationAsync();
        foreach (var c in new Control[] { _sqlAuth, _sqlConn, _sqlUser, _database, _schema, _table })
        {
            HookInputs(c);
            if (c is TextBox t) t.TextChanged += (_, _) => UpdateDestinationStale();
        }
        _sqlAuth.SelectedIndexChanged += (_, _) => UpdateDestinationStale();
    }

    private void ApplyDestinationSettings(AppSettings s)
    {
        _sqlAuth.SelectedIndex = s.SqlAuth switch
        {
            SqlAuthKind.EntraInteractive => 1,
            SqlAuthKind.EntraDefault => 2,
            _ => 0,
        };
        _sqlConn.Text = s.SqlConnectionString;
        _sqlUser.Text = s.SqlUser;
        _database.Text = s.DatabaseName;
        _schema.Text = s.SchemaName;
        _table.Text = s.TableName;
        UpdateSqlAuthUi();
    }

    private void ResetDestinationState()
    {
        _destColumns = null;
        _destLoaded = false;
        _destTable = "";
        _destOkKey = null;
        _destOkText = "";
        SetResult(_destResult, "Not tested yet.", Theme.TextMuted);
    }

    private void UpdateSqlAuthUi()
    {
        var auth = SelectedSqlAuth;
        _sqlUser.Enabled = auth == SqlAuthKind.EntraInteractive;
        _sqlConn.PlaceholderText = auth == SqlAuthKind.ConnectionString
            ? "Server=localhost;Integrated Security=true;TrustServerCertificate=true"
            : "Server=tcp:myserver.database.windows.net,1433;";
        _sqlConnHelp.Text = auth switch
        {
            SqlAuthKind.ConnectionString =>
                "Server-level connection string (no database), e.g. Server=localhost;Integrated Security=true;TrustServerCertificate=true, "
                + "or with User ID=...;Password=... for a SQL login. A password is used for this session only and never saved.",
            SqlAuthKind.EntraInteractive =>
                "Example: Server=tcp:myserver.database.windows.net,1433; - Entra sign-in (with MFA) and encryption are added automatically. A sign-in window opens.",
            _ =>
                "Example: Server=tcp:myserver.database.windows.net,1433; - signs in with managed identity, az login or Visual Studio. No window; suitable for scheduled runs.",
        };
    }

    private string DestinationKey() => string.Join("|",
        _sqlConn.Text.Trim(), SelectedSqlAuth, _sqlUser.Text.Trim().ToLowerInvariant(),
        _database.Text.Trim().ToLowerInvariant(), _schema.Text.Trim().ToLowerInvariant(), _table.Text.Trim().ToLowerInvariant());

    private string? DestinationProblem()
    {
        if (SqlTableSource.IsSameAsDestination(CollectSettings()))
        {
            return "The destination is the same table as the SQL source. Choose another destination table.";
        }
        if (_destOkKey is null)
        {
            return "Click \"Test & load destination table\" to continue.";
        }
        return _destOkKey == DestinationKey() ? null : "Destination settings changed - click \"Test & load destination table\" again.";
    }

    private void UpdateDestinationStale()
    {
        if (_applying || _destOkKey is null || _loading)
        {
            return;
        }
        if (_destOkKey == DestinationKey())
        {
            SetResult(_destResult, _destOkText, Theme.SuccessText);
        }
        else
        {
            SetResult(_destResult, "Settings changed since the last successful test. Click \"Test & load destination table\" again.", Theme.WarningText);
        }
    }

    private async Task TestDestinationAsync()
    {
        if (!IsIdle)
        {
            return;
        }
        var s = CollectSettings();
        if (string.IsNullOrWhiteSpace(s.SqlConnectionString) || string.IsNullOrWhiteSpace(s.DatabaseName)
            || string.IsNullOrWhiteSpace(s.SchemaName) || string.IsNullOrWhiteSpace(s.TableName))
        {
            MessageBox.Show(this, "Fill in the server / connection string, database, schema and table first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (SqlTableSource.IsSameAsDestination(s))
        {
            MessageBox.Show(this, $"The destination [{s.DatabaseName}].[{s.SchemaName}].[{s.TableName}] is the SQL source table itself. "
                + "Choose another destination table.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var key = DestinationKey();
        BeginBusy();
        SetResult(_destResult, $"Connecting and reading [{s.DatabaseName}].[{s.SchemaName}].[{s.TableName}]..."
            + (s.SqlAuth == SqlAuthKind.EntraInteractive ? " A sign-in window may open." : ""), Theme.Text);
        try
        {
            var ct = _formCts.Token;
            var target = await Task.Run(() => new SqlTarget(s.BuildSqlConnectionString(), s.DatabaseName, s.SchemaName, s.TableName), ct);
            var columns = await Task.Run(() => target.GetTableColumnsAsync(ct), ct);
            if (IsDisposed)
            {
                return;
            }
            _destColumns = columns;
            _destLoaded = true;
            _destTable = $"[{s.DatabaseName}].{target.FullTableName}";
            var mapped = AutoMap();
            if (_rows.Count == 0)
            {
                RebuildGrid();
            }
            _destOkKey = key;
            _destOkText = columns is null
                ? $"Connected. {_destTable} does not exist yet (database or table): it will be created on the first sync with the columns chosen in the next step."
                : $"Connected. {_destTable} exists with {columns.Count} columns; {mapped} attribute(s) map onto existing columns. "
                  + "Existing rows are kept; synced rows are added or updated.";
            SetResult(_destResult, _destOkText, Theme.SuccessText);
            AppLog.Info(_destOkText);
            UpdateStatuses();
        }
        catch (OperationCanceledException) when (_formCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading the destination table failed.", ex);
            if (!IsDisposed)
            {
                _destOkKey = null;
                SetResult(_destResult, "Failed: " + ex.Message, Theme.DangerText);
            }
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Shows a result line; success, warning and error results render as a tinted banner with an icon.</summary>
    private static void SetResult(Label label, string text, Color color)
    {
        var tone = Theme.ToneOf(color);
        var (fore, back, icon) = Theme.Colors(tone);
        var banner = tone is Theme.Tone.Success or Theme.Tone.Warning or Theme.Tone.Danger or Theme.Tone.Info && text.Length > 0;
        label.Text = banner ? $"{icon}  {text}" : text;
        label.ForeColor = fore;
        label.BackColor = banner ? back : Color.Transparent;
        label.Padding = banner ? new Padding(10, 8, 10, 8) : Padding.Empty;
        label.AccessibleName = text; // read without the icon glyph
    }
}
