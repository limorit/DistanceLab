using OpenCVDistanceLab.Core;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string folder = Path.Combine(Path.GetTempPath(), $"distance-lab-core-{Guid.NewGuid():N}");
Directory.CreateDirectory(folder);
try
{
    File.WriteAllBytes(Path.Combine(folder, "frame10.png"), [0]);
    File.WriteAllBytes(Path.Combine(folder, "frame2.png"), [0]);
    File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [0]);
    IReadOnlyList<string> images = ImageSequence.Find(folder);
    Require(images.Count == 2, "Image filter failed.");
    Require(Path.GetFileName(images[0]) == "frame2.png", "Natural sorting failed.");

    TrackMeasurement measurement = new(
        [new PointD(10, 20), new PointD(13, 24)],
        [.9, .8], [TrackStatus.LucasKanade, TrackStatus.FixedReference], [true, true]);
    Require(Math.Abs(measurement.DistancePixels - 5) < 1e-12, "Distance calculation failed.");
    string csvPath = Path.Combine(folder, "result.csv");
    await File.WriteAllLinesAsync(csvPath,
    [
        MeasurementCsv.Header,
        MeasurementCsv.CreateRow(0, "frame,2.png", measurement, 5, 2, "μm")
    ]);
    (List<PlotRecord> records, List<string> fields) = CsvPlotReader.Read(csvPath);
    Require(records.Count == 1 && fields.Contains("distance_actual"), "CSV round trip failed.");
    Require(Math.Abs(records[0].Values["distance_actual"] - 10) < 1e-12, "CSV value mismatch.");

    string settingsPath = Path.Combine(folder, "settings.json");
    ProductSettings expected = new() { Theme = "Dark", MarkerSize = 17.5, DefaultUnit = "mm" };
    expected.Save(settingsPath);
    ProductSettings actual = ProductSettings.Load(settingsPath);
    Require(actual.Theme == "Dark" && actual.MarkerSize == 17.5 && actual.DefaultUnit == "mm",
        "Settings round trip failed.");
}
finally
{
    Directory.Delete(folder, true);
}

Console.WriteLine("OpenCVDistanceLab.Core self-test: OK");
