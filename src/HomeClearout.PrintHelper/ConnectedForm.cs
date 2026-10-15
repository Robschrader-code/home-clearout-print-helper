using System.Diagnostics;

namespace HomeClearout.PrintHelper;

public sealed class ConnectedForm : Form
{
    private readonly TextBox code = new() { Multiline = true, Dock = DockStyle.Fill, UseSystemPasswordChar = false, AccessibleName = "Connection code" };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new(8), AccessibleName = "Connected printing status" };
    private readonly Button run = new() { Text = "Enable processing", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConnectionStore connections;
    private readonly PrintJournal journal;
    private readonly HelperSettings settings;
    private readonly IPrinterService printers;
    private PrintConnection? connection;
    private QueueProcessor? processor;
    private bool enabled, busy;

    public ConnectedForm(IPrinterService printers, HelperSettings settings, string directory)
    {
        this.printers = printers; this.settings = settings;
        connections = new(directory); journal = new(Path.Combine(directory, "pending-print.json"));
        Text = "Home Clearout · Connected printing"; ClientSize = new(730, 520); MinimumSize = new(640, 480);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Segoe UI", 10); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.Absolute, 70)); layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Absolute, 40)); layout.RowStyles.Add(new(SizeType.Absolute, 95)); layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = $"Printer: {settings.PrinterName}\nPaper: {settings.PaperMode}. Set and test the printer in the main window first.", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        var open = new Button { Text = "Open app to connect / manage computers", AutoSize = true };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("https://home-clearout-buddy.lovable.app/printing") { UseShellExecute = true }); layout.Controls.Add(open, 0, 1);
        layout.Controls.Add(new Label { Text = "Paste the secret connection code from the app:", AutoSize = true }, 0, 2); layout.Controls.Add(code, 0, 3);
        var pair = new Button { Text = "Save connection on this Windows account", AutoSize = true };
        pair.Click += (_, _) =>
        {
            if (busy || enabled) { ShowStatus("Pause processing first."); return; }
            try
            {
                if (journal.Read() is not null) throw new InvalidOperationException("Resolve the outstanding job using the current connection before replacing it.");
                var parsed = PrintConnection.Parse(code.Text.Trim()); connections.Save(parsed); connection = parsed; code.Clear(); processor = null;
                ShowStatus("Connection saved with Windows protection. Designate this computer in the app, then enable processing.");
            }
            catch (Exception e) { ShowStatus(e.Message); }
        };
        var forget = new Button { Text = "Reset local connection", AutoSize = true };
        forget.Click += (_, _) =>
        {
            if (busy || enabled) { ShowStatus("Pause processing first."); return; }
            if (MessageBox.Show("First check the printer and resolve outstanding output on the app’s Labels page. Resetting does not cancel a Windows job or revoke this computer. Revoke the old connection in the app. Reset now?", "Reset local connection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                var path = Path.Combine(directory, "pending-print.json");
                if (File.Exists(path)) File.Move(path, Path.Combine(directory, "reconciled-" + Guid.NewGuid().ToString("N") + ".json"));
                connections.Forget(); connection = null; processor = null; code.Clear(); ShowStatus("Local connection reset. The previous journal was retained for review. Create a new connection code in the app.");
            }
            catch (Exception e) { ShowStatus(e.Message); }
        };
        var pairing = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        pairing.Controls.AddRange([pair, forget]); layout.Controls.Add(pairing, 0, 4);
        run.Click += async (_, _) =>
        {
            if (busy) { ShowStatus("Wait for the current operation to finish."); return; }
            enabled = !enabled;
            run.Text = enabled ? "Pause processing" : "Enable processing";
            if (enabled) await Poll(); else ShowStatus("Paused. No new labels will be claimed.");
        };
        layout.Controls.Add(run, 0, 5); layout.Controls.Add(status, 0, 6); Controls.Add(layout);
        try { connection = connections.Load(); ShowStatus(connection is null ? "Not connected. Create a connection in the web app." : "Connection loaded. Processing is paused until you enable it."); }
        catch (Exception) { ShowStatus("Saved connection could not be opened on this Windows account. Create a new connection code; reconcile any outstanding job first."); }
        timer.Tick += async (_, _) => { if (enabled && !busy) await Poll(); }; timer.Start();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; ShowStatus("Wait for the current operation before closing. Closing this window pauses processing."); } };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); http.Dispose(); };
    }
    private void ShowStatus(string text) => status.Text = text;
    private async Task Poll()
    {
        busy = true;
        try
        {
            if (connection is null) throw new InvalidOperationException("Connect this computer first.");
            WindowsPrinterService.ValidatePrinter(settings.PrinterName, printers.InstalledPrinters());
            if (settings.PrinterName.Contains("PDF", StringComparison.OrdinalIgnoreCase) || settings.PrinterName.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || settings.PrinterName.Contains("XPS", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a physical printer in the main window. Use manual test printing for file printers.");
            processor ??= new(new PrintQueueClient(connection, http), journal,
                job => { using var preview = LabelRenderer.Render(job.Label(connection)); return Task.CompletedTask; },
                job => PrintOnSta(job.Label(connection)), settings.PrinterName);
            ShowStatus(await processor.Tick());
        }
        catch (Exception e) { enabled = false; run.Text = "Enable processing"; ShowStatus(e.Message + " Processing paused."); }
        finally { busy = false; }
    }
    private Task PrintOnSta(LabelSpec spec)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { printers.Print(settings.PrinterName, spec, settings.PaperMode); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
