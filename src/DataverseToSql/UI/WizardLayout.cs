namespace DataverseToSql.UI;

/// <summary>Small layout helpers for the code-only wizard pages (no designer files).</summary>
internal static class WizardLayout
{
    /// <summary>Tag that marks a label whose text wraps to the width of its page.</summary>
    public const string WrapTag = "wrap";

    /// <summary>Tag of a page host (scrolling page + fixed footer); wrapping is applied per part.</summary>
    private const string HostTag = "host";

    /// <summary>Width of the label column in field grids.</summary>
    public const int LabelColumnWidth = 170;

    /// <summary>Horizontal page padding; the fixed footer uses the same so its content lines up.</summary>
    private const int PageSide = 22;

    /// <summary>Creates a page: title, description and a single column for content blocks.</summary>
    public static TableLayoutPanel CreatePage(string title, string description, bool scroll = true)
    {
        // Scrolling pages: an auto-sized table docked to the top of a scrolling panel. (A filled TableLayoutPanel with
        // AutoScroll squeezes its rows instead of showing a scrollbar, which hid content in small windows.)
        var page = new TableLayoutPanel
        {
            Dock = scroll ? DockStyle.Top : DockStyle.Fill, AutoSize = scroll, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Padding = new Padding(PageSide, 12, PageSide, 8), BackColor = Theme.PageBack,
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (scroll)
        {
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.PageBack, Tag = HostTag };
            scroller.Controls.Add(page);
            scroller.VisibleChanged += (_, _) => UpdateWrapping(page);
        }
        AddBlock(page, new Label
        {
            Text = title, AutoSize = true, Font = Theme.PageTitle, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false,
        });
        AddBlock(page, Helper(description, Theme.TextMuted, new Padding(0, 0, 0, 8)));
        page.Resize += (_, _) => UpdateWrapping(page);
        page.VisibleChanged += (_, _) => UpdateWrapping(page);
        return page;
    }

    /// <summary>
    /// Wraps <paramref name="page"/> with a footer that stays visible while the page scrolls, so the page's main action
    /// (test, start, stop) and its result are never hidden below the fold.
    /// </summary>
    public static Control WithFooter(TableLayoutPanel page, params Control[] rows)
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Padding = new Padding(PageSide, 8, PageSide, 6), BackColor = Theme.PageBack,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var r in rows)
        {
            if (r is not Label && r.Dock == DockStyle.None)
            {
                r.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            }
            footer.Controls.Add(r);
        }
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, PageSide, 0, footer.Width - PageSide, 0);
        };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PageBack, Tag = HostTag };
        host.Controls.Add(Root(page)); // fill: docked last
        host.Controls.Add(footer); // bottom: docked first
        footer.Resize += (_, _) => UpdateWrapping(footer);
        host.VisibleChanged += (_, _) => UpdateWrapping(host);
        return host;
    }

    /// <summary>The control to put on screen for <paramref name="page"/>: its scrolling panel, or the page itself.</summary>
    public static Control Root(TableLayoutPanel page) => page.Parent ?? (Control)page;

    /// <summary>Scrolls the page so <paramref name="control"/> is visible.</summary>
    public static void ScrollIntoView(TableLayoutPanel page, Control control) =>
        (page.Parent as ScrollableControl)?.ScrollControlIntoView(control);

    /// <summary>Adds a content block to a page; <paramref name="fill"/> gives it the remaining height.</summary>
    public static T AddBlock<T>(TableLayoutPanel page, T control, bool fill = false) where T : Control
    {
        var row = page.RowCount++;
        page.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        control.Dock = fill ? DockStyle.Fill : control.Dock;
        if (!fill && control is not Label)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        }
        page.Controls.Add(control, 0, row);
        return control;
    }

    /// <summary>Adds a final filler row so blocks stay at the top.</summary>
    public static void AddFiller(TableLayoutPanel page)
    {
        page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    }

    /// <summary>Creates a titled card that sizes to its content.</summary>
    public static Card Group(string title, Control content)
    {
        var box = new Card(title) { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    /// <summary>Creates a two-column grid: label column and field column.</summary>
    public static TableLayoutPanel FieldGrid()
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    /// <summary>Adds a labelled field and an optional gray helper text under it.</summary>
    public static void AddField(TableLayoutPanel grid, string label, Control control, string? help = null, Label? helpLabel = null)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 3, 2), Font = Theme.BaseSemibold, ForeColor = Theme.Text }, 0, row);
        switch (control)
        {
            case TextBox:
                control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                break;
            case ComboBox:
                control.Anchor = AnchorStyles.Left;
                control.Width = Math.Max(control.Width, 460);
                break;
            case NumericUpDown:
                control.Anchor = AnchorStyles.Left;
                control.Width = 110;
                break;
            default:
                control.Anchor = AnchorStyles.Left;
                break;
        }
        if (string.IsNullOrEmpty(control.AccessibleName))
        {
            control.AccessibleName = label; // programmatic label for screen readers
        }
        control.Margin = new Padding(3, 3, 3, help is null && helpLabel is null ? 5 : 0);
        grid.Controls.Add(control, 1, row);
        if (help is not null || helpLabel is not null)
        {
            AddWide(grid, helpLabel ?? Helper(help!));
        }
    }

    /// <summary>Adds a control in the field column only (checkbox, button, helper text).</summary>
    public static void AddWide(TableLayoutPanel grid, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (control is not Label)
        {
            control.Anchor = AnchorStyles.Left;
        }
        grid.Controls.Add(control, 1, row);
    }

    /// <summary>Creates a wrapping helper label (gray by default).</summary>
    public static Label Helper(string text, Color? color = null, Padding? margin = null) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Theme.TextMuted, Tag = WrapTag, UseMnemonic = false,
        Margin = margin ?? new Padding(3, 1, 3, 5),
    };

    /// <summary>Creates a left-to-right button row.</summary>
    public static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Margin = new Padding(0, 0, 0, 2),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>Creates a button with a sensible minimum size.</summary>
    public static Button MakeButton(string text, int minWidth = 110) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(minWidth, 30), Padding = new Padding(10, 0, 10, 0),
    };

    /// <summary>Limits every wrap-tagged label on the page to the visible width.</summary>
    public static void UpdateWrapping(Control page)
    {
        if (Equals(page.Tag, HostTag))
        {
            foreach (Control part in page.Controls)
            {
                UpdateWrapping(part);
            }
            return;
        }
        if (!page.Visible || page.ClientSize.Width <= 0)
        {
            return;
        }
        var right = page.ClientSize.Width - page.Padding.Right - (page is ScrollableControl { VerticalScroll.Visible: true } ? 0 : SystemInformation.VerticalScrollBarWidth) - 8;
        foreach (var label in Descendants(page).OfType<Label>().Where(l => Equals(l.Tag, WrapTag)))
        {
            var left = page.PointToClient(label.Parent!.PointToScreen(label.Location)).X;
            var width = Math.Max(160, right - left);
            if (label.MaximumSize.Width != width)
            {
                label.MaximumSize = new Size(width, 0);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c))
            {
                yield return d;
            }
        }
    }
}
