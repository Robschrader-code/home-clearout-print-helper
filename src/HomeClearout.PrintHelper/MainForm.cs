namespace HomeClearout.PrintHelper;

public sealed class MainForm : Form
{
    private readonly IPrinterService printers;
    private readonly SettingsStore store;
    private readonly ComboBox printer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Printer" };
    private readonly ComboBox size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Label size" };
    private readonly ComboBox paper = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Paper mode" };
    private readonly TextBox inventory = new() { Text = "HC-00001", MaxLength = 24, Dock = DockStyle.Fill, AccessibleName = "Test inventory ID" };
    private readonly TextBox description = new() { Text = "Test Inventory Item", MaxLength = 100, Dock = DockStyle.Fill, AccessibleName = "Test item description" };
    private readonly TextBox url = new() { MaxLength = 256, Dock = DockStyle.Fill, AccessibleName = "Test QR URL" };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, AccessibleName = "Status", Padding = new(12), BackColor = Color.FromArgb(235, 241, 236) };
    private readonly Label previewNote = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly PictureBox preview = new() { SizeMode = PictureBoxSizeMode.Normal, BackColor = Color.White, AccessibleName = "Inventory label preview" };
    private readonly Button print = new() { Text = "Print Test Label", AutoSize = true, Padding = new(14, 8, 14, 8), BackColor = Color.FromArgb(40, 90, 65), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, AccessibleName = "Print Test Label" };
    private readonly Button check = new() { Text = "I checked the physical output", AutoSize = true, Enabled = false };
    private bool busy, initialized;
    private readonly List<Control> editors = [];
    private string? previewError;

    public MainForm(IPrinterService printers, SettingsStore store)
    {
        this.printers = printers; this.store = store;
        Text = "Home Clearout Print Helper · v0.1";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(1000, 740); MinimumSize = new(840, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 247, 242);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new(SizeType.Absolute, 78)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 66)); root.RowStyles.Add(new(SizeType.Absolute, 84));
        root.Controls.Add(new Label { Text = "Home Clearout Print Helper\nGenerate a test label. Preview it. Print through Windows.", Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        body.ColumnStyles.Add(new(SizeType.Percent, 50)); body.ColumnStyles.Add(new(SizeType.Percent, 50)); root.Controls.Add(body, 0, 1);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new(0, 0, 24, 0), AutoScroll = true };
        int row = 0;
        void Field(string title, Control control)
        {
            fields.RowStyles.Add(new(SizeType.Absolute, 25)); fields.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row++);
            fields.RowStyles.Add(new(SizeType.Absolute, 39)); fields.Controls.Add(control, 0, row++);
        }
        Field("Printer", printer);
        var refresh = new Button { Text = "Refresh printers", AutoSize = true }; refresh.Click += (_, _) => RefreshPrinters(printer.SelectedItem as string);
        fields.RowStyles.Add(new(SizeType.Absolute, 36)); fields.Controls.Add(refresh, 0, row++);
        Field("Label size", size); Field("Paper mode", paper); Field("Test inventory ID", inventory); Field("Test item description", description); Field("Test QR URL", url);
        fields.RowCount = row; body.Controls.Add(fields, 0, 0);
        var visual = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(12), BackColor = Color.FromArgb(235, 232, 224) };
        visual.Controls.Add(preview); visual.Controls.Add(previewNote); body.Controls.Add(visual, 1, 0);
        visual.Resize += (_, _) => PositionPreview(visual);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var save = new Button { Text = "Save settings", AutoSize = true, Padding = new(6, 8, 6, 8) };
        save.Click += (_, _) => { if (SaveSettings()) SetStatus("Settings saved on this computer."); };
        check.Click += (_, _) => { SetStatus("You confirmed the physical output. You can print another test or close the helper."); check.Enabled = false; };
        commands.Controls.AddRange([print, save, check]); root.Controls.Add(commands, 0, 2); root.Controls.Add(status, 0, 3); Controls.Add(root);
        editors.AddRange([body, save]);
        size.Items.AddRange(LabelSize.Presets); paper.Items.AddRange(["Letter sheet · 8.5 × 11 inches", "Label roll · selected label size"]);
        var settings = store.Load(out var warning);
        size.SelectedItem = LabelSize.Presets.Single(s => s.HeightHundredths == settings.LabelHeight);
        paper.SelectedIndex = settings.PaperMode == PaperMode.Letter ? 0 : 1; url.Text = settings.TestUrl;
        RefreshPrinters(settings.PrinterName);
        if (warning is not null) SetStatus(warning, true);
        foreach (var text in new[] { inventory, description, url }) text.TextChanged += (_, _) => UpdatePreview();
        size.SelectedIndexChanged += (_, _) => UpdatePreview(); paper.SelectedIndexChanged += (_, _) => UpdatePreview();
        print.Click += async (_, _) => await PrintLabel();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; SetStatus("Wait for Windows to finish accepting the job before closing."); } else SaveSettings(); };
        initialized = true; UpdatePreview();
    }
    private LabelSpec Spec() => new((LabelSize)size.SelectedItem!, inventory.Text.Trim(), description.Text.Trim(), url.Text.Trim());
    private PaperMode Mode => paper.SelectedIndex == 0 ? PaperMode.Letter : PaperMode.LabelRoll;
    private void SetStatus(string message, bool error = false) { status.Text = message; status.ForeColor = error ? Color.Maroon : Color.FromArgb(25, 65, 45); }
    private void RefreshPrinters(string? preferred)
    {
        try
        {
            var names = printers.InstalledPrinters(); printer.Items.Clear(); printer.Items.AddRange(names);
            var selected = !string.IsNullOrWhiteSpace(preferred) ? preferred : names.FirstOrDefault(n => n == printers.DefaultPrinter) ?? names.FirstOrDefault();
            if (selected is not null && !names.Contains(selected)) printer.Items.Insert(0, selected);
            printer.SelectedItem = selected;
            if (names.Length == 0) SetStatus("No installed printers found. Install a Windows printer driver, then refresh.", true);
            else if (selected is not null && !names.Contains(selected)) SetStatus("Your saved printer is unavailable. Choose an installed printer or reconnect it and refresh.", true);
            else SetStatus("Ready. The sample URL is a placeholder, not a real inventory item. No cloud connection is used.");
        }
        catch (Exception e) { SetStatus("Could not read Windows printers: " + e.Message, true); }
    }
    private void UpdatePreview()
    {
        if (!initialized) return;
        check.Enabled = false;
        try
        {
            var image = LabelRenderer.Render(Spec(), DeviceDpi);
            var old = preview.Image;
            preview.Image = image; old?.Dispose(); preview.Size = image.Size;
            previewNote.Text = $"LABEL PREVIEW · {((LabelSize)size.SelectedItem!).Name}\n\n100% at Windows display DPI. Check paper dimensions with a ruler.\n\n{(Mode == PaperMode.Letter ? "Letter: actual-size label in the upper-left printable area.\nThe rest of the sheet stays blank." : "Roll: use matching custom paper size in the driver.")}\n\nAttach labels to packaging, never to merchandise.";
            if (previewError is not null) SetStatus("Preview is ready. Check your printer and paper mode before printing.");
            previewError = null; print.Enabled = !busy;
            PositionPreview((Panel)preview.Parent!);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { previewError = e.Message; preview.Image?.Dispose(); preview.Image = null; print.Enabled = false; SetStatus(e.Message, true); }
    }
    private void PositionPreview(Panel parent)
    {
        previewNote.MaximumSize = new(Math.Max(100, parent.ClientSize.Width - 24), 0);
        preview.Location = new(Math.Max(12, (parent.ClientSize.Width - preview.Width) / 2), Math.Max(previewNote.Bottom + 28, parent.ClientSize.Height / 2));
    }
    private bool SaveSettings()
    {
        try { Spec().Validate(); store.Save(new() { PrinterName = printer.SelectedItem as string ?? "", LabelHeight = Spec().Size.HeightHundredths, TestUrl = url.Text.Trim(), PaperMode = Mode }); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { SetStatus("Settings not saved: " + e.Message, true); return false; }
    }
    private async Task PrintLabel()
    {
        if (busy || previewError is not null) return;
        try
        {
            var selected = printer.SelectedItem as string ?? "";
            WindowsPrinterService.ValidatePrinter(selected, printers.InstalledPrinters());
            var label = Spec(); label.Validate();
            string? output = null;
            // A virtual PDF driver needs a user-chosen destination, not a physical printer model.
            if (selected.Equals("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase))
            {
                using var dialog = new SaveFileDialog { Filter = "PDF document|*.pdf", FileName = "Home-Clearout-test-label.pdf", OverwritePrompt = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("Printing cancelled. Your settings and label are still available."); return; }
                output = dialog.FileName;
            }
            if (!SaveSettings()) return;
            var mode = Mode;
            busy = true; print.Enabled = false; check.Enabled = false; UseWaitCursor = true; editors.ForEach(c => c.Enabled = false);
            SetStatus("Sending one test label to Windows. Please wait…");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() => { try { printers.Print(selected, label, mode, output); completed.SetResult(); } catch (Exception e) { completed.SetException(e); } }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA); worker.Start(); await completed.Task;
            SetStatus("Windows accepted the print job. Physical printing is NOT confirmed. Check the paper/PDF and scan the QR; check the Windows queue if nothing appears.");
            check.Enabled = true;
        }
        catch (Exception e) { SetStatus("Printing could not be confirmed: " + e.Message + " Check the Windows print queue before retrying to avoid duplicate output.", true); }
        finally { busy = false; UseWaitCursor = false; print.Enabled = previewError is null; editors.ForEach(c => c.Enabled = true); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}
