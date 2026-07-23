using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCVDistanceLab.Core;

public sealed class ProductSettings
{
    public int SchemaVersion { get; set; } = 4;
    public string Theme { get; set; } = "System";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DisplayMethod DisplayMethod { get; set; } = DisplayMethod.LinearContrast;

    public double ContrastClip { get; set; } = 1;
    public double Gamma { get; set; } = .75;
    public double DenoiseStrength { get; set; } = 5;
    public double RetinexSigma { get; set; } = 80;
    public double MarkerSize { get; set; } = 12;
    public double MarkerWidth { get; set; } = 1.2;
    public double MarkerOpacity { get; set; } = .55;
    public int WindowSize { get; set; } = 61;
    public int SearchRadius { get; set; } = 80;
    public double ReferenceThreshold { get; set; } = .48;
    public double ForwardBackwardThreshold { get; set; } = 1.5;
    public bool PauseOnLoss { get; set; } = true;
    public double DefaultScaleLength { get; set; } = 100;
    public string DefaultUnit { get; set; } = "μm";

    public static ProductSettings Load(string path) =>
        JsonSerializer.Deserialize<ProductSettings>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("设置文件为空或格式不正确。");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

public sealed class AppPreferences
{
    public string? LastWorkingDirectory { get; set; }
    public ProductSettings Settings { get; set; } = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCVDistanceLab", "preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, ProductSettings.JsonOptions));
    }
}
