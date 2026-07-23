using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using OpenCVDistanceLab.Core;

namespace OpenCVDistanceLab.App;

public sealed class PlotControl : Control
{
    private IReadOnlyList<PlotRecord> _records = [];
    private string _xField = "frame_index";
    private string _yField = "distance_actual";

    public void SetData(IReadOnlyList<PlotRecord> records, string xField, string yField)
    {
        _records = records;
        _xField = xField;
        _yField = yField;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        IBrush panel = new SolidColorBrush(Color.Parse(dark ? "#24262A" : "#F3F4F6"));
        IBrush border = new SolidColorBrush(Color.Parse(dark ? "#484B52" : "#D1D5DB"));
        IBrush muted = new SolidColorBrush(Color.Parse(dark ? "#B7BAC1" : "#667085"));
        IBrush accent = new SolidColorBrush(Color.Parse("#1888E8"));
        Rect bounds = new(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(panel, new Pen(border, 1), bounds, 9, 9);
        if (Bounds.Width < 160 || Bounds.Height < 120) return;

        const double left = 76, right = 15, top = 16, bottom = 42;
        Rect plot = new(left, top, Math.Max(10, Bounds.Width - left - right), Math.Max(10, Bounds.Height - top - bottom));
        Pen axis = new(muted, 1);
        context.DrawLine(axis, plot.BottomLeft, plot.TopLeft);
        context.DrawLine(axis, plot.BottomLeft, plot.BottomRight);

        List<Point> pairs = [];
        foreach (PlotRecord record in _records)
            if (record.Values.TryGetValue(_xField, out double x) &&
                record.Values.TryGetValue(_yField, out double y) &&
                double.IsFinite(x) && double.IsFinite(y))
                pairs.Add(new Point(x, y));

        DrawText(context, _xField, new Point(plot.Center.X, Bounds.Height - 16), muted, 10, TextAlignment.Center);
        if (pairs.Count == 0)
        {
            DrawText(context, "等待有效数据…", plot.Center, muted, 12, TextAlignment.Center);
            return;
        }

        int maximum = Math.Max(100, (int)(plot.Width * 2));
        if (pairs.Count > maximum)
        {
            int step = (int)Math.Ceiling(pairs.Count / (double)maximum);
            List<Point> reduced = pairs.Where((_, index) => index % step == 0).ToList();
            if (reduced[^1] != pairs[^1]) reduced.Add(pairs[^1]);
            pairs = reduced;
        }

        double minX = pairs.Min(point => point.X), maxX = pairs.Max(point => point.X);
        double minY = pairs.Min(point => point.Y), maxY = pairs.Max(point => point.Y);
        ExpandRange(ref minX, ref maxX);
        ExpandRange(ref minY, ref maxY, .05);
        Pen grid = new(border, 1);
        for (int tick = 0; tick < 5; tick++)
        {
            double fraction = tick / 4d;
            double px = plot.Left + fraction * plot.Width;
            double py = plot.Bottom - fraction * plot.Height;
            if (tick is > 0 and < 4) context.DrawLine(grid, new Point(plot.Left, py), new Point(plot.Right, py));
            DrawText(context, Format(minX + fraction * (maxX - minX)), new Point(px, plot.Bottom + 13), muted, 9, TextAlignment.Center);
            DrawText(context, Format(minY + fraction * (maxY - minY)), new Point(plot.Left - 7, py), muted, 9, TextAlignment.Right);
        }

        Point Transform(Point point) => new(
            plot.Left + (point.X - minX) / (maxX - minX) * plot.Width,
            plot.Bottom - (point.Y - minY) / (maxY - minY) * plot.Height);
        Pen curve = new(accent, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point previous = Transform(pairs[0]);
        if (pairs.Count == 1) context.DrawEllipse(accent, null, previous, 2.5, 2.5);
        foreach (Point point in pairs.Skip(1))
        {
            Point current = Transform(point);
            context.DrawLine(curve, previous, current);
            previous = current;
        }
    }

    private static void ExpandRange(ref double minimum, ref double maximum, double fraction = .01)
    {
        if (maximum <= minimum)
        {
            double padding = Math.Max(Math.Abs(minimum) * .01, .5);
            minimum -= padding;
            maximum += padding;
            return;
        }
        double extra = (maximum - minimum) * fraction;
        minimum -= extra;
        maximum += extra;
    }

    private static void DrawText(
        DrawingContext context, string text, Point anchor, IBrush brush,
        double size, TextAlignment alignment)
    {
        TextLayout layout = new(text, new Typeface("Inter"), size, brush, alignment);
        Point origin = alignment switch
        {
            TextAlignment.Center => new Point(anchor.X - layout.Width / 2, anchor.Y - layout.Height / 2),
            TextAlignment.Right => new Point(anchor.X - layout.Width, anchor.Y - layout.Height / 2),
            _ => anchor
        };
        layout.Draw(context, origin);
    }

    private static string Format(double value) =>
        value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
}
