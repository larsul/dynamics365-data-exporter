using System.Drawing.Drawing2D;

namespace DataverseToSql.UI;

/// <summary>White, bordered content card with a title; sizes to its content like a GroupBox.</summary>
internal sealed class Card : Panel
{
    private const int TitleTop = 9;

    public Card(string title)
    {
        Title = title;
        BackColor = Theme.CardBack;
        Padding = string.IsNullOrEmpty(title) ? new Padding(14, 10, 14, 10) : new Padding(14, 34, 14, 8);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 8);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = title;
    }

    /// <summary>Card heading.</summary>
    public string Title { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Border))
        {
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        if (!string.IsNullOrEmpty(Title))
        {
            TextRenderer.DrawText(e.Graphics, Title, Theme.CardTitle, new Point(Padding.Left - 2, TitleTop),
                Enabled ? Theme.Text : Theme.TextMuted, TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>Vertical step list for the wizard: number or check mark, title and a one-line status per step.</summary>
internal sealed class StepNav : Control
{
    public enum State { Upcoming, Current, Done, Blocked }

    public sealed record Item(string Title, string Subtitle, State State);

    private const int RowHeight = 62;
    private const int ListTop = 8;
    private IReadOnlyList<Item> _items = Array.Empty<Item>();
    private int _hover = -1;

    public StepNav()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.NavBack;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Steps";
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised with the step index when a step is clicked.</summary>
    public event Action<int>? StepClicked;

    public void SetItems(IReadOnlyList<Item> items)
    {
        _items = items;
        var current = items.Select((it, i) => (it, i)).FirstOrDefault(x => x.it.State == State.Current);
        AccessibleDescription = current.it is null ? null : $"Step {current.i + 1} of {items.Count}: {current.it.Title}";
        Invalidate();
    }

    private int IndexAt(Point p) => p.Y < ListTop ? -1 : (p.Y - ListTop) / RowHeight is var i && i < _items.Count ? i : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexAt(e.Location);
        if (i != _hover)
        {
            _hover = i;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (IndexAt(e.Location) is var i and >= 0)
        {
            StepClicked?.Invoke(i);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var row = new Rectangle(8, ListTop + i * RowHeight, Width - 17, RowHeight - 4);
            if (item.State == State.Current || i == _hover)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Theme.Rounded(row, 6);
                using var brush = new SolidBrush(item.State == State.Current ? Theme.AccentSoft : Theme.HoverBack);
                g.FillPath(brush, path);
            }

            // Connector line between circles.
            var cx = row.X + 22;
            var cy = row.Y + 20;
            if (i < _items.Count - 1)
            {
                using var pen = new Pen(item.State == State.Done ? Theme.Accent : Theme.BorderStrong, 2);
                g.DrawLine(pen, cx, cy + 14, cx, cy + RowHeight - 14);
            }

            // Circle: filled accent when done or current, outlined otherwise.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var circle = new Rectangle(cx - 13, cy - 13, 26, 26);
            var filled = item.State is State.Done or State.Current;
            using (var brush = new SolidBrush(filled ? Theme.Accent : Color.White))
            {
                g.FillEllipse(brush, circle);
            }
            if (!filled)
            {
                using var pen = new Pen(item.State == State.Blocked ? Theme.Border : Theme.BorderStrong, 1.5f);
                g.DrawEllipse(pen, circle);
            }
            var glyph = item.State == State.Done ? "✔" : (i + 1).ToString();
            TextRenderer.DrawText(g, glyph, Theme.BaseSemibold, circle, filled ? Color.White : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var textLeft = cx + 22;
            var titleColor = item.State == State.Blocked ? Theme.TextMuted : Theme.Text;
            TextRenderer.DrawText(g, item.Title, item.State == State.Current ? Theme.BaseSemibold : Theme.Base,
                new Rectangle(textLeft, row.Y + 8, row.Right - textLeft - 4, 20), titleColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, item.Subtitle, Theme.Small,
                new Rectangle(textLeft, row.Y + 28, row.Right - textLeft - 4, 18), Theme.TextMuted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>Small status pill (icon + word), e.g. in the header or above the mapping grid.</summary>
internal sealed class Pill : Control
{
    private Theme.Tone _tone;

    public Pill()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 4, 8, 4);
        Height = 26;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void Set(string text, Theme.Tone tone)
    {
        _tone = tone;
        Text = text;
        AccessibleName = text;
        using var g = CreateGraphics();
        var icon = Theme.Colors(tone).Icon;
        var label = icon.Length > 0 ? $"{icon}  {text}" : text;
        Width = TextRenderer.MeasureText(g, label, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + 20;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!string.IsNullOrEmpty(Text))
        {
            Theme.DrawPill(e.Graphics, ClientRectangle, Text, _tone);
        }
    }
}

/// <summary>A key figure: caption, big value, a muted sub-line and an optional usage meter (e.g. busy workers).</summary>
internal sealed class KpiTile : Control
{
    private string _value = "-";
    private string _sub = "";
    private int _meterActive;
    private int _meterTotal;

    public KpiTile(string caption)
    {
        Caption = caption;
        Size = new Size(176, 92);
        Margin = new Padding(0, 0, 10, 10);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Theme.CardBack;
        AccessibleRole = AccessibleRole.StaticText;
        UpdateAccessibleName();
    }

    public string Caption { get; }

    public void Set(string value, string sub = "")
    {
        if (value == _value && sub == _sub) return;
        _value = value;
        _sub = sub;
        UpdateAccessibleName();
        Invalidate();
    }

    /// <summary>Shows <paramref name="total"/> slots of which <paramref name="active"/> are filled; total 0 hides the meter.</summary>
    public void SetMeter(int active, int total)
    {
        if (active == _meterActive && total == _meterTotal) return;
        _meterActive = Math.Clamp(active, 0, Math.Max(0, total));
        _meterTotal = Math.Max(0, total);
        Invalidate();
    }

    private void UpdateAccessibleName() => AccessibleName = $"{Caption}: {_value} {_sub}".Trim();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.CardBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(box, 6))
        using (var brush = new SolidBrush(Theme.PageBack))
        {
            g.FillPath(brush, path);
        }
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, Caption, Theme.Small, new Rectangle(12, 10, Width - 20, 16), Theme.TextMuted, flags);
        TextRenderer.DrawText(g, _value, Theme.KpiValue, new Rectangle(10, 26, Width - 18, 34), Theme.Text, flags);
        if (_meterTotal > 0)
        {
            // One segment per worker; a 2px gap keeps neighbours distinct.
            var left = 12;
            var width = Width - 24;
            var gap = _meterTotal > 24 ? 1 : 2;
            var seg = Math.Max(2f, (width - gap * (_meterTotal - 1)) / (float)_meterTotal);
            for (var i = 0; i < _meterTotal; i++)
            {
                var r = new RectangleF(left + i * (seg + gap), 66, seg, 8);
                using var path = Theme.Rounded(r, 2);
                using var brush = new SolidBrush(i < _meterActive ? Theme.Accent : Theme.BorderStrong);
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(12, 74, Width - 20, 16), Theme.TextMuted, flags);
        }
        else
        {
            TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(12, 64, Width - 20, 18), Theme.TextMuted, flags);
        }
    }
}

/// <summary>Slim rounded progress bar: determinate fill, or a sliding segment when the total is unknown.</summary>
internal sealed class ProgressStrip : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private double _value;
    private bool _indeterminate;
    private float _phase;

    public ProgressStrip()
    {
        Height = 10;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Sync progress";
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.012f) % 1.4f;
            Invalidate();
        };
    }

    /// <summary>0..1 fills the bar; null shows the indeterminate animation (only while <paramref name="animate"/>).</summary>
    public void SetValue(double? fraction, bool animate)
    {
        _indeterminate = fraction is null && animate;
        _value = Math.Clamp(fraction ?? 0, 0, 1);
        AccessibleDescription = fraction is null ? null : $"{_value:P0}";
        if (_indeterminate) _timer.Start(); else _timer.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.CardBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, (Height - 8) / 2f, Width - 1, 8);
        using (var path = Theme.Rounded(track, 4))
        using (var brush = new SolidBrush(Theme.Border))
        {
            g.FillPath(brush, path);
        }
        RectangleF fill;
        if (_indeterminate)
        {
            var w = track.Width * 0.3f;
            var x = track.X + (track.Width + w) * _phase - w;
            fill = RectangleF.Intersect(new RectangleF(x, track.Y, w, track.Height), track);
        }
        else
        {
            fill = new RectangleF(track.X, track.Y, (float)(track.Width * _value), track.Height);
        }
        if (fill.Width >= 1)
        {
            using var path = Theme.Rounded(fill, 4);
            using var brush = new SolidBrush(Theme.Accent);
            g.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Single-series area chart of throughput over time (one hue, 2px line, recessive baseline, no legend: the card title
/// names the series). Hovering shows a crosshair with the value at that point.
/// </summary>
internal sealed class Sparkline : Control
{
    private const int MaxSamples = 300;
    private const int TopPad = 22, BottomPad = 18, SidePad = 6;
    private readonly List<double> _samples = new();
    private int _hover = -1;

    public Sparkline()
    {
        Height = 120;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "Throughput over time";
    }

    /// <summary>Unit shown after values, e.g. "rows/s".</summary>
    public string Unit { get; set; } = "";

    /// <summary>Seconds between samples (for the time axis text).</summary>
    public int SecondsPerSample { get; set; } = 1;

    public void Clear()
    {
        _samples.Clear();
        _hover = -1;
        Invalidate();
    }

    public void Add(double value)
    {
        _samples.Add(Math.Max(0, value));
        if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
        AccessibleDescription = $"{value:N0} {Unit}";
        Invalidate();
    }

    private RectangleF Plot => new(SidePad, TopPad, Width - 2 * SidePad, Height - TopPad - BottomPad);

    private PointF PointAt(int i, double max)
    {
        var plot = Plot;
        var x = plot.X + (_samples.Count <= 1 ? plot.Width : plot.Width * i / (float)(_samples.Count - 1));
        var y = plot.Bottom - (float)(max <= 0 ? 0 : plot.Height * _samples[i] / max);
        return new PointF(x, y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_samples.Count < 2) return;
        var plot = Plot;
        var i = (int)Math.Round((e.X - plot.X) / plot.Width * (_samples.Count - 1));
        i = Math.Clamp(i, 0, _samples.Count - 1);
        if (i != _hover)
        {
            _hover = i;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.CardBack);
        var plot = Plot;
        using var axis = new Pen(Theme.Border);
        g.DrawLine(axis, plot.X, plot.Bottom, plot.Right, plot.Bottom);

        if (_samples.Count < 2)
        {
            TextRenderer.DrawText(g, "The throughput chart fills in while a sync runs.", Theme.Small,
                Rectangle.Round(plot), Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var max = Math.Max(1, _samples.Max()) * 1.1;
        var points = Enumerable.Range(0, _samples.Count).Select(i => PointAt(i, max)).ToArray();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var area = new GraphicsPath())
        {
            area.AddLine(points[0].X, plot.Bottom, points[0].X, points[0].Y);
            area.AddLines(points);
            area.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(38, Theme.Accent));
            g.FillPath(fill, area);
        }
        using (var line = new Pen(Theme.Accent, 2f) { LineJoin = LineJoin.Round })
        {
            g.DrawLines(line, points);
        }

        // Peak reference (muted ink, not series colour) and time span.
        var peak = _samples.Max();
        TextRenderer.DrawText(g, $"peak {peak:N0} {Unit}", Theme.Small, new Point((int)plot.X, 2), Theme.TextMuted);
        var span = TimeSpan.FromSeconds((_samples.Count - 1) * SecondsPerSample);
        TextRenderer.DrawText(g, $"last {(span.TotalMinutes >= 1 ? $"{span.TotalMinutes:N0} min" : $"{span.TotalSeconds:N0} s")}",
            Theme.Small, new Rectangle((int)plot.X, (int)plot.Bottom + 2, (int)plot.Width, 16), Theme.TextMuted, TextFormatFlags.Left);
        TextRenderer.DrawText(g, "now", Theme.Small, new Rectangle((int)plot.X, (int)plot.Bottom + 2, (int)plot.Width, 16),
            Theme.TextMuted, TextFormatFlags.Right);

        var marker = _hover >= 0 && _hover < points.Length ? _hover : points.Length - 1;
        var p = points[marker];
        if (_hover >= 0)
        {
            using var cross = new Pen(Theme.BorderStrong) { DashStyle = DashStyle.Dot };
            g.DrawLine(cross, p.X, plot.Y, p.X, plot.Bottom);
            var ago = TimeSpan.FromSeconds((points.Length - 1 - marker) * SecondsPerSample);
            var tip = $"{_samples[marker]:N0} {Unit}" + (ago.TotalSeconds >= 1 ? $"  ·  {ago.TotalSeconds:N0} s ago" : "  ·  now");
            var size = TextRenderer.MeasureText(g, tip, Theme.Small);
            var tx = Math.Clamp(p.X + 8, plot.X, plot.Right - size.Width - 8);
            var box = new RectangleF(tx, 2, size.Width + 8, size.Height + 2);
            using (var path = Theme.Rounded(box, 4))
            using (var brush = new SolidBrush(Theme.Text))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, tip, Theme.Small, Rectangle.Round(box), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        // Marker with a 2px surface ring.
        using (var ring = new SolidBrush(Parent?.BackColor ?? Theme.CardBack))
        {
            g.FillEllipse(ring, p.X - 6, p.Y - 6, 12, 12);
        }
        using (var dot = new SolidBrush(Theme.Accent))
        {
            g.FillEllipse(dot, p.X - 4, p.Y - 4, 8, 8);
        }
    }
}
