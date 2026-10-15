using System.Drawing.Printing;

namespace HomeClearout.PrintHelper;

public interface IPrinterService
{
    string[] InstalledPrinters();
    string DefaultPrinter { get; }
    void Print(string printer, LabelSpec label, PaperMode mode, string? outputFile = null);
}
public sealed class WindowsPrinterService : IPrinterService
{
    public string[] InstalledPrinters() => PrinterSettings.InstalledPrinters.Cast<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    public string DefaultPrinter => new PrinterSettings().PrinterName;
    public static void ValidatePrinter(string printer, IEnumerable<string> installed)
    {
        if (string.IsNullOrWhiteSpace(printer) || !installed.Contains(printer, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("That printer is not installed or is unavailable. Connect/install it, then refresh the printer list.");
    }
    public PrintDocument CreateDocument(string printer, LabelSpec label, PaperMode mode, string? outputFile = null)
    {
        label.Validate();
        ValidatePrinter(printer, InstalledPrinters());
        // Validate layout before any spooler interaction.
        using var preview = LabelRenderer.Render(label);
        var doc = new PrintDocument { DocumentName = $"Home Clearout test label {label.InventoryId}", PrintController = new StandardPrintController(), OriginAtMargins = false };
        try
        {
            doc.PrinterSettings.PrinterName = printer;
            if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException("Windows cannot use the selected printer. Check its driver and connection.");
            doc.PrinterSettings.Copies = 1;
            doc.DefaultPageSettings.Landscape = false;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            if (mode == PaperMode.Letter)
            {
                var letter = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.Letter);
                if (letter is null) throw new InvalidOperationException("This driver does not offer US Letter. Choose label-roll mode or another printer.");
                doc.DefaultPageSettings.PaperSize = letter;
            }
            else doc.DefaultPageSettings.PaperSize = new PaperSize("Home Clearout label", label.Size.WidthHundredths, label.Size.HeightHundredths);
            if (outputFile is not null) { doc.PrinterSettings.PrintToFile = true; doc.PrinterSettings.PrintFileName = Path.GetFullPath(outputFile); }
            doc.PrintPage += (_, e) =>
            {
                if (e.Graphics is null) throw new InvalidOperationException("The printer did not provide a drawing surface.");
                // Default printer Graphics origin is the printable area's upper left.
                // Letter: retain that origin. Roll: shift to physical paper origin;
                // fail rather than clip if the driver's margins cannot fit the label.
                if (mode == PaperMode.Letter)
                {
                    var printable = e.PageSettings.PrintableArea;
                    if (printable.Width + 1 < label.Size.WidthHundredths || printable.Height + 1 < label.Size.HeightHundredths)
                        throw new InvalidOperationException("The selected paper cannot fit this label at actual size.");
                }
                else
                {
                    var p = e.PageSettings.PrintableArea;
                    var l = LabelRenderer.Layout(label.Size);
                    var ink = RectangleF.Union(l.Qr, RectangleF.Union(l.Id, l.Description));
                    if (p.Left > ink.Left + 1 || p.Top > ink.Top + 1 || p.Right + 1 < ink.Right || p.Bottom + 1 < ink.Bottom)
                        throw new InvalidOperationException("The driver's printable area would clip this label. Check custom paper size/margins, or test on Letter paper.");
                    e.Graphics.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
                }
                LabelRenderer.Draw(e.Graphics, label);
                e.HasMorePages = false;
            };
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }
    public void Print(string printer, LabelSpec label, PaperMode mode, string? outputFile = null)
    {
        using var document = CreateDocument(printer, label, mode, outputFile);
        document.Print();
    }
}
