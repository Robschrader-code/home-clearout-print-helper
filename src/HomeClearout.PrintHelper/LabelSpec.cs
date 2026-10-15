namespace HomeClearout.PrintHelper;

public enum PaperMode { Letter, LabelRoll }
public sealed record LabelSize(string Name, int WidthHundredths, int HeightHundredths)
{
    public static readonly LabelSize[] Presets = [new("2 × 1 inches", 200, 100), new("2 × 1.5 inches", 200, 150), new("2 × 2 inches", 200, 200)];
    public override string ToString() => Name;
}
public sealed record LabelSpec(LabelSize Size, string InventoryId, string Description, string Url)
{
    public static LabelSpec Sample(LabelSize? size = null) => new(size ?? LabelSize.Presets[1], "HC-00001", "Test Inventory Item", "https://example.com/");
    public void Validate()
    {
        if (!LabelSize.Presets.Any(s => s.WidthHundredths == Size.WidthHundredths && s.HeightHundredths == Size.HeightHundredths))
            throw new ArgumentException("Choose one of the three supported label sizes.");
        if (string.IsNullOrWhiteSpace(InventoryId) || InventoryId.Length > 24 || InventoryId.Any(char.IsControl))
            throw new ArgumentException("Use an inventory ID of 1–24 characters without line breaks.");
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 100 || Description.Any(char.IsControl))
            throw new ArgumentException("Use a description of 1–100 characters without line breaks.");
        if (Url.Length > 256 || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Enter a complete http:// or https:// test URL, up to 256 characters, without a password.");
    }
}
