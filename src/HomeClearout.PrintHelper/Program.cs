namespace HomeClearout.PrintHelper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--diagnostics")
        {
            try
            {
                var folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
                using var label = LabelRenderer.Render(LabelSpec.Sample()); label.Save(Path.Combine(folder, "portable-label.png"));
                // Assembly.Location is intentionally empty for a bundled core library.
#pragma warning disable IL3000
                bool runtimeFromBundle = string.IsNullOrEmpty(typeof(object).Assembly.Location);
#pragma warning restore IL3000
                File.WriteAllText(Path.Combine(folder, "portable-startup.json"), System.Text.Json.JsonSerializer.Serialize(new { version = "0.2.0", is64Bit = Environment.Is64BitProcess, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, runtimeFromBundle, printers = new WindowsPrinterService().InstalledPrinters().Length }));
                return 0;
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "portable-error.txt"), e.ToString()); return 1; }
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Home Clearout Print Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        using var single = new Mutex(true, "Local\\HomeClearoutPrintHelper", out bool first);
        if (!first) { MessageBox.Show("Home Clearout Print Helper is already running. Use its existing window."); return 0; }
        Application.Run(new MainForm(new WindowsPrinterService(), new SettingsStore()));
        return 0;
    }
}
