using System.Globalization;
using System.Text;

namespace OpenCVDistanceLab.Core;

public static class MeasurementCsv
{
    public static string Header => string.Join(',', DataFields.Csv);

    public static string CreateRow(
        int frame,
        string filename,
        TrackMeasurement measurement,
        double initialDistance,
        double unitPerPixel,
        string unit)
    {
        double distance = measurement.DistancePixels;
        double actual = distance * unitPerPixel;
        double initialActual = initialDistance * unitPerPixel;
        double change = actual - initialActual;
        double strain = initialDistance > 0
            ? (distance - initialDistance) / initialDistance * 100
            : double.NaN;
        string[] values =
        [
            frame.ToString(CultureInfo.InvariantCulture),
            DataFields.Escape(filename),
            DataFields.Number(measurement.Points[0].X),
            DataFields.Number(measurement.Points[0].Y),
            DataFields.Number(measurement.Points[1].X),
            DataFields.Number(measurement.Points[1].Y),
            DataFields.Number(distance),
            DataFields.Number(actual, 9),
            DataFields.Escape(unit),
            DataFields.Number(change, 9),
            DataFields.Number(strain, 9),
            DataFields.Number(measurement.Qualities[0], 5),
            DataFields.Number(measurement.Qualities[1], 5),
            StatusName(measurement.Statuses[0]),
            StatusName(measurement.Statuses[1])
        ];
        return string.Join(',', values);
    }

    public static PlotRecord CreatePlotRecord(
        int frame, TrackMeasurement measurement, double initialDistance, double unitPerPixel)
    {
        double distance = measurement.DistancePixels;
        double actual = distance * unitPerPixel;
        PlotRecord record = new();
        record.Values["frame_index"] = frame;
        record.Values["point1_x_px"] = measurement.Points[0].X;
        record.Values["point1_y_px"] = measurement.Points[0].Y;
        record.Values["point2_x_px"] = measurement.Points[1].X;
        record.Values["point2_y_px"] = measurement.Points[1].Y;
        AddFinite(record, "distance_px", distance);
        AddFinite(record, "distance_actual", actual);
        AddFinite(record, "change_from_initial", actual - initialDistance * unitPerPixel);
        AddFinite(record, "engineering_strain_percent",
            initialDistance > 0 ? (distance - initialDistance) / initialDistance * 100 : double.NaN);
        AddFinite(record, "point1_quality", measurement.Qualities[0]);
        AddFinite(record, "point2_quality", measurement.Qualities[1]);
        return record;
    }

    public static string StatusName(TrackStatus status) => status switch
    {
        TrackStatus.LucasKanade => "lk",
        TrackStatus.LocalTemplate => "template",
        TrackStatus.FixedReference => "reference",
        TrackStatus.Keyframe => "keyframe",
        TrackStatus.Manual => "manual",
        TrackStatus.ImageMissing => "image_missing",
        _ => "lost"
    };

    private static void AddFinite(PlotRecord record, string key, double value)
    {
        if (double.IsFinite(value)) record.Values[key] = value;
    }
}

public static class CsvPlotReader
{
    public static (List<PlotRecord> Records, List<string> Fields) Read(string path)
    {
        using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) throw new InvalidDataException("CSV 没有表头。");
        List<string> headers = ParseLine(headerLine).Select(value => value.Trim()).ToList();
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct().Count() != headers.Count)
            throw new InvalidDataException("CSV 表头中存在空列名或重复列名。");

        List<List<string>> rows = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            List<string> row = ParseLine(line);
            while (row.Count < headers.Count) row.Add(string.Empty);
            if (row.Any(value => !string.IsNullOrWhiteSpace(value))) rows.Add(row);
        }
        if (rows.Count == 0) throw new InvalidDataException("CSV 中没有数据行。");

        List<int> numericIndices = [];
        for (int column = 0; column < headers.Count; column++)
        {
            List<string> values = rows.Select(row => row[column])
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (values.Count > 0 && values.All(value =>
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                numericIndices.Add(column);
        }
        if (numericIndices.Count == 0) throw new InvalidDataException("CSV 中没有可绘图的数值列。");

        List<PlotRecord> records = [];
        foreach (List<string> row in rows)
        {
            PlotRecord record = new();
            foreach (int index in numericIndices)
                if (double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    record.Values[headers[index]] = value;
            records.Add(record);
        }
        return (records, numericIndices.Select(index => headers[index]).ToList());
    }

    private static List<string> ParseLine(string line)
    {
        List<string> fields = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted && character == '"' && index + 1 < line.Length && line[index + 1] == '"')
            {
                current.Append('"');
                index++;
            }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
