using System.Drawing.Drawing2D;
using System.Drawing.Text;
using QRCoder;

namespace HomeClearout.PrintHelper;

public sealed record LabelLayout(RectangleF Qr, RectangleF Id, RectangleF Description);
public static class LabelRenderer
{
    // All geometry in hundredths of an inch, shared by screen, bitmap and driver.
    public static LabelLayout Layout(LabelSize size)
    {
        float qr = Math.Min(size.HeightHundredths - 12, 108), x = qr + 10, width = size.WidthHundredths - x - 6;
        return new(new(4, (size.HeightHundredths - qr) / 2f, qr, qr), new(x, 12, width, 33), new(x, 48, width, size.HeightHundredths - 54));
    }
    public static void Draw(Graphics g, LabelSpec spec)
    {
        spec.Validate();
        var box = Layout(spec.Size);
        g.FillRectangle(Brushes.White, 0, 0, spec.Size.WidthHundredths, spec.Size.HeightHundredths);
        using var data = QRCodeGenerator.GenerateQrCode(spec.Url, QRCodeGenerator.ECCLevel.M);
        // ModuleMatrix includes a four-module quiet zone; never crop or smooth it.
        var matrix = data.ModuleMatrix;
        float module = box.Qr.Width / matrix.Count;
        if (module < 1.3f) throw new ArgumentException("That URL makes the QR too dense for this label. Use a shorter URL or a taller label.");
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int y = 0; y < matrix.Count; y++)
            for (int x = 0; x < matrix.Count; x++)
                if (matrix[y][x]) g.FillRectangle(Brushes.Black, box.Qr.X + x * module, box.Qr.Y + y * module, module, module);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        DrawText(g, spec.InventoryId.Trim(), box.Id, 11, 7, FontStyle.Bold);
        DrawText(g, spec.Description.Trim(), box.Description, 10, 7, FontStyle.Regular);
    }
    public static float FittingFontSize(Graphics g, string text, RectangleF area, float preferredPoints, float minimumPoints, FontStyle style)
    {
        using var format = TextFormat();
        for (float size = preferredPoints; size >= minimumPoints; size -= .5f)
        {
            using var font = new Font("Segoe UI", size * 100f / 72f, style, GraphicsUnit.World);
            var measured = g.MeasureString(text, font, new SizeF(area.Width, 1000), format);
            if (measured.Height <= area.Height && measured.Width <= area.Width + .1f) return size;
        }
        throw new ArgumentException("The text will not fit legibly. Shorten the inventory ID/description or select a taller label.");
    }
    private static StringFormat TextFormat() => new(StringFormat.GenericDefault) { Trimming = StringTrimming.None, FormatFlags = StringFormatFlags.LineLimit };
    private static void DrawText(Graphics g, string text, RectangleF area, float preferred, float minimum, FontStyle style)
    {
        var size = FittingFontSize(g, text, area, preferred, minimum, style);
        using var font = new Font("Segoe UI", size * 100f / 72f, style, GraphicsUnit.World);
        using var format = TextFormat();
        g.DrawString(text, font, Brushes.Black, area, format);
    }
    public static Bitmap Render(LabelSpec spec, int dpi = 300)
    {
        if (dpi < 96 || dpi > 1200) throw new ArgumentOutOfRangeException(nameof(dpi));
        var image = new Bitmap(spec.Size.WidthHundredths * dpi / 100, spec.Size.HeightHundredths * dpi / 100);
        try
        {
            image.SetResolution(dpi, dpi);
            using var g = Graphics.FromImage(image);
            g.Clear(Color.White);
            g.ScaleTransform(dpi / 100f, dpi / 100f);
            Draw(g, spec);
            return image;
        }
        catch { image.Dispose(); throw; }
    }
}
