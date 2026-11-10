using DataverseToSql.Sync;
using DataverseToSql.UI;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseToSql;

// Step 4: attribute selection and destination column mapping.
public sealed partial class MainForm
{
    private const int ColSync = 0, ColAttr = 1, ColDisplay = 2, ColType = 3, ColTarget = 4, ColSqlType = 5, ColStatus = 6;
    private const int ViewAll = 0, ViewSelected = 1, ViewNotSelected = 2, ViewAttention = 3, ViewNew = 4, ViewRenamed = 5;

    private readonly TextBox _filter = new() { PlaceholderText = "Search name, display name or type...", Width = 300, AccessibleName = "Search columns" };
    private readonly ComboBox _view = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, AccessibleName = "Show" };
    private readonly Button _selectAll = WizardLayout.MakeButton("Select all", 90);
    private readonly Button _selectNone = WizardLayout.MakeButton("Select none", 95);
    private readonly Button _autoMap = WizardLayout.MakeButton("Auto-map by name", 140);
    private readonly Button _resetNames = WizardLayout.MakeButton("Reset names", 105);
    private readonly FlowLayoutPanel _mapChips = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
    private readonly DataGridView _grid = new() { AccessibleName = "Column mapping" };
    private readonly Label _attrInfo = WizardLayout.Helper("", Theme.Text, new Padding(3, 6, 3, 2));
    private readonly Label _docWarn = WizardLayout.Helper(
        "documentbody holds the file content of attachments and is very large; it is unchecked by default (page size capped at 500 when selected).",
        Theme.WarningText, new Padding(3, 2, 3, 2));
    private readonly Label _tdsDocWarn = WizardLayout.Helper(
        "documentbody is selected but the TDS read method is chosen; documentbody is only supported with the Dataverse SDK read method (step 2).",
        Theme.DangerText, new Padding(3, 2, 3, 2));
    private readonly Label _destInfo = WizardLayout.Helper("", null, new Padding(3, 2, 3, 2));

    // Attribute state: checked flags survive filtering.
    private readonly Dictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MapRow> _rows = new();
    private readonly Dictionary<string, string> _mapping = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MapRow, MapState> _states = new();
    private IReadOnlyList<DestinationColumn>? _destColumns;
    private bool _destLoaded;
    private string _destTable = "";
    private readonly HashSet<string> _forced = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedEntity;
    private bool _suppressGridEvents;

    private enum MapKind { Off, Pending, Ok, New, Warn, Error }

    private sealed record MapState(MapKind Kind, string Text, string DestType);

    private Control BuildMappingPage()
    {
        var page = WizardLayout.CreatePage("Column mapping",
            "Choose which columns to copy and where each one goes. Destination columns with the same name are matched automatically "
            + "(ignoring case and underscores); pick another column in \"Destination column\" to rename. The primary id and modifiedon are always copied.",
            scroll: false);

        _view.Items.AddRange(new object[] { "Show: all columns", "Show: selected", "Show: not selected", "Show: needs attention", "Show: new in destination", "Show: renamed" });
        _view.SelectedIndex = ViewAll;

        var toolbar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        _filter.Margin = new Padding(0, 5, 8, 3);
        _view.Margin = new Padding(0, 5, 16, 3);
        toolbar.Controls.AddRange(new Control[] { _filter, _view, _selectAll, _selectNone, _autoMap, _resetNames });
        WizardLayout.AddBlock(page, toolbar);
        WizardLayout.AddBlock(page, _mapChips);

        var gridCard = new Card("") { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(1), Margin = new Padding(0, 0, 0, 6) };
        _grid.Dock = DockStyle.Fill;
        gridCard.Controls.Add(_grid);
        WizardLayout.AddBlock(page, gridCard, fill: true);
        WizardLayout.AddBlock(page, WizardLayout.Helper(
            "Status:  ✔ existing column   ℹ new column, added on the first run   ⚠ check type or length   ✖ must be fixed before you continue. "
            + "Select all / none apply to the rows shown.", null, new Padding(3, 0, 3, 2)));
        WizardLayout.AddBlock(page, _attrInfo);
        WizardLayout.AddBlock(page, _docWarn);
        WizardLayout.AddBlock(page, _tdsDocWarn);
        WizardLayout.AddBlock(page, _destInfo);
        _tdsDocWarn.Visible = false;
        return page;
    }

    private void WireMappingEvents()
    {
        _filter.TextChanged += (_, _) => ApplyFilter();
        _view.SelectedIndexChanged += (_, _) => ApplyFilter();
        _selectAll.Click += (_, _) => SetVisibleChecked(true);
        _selectNone.Click += (_, _) => SetVisibleChecked(false);
        _autoMap.Click += (_, _) =>
        {
            var before = _mapping.Count;
            var onExisting = AutoMap();
            _attrInfo.Text = $"Auto-map: {_mapping.Count - before} new match(es); {onExisting} column(s) now map onto existing destination columns.";
            UpdateStatuses();
            OnInputChanged();
        };
        _resetNames.Click += (_, _) =>
        {
            if (_mapping.Count == 0) return;
            if (MessageBox.Show(this, $"Reset all {_mapping.Count} renamed column(s) to their source names?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _mapping.Clear();
            RebuildGrid();
            OnInputChanged();
        };
        _grid.CellValueChanged += OnGridCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); // apply checkbox/combo changes immediately
            }
        };
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CellPainting += (_, e) => PaintPillCell(_grid, ColStatus, e);
    }

    private void ConfigureGrid()
    {
        Theme.StyleGrid(_grid);
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ShowCellToolTips = true;
        _grid.StandardTab = true; // Tab leaves the grid instead of walking every cell

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Copy", FillWeight = 9, MinimumWidth = 46, ToolTipText = "Copy this column" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source column", ReadOnly = true, FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Display name", ReadOnly = true, FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source type", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Destination column", FillWeight = 34, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, ToolTipText = "Pick an existing destination column to rename",
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SQL type", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", ReadOnly = true, FillWeight = 52 });
        foreach (DataGridViewColumn c in _grid.Columns)
        {
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private string? MappingProblem()
    {
        if (_loadedEntity is null)
        {
            return "Columns are not loaded - test the source in step 2.";
        }
        if (!_checked.Any(kv => kv.Value))
        {
            return "Select at least one column.";
        }
        var duplicates = DuplicateTargets();
        if (duplicates.Count > 0)
        {
            return $"Two or more columns map to the same destination column: {string.Join(", ", duplicates.Take(5))}.";
        }
        var readOnly = SelectedRows().Select(TargetOf).Where(t => DestColumn(t) is { IsReadOnly: true })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return readOnly.Count > 0
            ? $"Read-only destination column(s), filled by SQL Server (e.g. temporal period columns): {string.Join(", ", readOnly.Take(5))}. Deselect or remap them."
            : null;
    }

    private List<MapRow> SelectedRows() => _rows.Where(r => _checked.TryGetValue(r.Attribute, out var c) && c).ToList();

    private HashSet<string> DuplicateTargets() => SelectedRows().GroupBy(TargetOf, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing destination columns that are NOT NULL without default and not filled by the sync.</summary>
    private List<string> RequiredUnmappedColumns()
    {
        if (!_destLoaded || _destColumns is null)
        {
            return new List<string>();
        }
        var used = SelectedRows().Select(TargetOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _destColumns.Where(d => !used.Contains(d.Name) && d.RequiresValue && !d.Name.Equals("__syncedon", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name).ToList();
    }

    /// <summary>Maps unmapped rows to a destination column with the same name, ignoring case and underscores. Returns rows now pointing at an existing column.</summary>
    private int AutoMap()
    {
        if (_destColumns is null)
        {
            return 0;
        }
        static string Norm(string n) => n.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        var byNorm = _destColumns.GroupBy(c => Norm(c.Name)).ToDictionary(g => g.Key, g => g.First().Name);
        var count = 0;
        foreach (var row in _rows)
        {
            if (_mapping.ContainsKey(row.DefaultColumn))
            {
                count += DestColumn(_mapping[row.DefaultColumn]) is not null ? 1 : 0;
                continue;
            }
            if (DestColumn(row.DefaultColumn) is not null)
            {
                count++;
                continue;
            }
            if (byNorm.TryGetValue(Norm(row.DefaultColumn), out var match))
            {
                _mapping[row.DefaultColumn] = match;
                count++;
            }
        }
        RebuildGrid();
        return count;
    }

    private DestinationColumn? DestColumn(string name) =>
        _destColumns?.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string TargetOf(MapRow row) =>
        _mapping.TryGetValue(row.DefaultColumn, out var t) && !string.IsNullOrWhiteSpace(t) ? t : row.DefaultColumn;

    private void PopulateAttributes(EntityMetadata meta, AppSettings s)
    {
        _rows.Clear();
        _checked.Clear();
        _forced.Clear();

        if (!string.IsNullOrEmpty(meta.PrimaryIdAttribute))
        {
            _forced.Add(meta.PrimaryIdAttribute);
        }
        _forced.Add("modifiedon");

        var syncable = (meta.Attributes ?? Array.Empty<AttributeMetadata>())
            .Where(ColumnMap.IsSyncable)
            .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var describe = syncable.ToDictionary(a => a.LogicalName, ColumnMap.Describe, StringComparer.OrdinalIgnoreCase);
        var labels = syncable.ToDictionary(a => a.LogicalName, a => a.DisplayName?.UserLocalizedLabel?.Label ?? "", StringComparer.OrdinalIgnoreCase);

        var sameEntity = string.Equals(_settings.EntityName, meta.LogicalName, StringComparison.OrdinalIgnoreCase);
        var saved = sameEntity
            ? new HashSet<string>(s.SelectedAttributes, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!sameEntity && !string.Equals(_loadedEntity, meta.LogicalName, StringComparison.OrdinalIgnoreCase))
        {
            _mapping.Clear(); // mappings belong to the previous entity
        }

        // One row per destination column: a polymorphic lookup gives two rows (id and _entity).
        var allColumns = ColumnMap.Build(meta, syncable.Select(a => a.LogicalName));
        foreach (var c in allColumns.OrderBy(c => c.IsPrimaryKey ? 0 : 1).ThenBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase))
        {
            var desc = c.Kind == ColumnKind.LookupEntity
                ? "lookup target table"
                : describe.TryGetValue(c.AttributeName, out var d) ? d : c.SqlType;
            var label = labels.TryGetValue(c.AttributeName, out var l) ? l : "";
            if (c.Kind == ColumnKind.LookupEntity && label.Length > 0) label += " (table)";
            if (c.IsPrimaryKey) label = (label.Length > 0 ? label + " · " : "") + "primary key";
            else if (c.AttributeName.Equals("modifiedon", StringComparison.OrdinalIgnoreCase) && c.Kind != ColumnKind.LookupEntity)
                label = (label.Length > 0 ? label + " · " : "") + "change tracking";
            _rows.Add(new MapRow(c.AttributeName, c.ColumnName, desc, c.SqlType, _forced.Contains(c.AttributeName), label));
        }

        foreach (var name in _rows.Select(r => r.Attribute).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _checked[name] = _forced.Contains(name)
                || (saved.Count > 0 ? saved.Contains(name) : !string.Equals(name, DocumentBody, StringComparison.OrdinalIgnoreCase));
        }

        _loadedEntity = meta.LogicalName;
        if (_destLoaded)
        {
            AutoMap(); // also rebuilds the grid
        }
        else
        {
            RebuildGrid();
        }
    }

    private void RebuildGrid()
    {
        _suppressGridEvents = true;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            var destNames = _destColumns?.Select(c => c.Name).ToList() ?? new List<string>();
            foreach (var row in _rows)
            {
                var index = _grid.Rows.Add();
                var gridRow = _grid.Rows[index];
                gridRow.Tag = row;
                gridRow.Cells[ColSync].Value = _checked.TryGetValue(row.Attribute, out var c) && c;
                gridRow.Cells[ColSync].ReadOnly = row.Forced;
                gridRow.Cells[ColAttr].Value = row.DefaultColumn;
                gridRow.Cells[ColDisplay].Value = row.DisplayName;
                gridRow.Cells[ColType].Value = row.Description;
                gridRow.Cells[ColDisplay].Style.ForeColor = Theme.TextMuted;
                gridRow.Cells[ColType].Style.ForeColor = Theme.TextMuted;

                var target = TargetOf(row);
                var combo = (DataGridViewComboBoxCell)gridRow.Cells[ColTarget];
                var items = new List<string> { row.DefaultColumn };
                items.AddRange(destNames.Where(n => !n.Equals(row.DefaultColumn, StringComparison.OrdinalIgnoreCase)));
                if (!items.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(target);
                }
                combo.Items.AddRange(items.Cast<object>().ToArray());
                combo.Value = items.First(i => i.Equals(target, StringComparison.OrdinalIgnoreCase));

                string? tip = null;
                if (string.Equals(row.Attribute, DocumentBody, StringComparison.OrdinalIgnoreCase))
                {
                    tip = "Large: base64 file content of the note attachment. Slows the sync and grows the database considerably.";
                }
                else if (row.Forced)
                {
                    tip = "Always copied: used as primary key / incremental watermark.";
                }
                if (tip is not null)
                {
                    foreach (DataGridViewCell cell in gridRow.Cells)
                    {
                        cell.ToolTipText = tip;
                    }
                }
            }
        }
        finally
        {
            _grid.ResumeLayout();
            _suppressGridEvents = false;
        }
        UpdateStatuses();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = _filter.Text.Trim();
        var view = _view.SelectedIndex;
        _grid.CurrentCell = null; // the current row cannot be hidden
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            var row = (MapRow)gridRow.Tag!;
            var target = TargetOf(row);
            var kind = _states.TryGetValue(row, out var st) ? st.Kind : MapKind.Pending;
            var selected = _checked.TryGetValue(row.Attribute, out var c) && c;
            var inView = view switch
            {
                ViewSelected => selected,
                ViewNotSelected => !selected,
                ViewAttention => kind is MapKind.Warn or MapKind.Error,
                ViewNew => kind == MapKind.New,
                ViewRenamed => !target.Equals(row.DefaultColumn, StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
            gridRow.Visible = inView && (filter.Length == 0
                || row.DefaultColumn.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || target.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        UpdateAttributeInfo();
    }

    private void SetVisibleChecked(bool value)
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.Visible && gridRow.Tag is MapRow row)
            {
                _checked[row.Attribute] = value || row.Forced;
            }
        }
        SyncCheckCells();
        UpdateStatuses();
        OnInputChanged();
    }

    private void SyncCheckCells()
    {
        _suppressGridEvents = true;
        try
        {
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                if (gridRow.Tag is MapRow row)
                {
                    gridRow.Cells[ColSync].Value = _checked.TryGetValue(row.Attribute, out var c) && c;
                }
            }
        }
        finally
        {
            _suppressGridEvents = false;
        }
    }

    private void OnGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressGridEvents || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not MapRow row)
        {
            return;
        }
        var cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        if (e.ColumnIndex == ColSync)
        {
            var value = cell.Value is true;
            _checked[row.Attribute] = value || row.Forced;
            SyncCheckCells(); // a lookup's id and _entity rows share one checkbox
        }
        else if (e.ColumnIndex == ColTarget)
        {
            var target = cell.Value as string;
            if (string.IsNullOrWhiteSpace(target) || target.Equals(row.DefaultColumn, StringComparison.OrdinalIgnoreCase))
            {
                _mapping.Remove(row.DefaultColumn);
            }
            else
            {
                _mapping[row.DefaultColumn] = target;
            }
        }
        UpdateStatuses();
        OnInputChanged();
    }

    // Type families: a different family (e.g. text into an int column) may fail on insert; text accepts everything.
    private static string TypeFamily(string sqlType) => sqlType.Split('(')[0].Trim().ToLowerInvariant() switch
    {
        "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" or "xml" => "text",
        "int" or "bigint" or "smallint" or "tinyint" => "integer",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "number",
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "date",
        "uniqueidentifier" => "guid",
        "bit" => "bit",
        "varbinary" or "binary" or "image" => "binary",
        var other => other,
    };

    private static int? TextLength(string sqlType)
    {
        var open = sqlType.IndexOf('(');
        if (open < 0 || TypeFamily(sqlType) != "text") return null;
        var inner = sqlType[(open + 1)..].TrimEnd(')').Trim();
        return inner.Equals("max", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : int.TryParse(inner, out var n) ? n : null;
    }

    /// <summary>Null when the source type fits the destination column, otherwise a short warning.</summary>
    private static string? TypeWarning(string sourceSqlType, string destType)
    {
        var from = TypeFamily(sourceSqlType);
        var to = TypeFamily(destType);
        if (from != to)
        {
            var fits = to == "text" || (from == "binary" && to == "text") || (from is "integer" or "bit" && to is "integer" or "number");
            return fits ? null : $"type differs (source {sourceSqlType})";
        }
        if (TextLength(sourceSqlType) is { } srcLen && TextLength(destType) is { } destLen && destLen < srcLen)
        {
            return $"may truncate (source {srcLen:N0} chars)";
        }
        return null;
    }

    private void UpdateStatuses()
    {
        var selectedRows = SelectedRows();
        var duplicates = DuplicateTargets();
        _states.Clear();

        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.Tag is not MapRow row)
            {
                continue;
            }
            var selected = _checked.TryGetValue(row.Attribute, out var c) && c;
            var target = TargetOf(row);
            MapState state;
            if (!selected)
            {
                state = new MapState(MapKind.Off, "not copied", "");
            }
            else if (duplicates.Contains(target))
            {
                state = new MapState(MapKind.Error, "Duplicate: another column maps here", row.SqlType);
            }
            else if (!_destLoaded)
            {
                state = new MapState(MapKind.Pending, "destination not tested yet", row.SqlType);
            }
            else if (_destColumns is null)
            {
                state = new MapState(MapKind.New, "new table", row.SqlType);
            }
            else if (DestColumn(target) is { IsReadOnly: true } readOnlyDest)
            {
                state = new MapState(MapKind.Error, "read-only: filled by SQL Server", readOnlyDest.TypeDisplay);
            }
            else if (DestColumn(target) is { } dest)
            {
                state = TypeWarning(row.SqlType, dest.TypeDisplay) is { } warning
                    ? new MapState(MapKind.Warn, warning, dest.TypeDisplay)
                    : new MapState(MapKind.Ok, "existing column", dest.TypeDisplay);
            }
            else
            {
                state = new MapState(MapKind.New, "new column", row.SqlType);
            }
            _states[row] = state;
            gridRow.Cells[ColSqlType].Value = state.DestType;
            gridRow.Cells[ColStatus].Value = state.Text;
            gridRow.Cells[ColStatus].Tag = state.Kind switch
            {
                MapKind.Ok => Theme.Tone.Success,
                MapKind.New => Theme.Tone.Info,
                MapKind.Warn => Theme.Tone.Warning,
                MapKind.Error => Theme.Tone.Danger,
                _ => Theme.Tone.Muted,
            };
            gridRow.DefaultCellStyle.ForeColor = selected ? Theme.Text : Theme.TextMuted;
        }

        // Destination columns nobody fills: required ones would break every INSERT.
        var required = new List<string>();
        if (_destLoaded && _destColumns is not null)
        {
            var used = selectedRows.Select(TargetOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unmapped = _destColumns.Where(d => !used.Contains(d.Name) && !d.Name.Equals("__syncedon", StringComparison.OrdinalIgnoreCase)).ToList();
            required = unmapped.Where(d => d.RequiresValue).Select(d => d.Name).ToList();
            SetResult(_destInfo, $"Destination {_destTable}: {_destColumns.Count} columns, {unmapped.Count} not filled by the sync"
                + (unmapped.Count > 0 ? $" ({string.Join(", ", unmapped.Take(8).Select(u => u.Name))}{(unmapped.Count > 8 ? ", ..." : "")})" : "")
                + (required.Count > 0 ? $". NOT NULL without default, inserts will fail: {string.Join(", ", required)}" : "."),
                required.Count > 0 ? Theme.DangerText : Theme.TextMuted);
        }
        else if (_destLoaded)
        {
            SetResult(_destInfo, $"Destination {_destTable} does not exist yet; the sync creates it with the selected columns.", Theme.TextMuted);
        }
        else
        {
            SetResult(_destInfo, "Destination not tested yet: test it in step 3 to map onto an existing table.", Theme.TextMuted);
        }

        var docSelected = _checked.TryGetValue(DocumentBody, out var doc) && doc;
        _tdsDocWarn.Visible = docSelected && _readTds.Checked;
        UpdateMappingChips(required.Count);
        UpdateAttributeInfo();
    }

    private void UpdateMappingChips(int requiredUnfilled)
    {
        var chips = new List<(string Text, Theme.Tone Tone)>();
        if (_loadedEntity is not null)
        {
            var selected = _states.Where(kv => kv.Value.Kind != MapKind.Off).ToList();
            chips.Add(($"{_checked.Count(kv => kv.Value)} of {_checked.Count} columns selected", Theme.Tone.Neutral));
            var existing = selected.Count(kv => kv.Value.Kind == MapKind.Ok);
            var created = selected.Count(kv => kv.Value.Kind == MapKind.New);
            var renamed = _rows.Count(r => _checked.TryGetValue(r.Attribute, out var c) && c && !TargetOf(r).Equals(r.DefaultColumn, StringComparison.OrdinalIgnoreCase));
            var warn = selected.Count(kv => kv.Value.Kind == MapKind.Warn);
            var errors = selected.Count(kv => kv.Value.Kind == MapKind.Error);
            if (existing > 0) chips.Add(($"{existing} map to existing columns", Theme.Tone.Success));
            if (created > 0) chips.Add(($"{created} new columns", Theme.Tone.Info));
            if (renamed > 0) chips.Add(($"{renamed} renamed", Theme.Tone.Neutral));
            if (warn > 0) chips.Add(($"{warn} to check", Theme.Tone.Warning));
            if (errors > 0) chips.Add(($"{errors} must be fixed", Theme.Tone.Danger));
            if (requiredUnfilled > 0) chips.Add(($"{requiredUnfilled} required destination column(s) not filled", Theme.Tone.Danger));
        }
        else
        {
            chips.Add(("Test the source in step 2 to load its columns", Theme.Tone.Muted));
        }

        var key = string.Join("|", chips.Select(c => c.Text + c.Tone));
        if (Equals(_mapChips.Tag, key)) return;
        _mapChips.Tag = key;
        _mapChips.SuspendLayout();
        foreach (var c in _mapChips.Controls.Cast<Control>().ToList()) c.Dispose();
        foreach (var (text, tone) in chips)
        {
            var pill = new Pill();
            _mapChips.Controls.Add(pill);
            pill.Set(text, tone);
        }
        _mapChips.ResumeLayout();
    }

    private void UpdateAttributeInfo()
    {
        if (_loadedEntity is null)
        {
            return;
        }
        var shown = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);
        _attrInfo.Text = $"{_loadedEntity}: {shown} of {_grid.Rows.Count} rows shown.";
    }

    /// <summary>One destination column produced by a source attribute (a polymorphic lookup gives two).</summary>
    private sealed record MapRow(string Attribute, string DefaultColumn, string Description, string SqlType, bool Forced, string DisplayName);
}
