using System.Text.Json;

namespace HomeClearout.PrintHelper;

public sealed record HelperSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string PrinterName { get; init; } = "";
    public int LabelHeight { get; init; } = 150;
    public string TestUrl { get; init; } = "https://example.com/";
    public PaperMode PaperMode { get; init; } = PaperMode.Letter;
}
public sealed class SettingsStore(string? directory = null)
{
    public string FilePath { get; } = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeClearout", "PrintHelper"), "settings.json");
    public HelperSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new();
            var settings = JsonSerializer.Deserialize<HelperSettings>(File.ReadAllText(FilePath)) ?? throw new JsonException();
            if (settings.SchemaVersion != 1 || !LabelSize.Presets.Any(s => s.HeightHundredths == settings.LabelHeight) || !Enum.IsDefined(settings.PaperMode) || settings.PrinterName is null || settings.TestUrl is null)
                throw new JsonException();
            var sample = LabelSpec.Sample() with { Url = settings.TestUrl };
            sample.Validate();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = "Saved settings could not be read. Defaults are shown; your old file is left unchanged until you save.";
            return new();
        }
    }
    public void Save(HelperSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, FilePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
