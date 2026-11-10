using System.Drawing.Drawing2D;

namespace DataverseToSql.UI;

/// <summary>Visual tokens (Fluent-style neutral palette) and helpers that style standard WinForms controls.</summary>
internal static class Theme
{
    // Surfaces
    public static readonly Color PageBack = Color.FromArgb(245, 245, 245);
    public static readonly Color CardBack = Color.White;
    public static readonly Color NavBack = Color.FromArgb(250, 250, 250);
    public static readonly Color Border = Color.FromArgb(224, 224, 224);
    public static readonly Color BorderStrong = Color.FromArgb(209, 209, 209);
    public static readonly Color HoverBack = Color.FromArgb(240, 240, 240);
    public static readonly Color NeutralPill = Color.FromArgb(235, 235, 235);

    // Ink (all >= 4.5:1 on white and on PageBack)
    public static readonly Color Text = Color.FromArgb(36, 36, 36);
    public static readonly Color TextMuted = Color.FromArgb(97, 97, 97);

    // Brand / accent
    public static readonly Color Accent = Color.FromArgb(15, 108, 189);
    public static readonly Color AccentHover = Color.FromArgb(17, 94, 163);
    public static readonly Color AccentPressed = Color.FromArgb(12, 59, 94);
    public static readonly Color AccentSoft = Color.FromArgb(235, 243, 252);
    public static readonly Color Disabled = Color.FromArgb(224, 224, 224);
    public static readonly Color DisabledText = Color.FromArgb(150, 150, 150);

    // Status (reserved: always shown with an icon and a word, never colour alone)
    public static readonly Color SuccessText = Color.FromArgb(14, 112, 14);
    public static readonly Color SuccessBack = Color.FromArgb(241, 250, 241);
    public static readonly Color WarningText = Color.FromArgb(131, 72, 0);
    public static readonly Color WarningBack = Color.FromArgb(255, 244, 206);
    public static readonly Color DangerText = Color.FromArgb(177, 14, 28);
    public static readonly Color DangerBack = Color.FromArgb(253, 231, 233);
    public static readonly Color InfoText = Accent;
    public static readonly Color InfoBack = AccentSoft;

    // Type
    public static readonly Font Base = new("Segoe UI", 9f);
    public static readonly Font BaseSemibold = new("Segoe UI Semibold", 9f);
    public static readonly Font Small = new("Segoe UI", 8.25f);
    public static readonly Font PageTitle = new("Segoe UI Semibold", 14f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 10.5f);
    public static readonly Font AppTitle = new("Segoe UI Semibold", 12.5f);
    public static readonly Font KpiValue = new("Segoe UI Semibold", 17f);
    public static readonly Font Mono = new("Consolas", 9f);

    private const string PrimaryTag = "primary";

    /// <summary>Tone of a status message: picks foreground, background and icon together.</summary>
    public enum Tone { Neutral, Muted, Info, Success, Warning, Danger }

    public static (Color Fore, Color Back, string Icon) Colors(Tone tone) => tone switch
    {
        Tone.Success => (SuccessText, SuccessBack, "✔"),
        Tone.Warning => (WarningText, WarningBack, "⚠"),
        Tone.Danger => (DangerText, DangerBack, "✖"),
        Tone.Info => (InfoText, InfoBack, "ℹ"),
        Tone.Muted => (TextMuted, Color.Empty, ""),
        _ => (Text, Color.Empty, ""),
    };

    /// <summary>Maps the text colours used across the pages to a tone (so a result label can render as a banner).</summary>
    public static Tone ToneOf(Color fore) =>
        fore == SuccessText ? Tone.Success
        : fore == WarningText ? Tone.Warning
        : fore == DangerText ? Tone.Danger
        : fore == InfoText ? Tone.Info
        : fore == TextMuted ? Tone.Muted
        : Tone.Neutral;

    /// <summary>Accent-filled call-to-action button.</summary>
    public static T Primary<T>(T button) where T : Button
    {
        button.Tag = PrimaryTag;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
        button.Font = BaseSemibold;
        button.Cursor = Cursors.Hand;
        void Sync()
        {
            button.BackColor = button.Enabled ? Accent : Disabled;
            button.ForeColor = button.Enabled ? Color.White : DisabledText;
        }
        button.EnabledChanged += (_, _) => Sync();
        Sync();
        return button;
    }

    /// <summary>Outlined neutral button.</summary>
    public static void Secondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderStrong;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = HoverBack;
        button.FlatAppearance.MouseDownBackColor = Border;
        button.BackColor = Color.White;
        button.ForeColor = Text;
        button.Cursor = Cursors.Hand;
    }

    /// <summary>Styles every button, text box, link and grid below <paramref name="root"/> that is not styled yet.</summary>
    public static void ApplyTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b when !Equals(b.Tag, PrimaryTag):
                    Secondary(b);
                    break;
                case TextBox t when t.BorderStyle == BorderStyle.Fixed3D:
                    t.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case LinkLabel l:
                    l.LinkColor = Accent;
                    l.ActiveLinkColor = AccentPressed;
                    l.LinkBehavior = LinkBehavior.HoverUnderline;
                    break;
            }
            ApplyTree(c);
        }
    }

    /// <summary>Flat, airy grid: no grid lines between columns, soft header, accent selection.</summary>
    public static void StyleGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = CardBack;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PageBack, ForeColor = TextMuted, Font = BaseSemibold, SelectionBackColor = PageBack,
            SelectionForeColor = TextMuted, Padding = new Padding(6, 0, 6, 0), Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = CardBack, ForeColor = Text, Font = Base, SelectionBackColor = AccentSoft, SelectionForeColor = Text,
            Padding = new Padding(6, 0, 6, 0),
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(252, 252, 252) };
        grid.RowTemplate.Height = 32;
    }

    /// <summary>Rounded-rectangle path.</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Draws a status pill (icon + text) centred vertically in <paramref name="bounds"/>; returns its width.</summary>
    public static int DrawPill(Graphics g, Rectangle bounds, string text, Tone tone, Font? font = null)
    {
        font ??= Small;
        var (fore, back, icon) = Colors(tone);
        var label = icon.Length > 0 ? $"{icon}  {text}" : text;
        var size = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPadding);
        var h = Math.Min(bounds.Height - 4, size.Height + 8);
        var rect = new Rectangle(bounds.X, bounds.Y + (bounds.Height - h) / 2, size.Width + 18, h);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(rect, h / 2f))
        using (var brush = new SolidBrush(back.IsEmpty ? NeutralPill : back))
        {
            g.FillPath(brush, path);
        }
        g.SmoothingMode = smoothing;
        TextRenderer.DrawText(g, label, font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return rect.Width;
    }
}
