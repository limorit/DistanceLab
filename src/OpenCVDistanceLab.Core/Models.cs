using System.Globalization;

namespace OpenCVDistanceLab.Core;

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD left, PointD right) =>
        new(left.X + right.X, left.Y + right.Y);

    public static PointD operator -(PointD left, PointD right) =>
        new(left.X - right.X, left.Y - right.Y);

    public double DistanceTo(PointD other) =>
        Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

public enum DisplayMethod
{
    LinearContrast,
    Original,
    HistogramEqualization,
    Clahe,
    NlmClahe,
    Retinex,
    Gamma
}

public enum TrackStatus
{
    Lost = 0,
    LucasKanade = 1,
    LocalTemplate = 2,
    FixedReference = 3,
    Keyframe = 4,
    Manual = 5,
    ImageMissing = 6
}

public sealed record DisplayOption(DisplayMethod Method, string Name, string Description)
{
    public override string ToString() => Name;
}

public sealed record DisplaySettings(
    DisplayMethod Method,
    double ContrastClip,
    double Gamma,
    double DenoiseStrength,
    double RetinexSigma);

public sealed record TrackingSettings(
    int WindowSize,
    int SearchRadius,
    double ReferenceThreshold,
    double ForwardBackwardThreshold,
    bool PauseOnLoss)
{
    public TrackingSettings Normalize() => this with
    {
        WindowSize = Math.Max(15, WindowSize) | 1,
        SearchRadius = Math.Max(10, SearchRadius),
        ReferenceThreshold = Math.Clamp(ReferenceThreshold, .1, .99),
        ForwardBackwardThreshold = Math.Clamp(ForwardBackwardThreshold, .1, 20)
    };
}

public sealed record TrackMeasurement(
    PointD[] Points,
    double[] Qualities,
    TrackStatus[] Statuses,
    bool[] Valid)
{
    public double DistancePixels => Valid.All(value => value)
        ? Points[0].DistanceTo(Points[1])
        : double.NaN;
}

public sealed record PixelFrame(
    int Width,
    int Height,
    int PreviewStride,
    byte[] PreviewBgra,
    int TrackingStride,
    byte[] TrackingGray);

public sealed class PlotRecord
{
    public Dictionary<string, double> Values { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public static class DataFields
{
    public static readonly string[] Plot =
    [
        "frame_index", "point1_x_px", "point1_y_px", "point2_x_px",
        "point2_y_px", "distance_px", "distance_actual",
        "change_from_initial", "engineering_strain_percent",
        "point1_quality", "point2_quality"
    ];

    public static readonly string[] Csv =
    [
        "frame_index", "filename", "point1_x_px", "point1_y_px",
        "point2_x_px", "point2_y_px", "distance_px", "distance_actual",
        "unit", "change_from_initial", "engineering_strain_percent",
        "point1_quality", "point2_quality", "point1_status", "point2_status"
    ];

    public static string Number(double value, int digits = 6) =>
        double.IsFinite(value)
            ? value.ToString($"F{digits}", CultureInfo.InvariantCulture)
            : string.Empty;

    public static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}
