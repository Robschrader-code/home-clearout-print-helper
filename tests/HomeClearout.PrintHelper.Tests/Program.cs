using HomeClearout.PrintHelper;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text.Json;
using ZXing;

internal static class Program
{
    private static int passed, failed;
    private static string artifacts = "";
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--decode-image") { using var image = new Bitmap(args[1]); Console.WriteLine(Decode(image)); return 0; }
        artifacts = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/test-results");
        Directory.CreateDirectory(artifacts);
        foreach (var size in LabelSize.Presets)
        {
            Test($"Dimensions and rendering {size.Name}", () =>
            {
                using var image = LabelRenderer.Render(LabelSpec.Sample(size));
                Equal(image.Width, 600); Equal(image.Height, size.HeightHundredths * 3); Equal(image.HorizontalResolution, 300f);
                image.Save(Path.Combine(artifacts, $"label-{size.HeightHundredths}.png"), ImageFormat.Png);
                var bounds = new RectangleF(0, 0, size.WidthHundredths, size.HeightHundredths); var layout = LabelRenderer.Layout(size);
                Assert(bounds.Contains(layout.Qr) && bounds.Contains(layout.Id) && bounds.Contains(layout.Description), "All content must fit");
                Assert(!layout.Qr.IntersectsWith(layout.Id) && !layout.Qr.IntersectsWith(layout.Description), "QR and text must not overlap");
            });
            Test($"Independent QR decode {size.Name}", () =>
            {
                using var image = LabelRenderer.Render(LabelSpec.Sample(size));
                Equal(Decode(image), "https://example.com/");
            });
            Test($"Text fit {size.Name}", () =>
            {
                using var b = new Bitmap(600, 600); using var g = Graphics.FromImage(b);
                var layout = LabelRenderer.Layout(size);
                Assert(LabelRenderer.FittingFontSize(g, "HC-00001", layout.Id, 11, 7, FontStyle.Bold) >= 7, "Readable ID font");
                Assert(LabelRenderer.FittingFontSize(g, "Test Inventory Item", layout.Description, 10, 7, FontStyle.Regular) >= 7, "Readable description font");
            });
            Test($"203 dpi thermal QR {size.Name}", () => { using var image = LabelRenderer.Render(LabelSpec.Sample(size), 203); Equal(Decode(image), "https://example.com/"); });
        }
        Test("Configured URL is encoded rather than an invented item link", () => { var spec = LabelSpec.Sample() with { Url = "https://example.org/test?value=42" }; using var image = LabelRenderer.Render(spec); Equal(Decode(image), spec.Url); });
        Test("Invalid and credential-bearing URLs rejected", () => { foreach (var url in new[] { "javascript:alert(1)", "file:///C:/secret", "not a URL", "https://user:password@example.org/", new string('x', 257) }) Throws(() => (LabelSpec.Sample() with { Url = url }).Validate()); });
        Test("Control characters and unreadable text rejected", () => { Throws(() => (LabelSpec.Sample() with { InventoryId = "HC\n1" }).Validate()); Throws(() => (LabelSpec.Sample() with { Description = "" }).Validate()); Throws(() => LabelRenderer.Render(LabelSpec.Sample(LabelSize.Presets[0]) with { Description = new string('W', 100) })); });
        Test("Invalid dimensions and DPI rejected", () => { Throws(() => (LabelSpec.Sample(new("Invalid", 250, 150))).Validate()); Throws(() => LabelRenderer.Render(LabelSpec.Sample(), 10)); });
        Test("Settings round trip in user-selected isolated directory", () =>
        {
            var store = new SettingsStore(Path.Combine(artifacts, "settings-roundtrip"));
            var expected = new HelperSettings { PrinterName = "Test laser", LabelHeight = 200, TestUrl = "https://example.org/", PaperMode = PaperMode.LabelRoll }; store.Save(expected); Equal(store.Load(out var warning), expected); Assert(warning is null, "No read warning");
        });
        Test("Missing/corrupt/unknown-version settings are safe", () =>
        {
            var store = new SettingsStore(Path.Combine(artifacts, "settings-invalid")); if (File.Exists(store.FilePath)) File.Delete(store.FilePath); Equal(store.Load(out _), new HelperSettings());
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!); foreach (var json in new[] { "invalid", "null", "{\"SchemaVersion\":999}", "{\"LabelHeight\":123}", "{\"TestUrl\":null}" }) { File.WriteAllText(store.FilePath, json); Equal(store.Load(out var warning), new HelperSettings()); Assert(warning is not null, "Corruption warning"); Equal(File.ReadAllText(store.FilePath), json); }
        });
        Test("Unavailable printer fails before attempting a job", () => { Throws(() => WindowsPrinterService.ValidatePrinter("missing", ["Laser"])); Throws(() => WindowsPrinterService.ValidatePrinter("", [])); WindowsPrinterService.ValidatePrinter("laser", ["Laser"]); });
        Test("WinForms defaults and preview render", () =>
        {
            using var form = new MainForm(new FakePrinters(), new SettingsStore(Path.Combine(artifacts, "ui-settings")));
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-30000, -30000); form.Show(); Application.DoEvents(); form.PerformLayout();
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(artifacts, "application.png"));
            var controls = All(form).ToArray(); Assert(controls.Any(c => c.AccessibleName == "Print Test Label"), "Print action exists"); Assert(controls.OfType<PictureBox>().Any(p => p.Image is not null), "Rendered preview exists");
            Assert(controls.OfType<ComboBox>().All(c => c.Width > 100 && c.Height > 15), "Input controls have usable bounds"); form.Close();
        });
        Test("Unplugged preferred printer does not silently select another", () =>
        {
            var store = new SettingsStore(Path.Combine(artifacts, "missing-printer")); store.Save(new() { PrinterName = "Disconnected Brother" }); using var form = new MainForm(new FakePrinters(), store); var combo = All(form).OfType<ComboBox>().Single(c => c.AccessibleName == "Printer"); Equal(combo.SelectedItem as string, "Disconnected Brother"); Assert(All(form).Any(c => c.AccessibleName == "Status" && c.Text.Contains("unavailable")), "Clear warning");
        });
        if (args.Contains("--print-pdf"))
        {
            Test("Actual Windows driver Letter output", () =>
            {
                var service = new WindowsPrinterService(); Assert(service.InstalledPrinters().Contains("Microsoft Print to PDF"), "PDF driver installed");
                var file = Path.Combine(artifacts, "windows-letter-test.pdf"); if (File.Exists(file)) File.Delete(file);
                service.Print("Microsoft Print to PDF", LabelSpec.Sample(), PaperMode.Letter, file);
                Assert(SpinWait.SpinUntil(() => File.Exists(file) && new FileInfo(file).Length > 1000, TimeSpan.FromSeconds(20)), "Windows produced a PDF");
                using var document = service.CreateDocument("Microsoft Print to PDF", LabelSpec.Sample(), PaperMode.Letter);
                Equal(document.DefaultPageSettings.PaperSize.Width, 850); Equal(document.DefaultPageSettings.PaperSize.Height, 1100); Equal(document.PrinterSettings.Copies, (short)1);
            });
        }
        File.WriteAllText(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(new { passed, failed, environment = Environment.OSVersion.ToString(), framework = RuntimeInformation.FrameworkDescription }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{passed} passed; {failed} failed."); return failed == 0 ? 0 : 1;
    }
    private static IEnumerable<Control> All(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; } }
    private static string Decode(Bitmap image)
    {
        var rect = new Rectangle(0, 0, image.Width, image.Height); using var rgb = image.Clone(rect, PixelFormat.Format24bppRgb); var data = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] bytes = new byte[image.Width * image.Height * 3]; try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * image.Width * 3, image.Width * 3); } finally { rgb.UnlockBits(data); }
        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true } };
        return reader.Decode(new RGBLuminanceSource(bytes, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGR24))?.Text ?? throw new Exception("QR did not decode");
    }
    private static void Test(string name, Action run) { try { run(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Throws(Action run) { try { run(); } catch { return; } throw new Exception("Expected rejection"); }
    private sealed class FakePrinters : IPrinterService { public string DefaultPrinter => "Test laser"; public string[] InstalledPrinters() => ["Test laser"]; public void Print(string printer, LabelSpec label, PaperMode mode, string? outputFile = null) => throw new NotSupportedException("Tests never send physical jobs"); }
}
