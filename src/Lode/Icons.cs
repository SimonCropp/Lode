using System.Drawing.Text;

/// <summary>
/// The toolbar and address bar icons: glyphs of Segoe Fluent Icons, the icon font Windows 11
/// and File Explorer use, so no images ship with Lode. Windows 10 has no Segoe Fluent Icons;
/// there the same code points come from its predecessor, Segoe MDL2 Assets. Each icon is drawn
/// to a bitmap once per size and colour.
/// </summary>
static class Icons
{
    // Code points shared by Segoe Fluent Icons and Segoe MDL2 Assets.
    public const char New = '';
    public const char Cut = '';
    public const char Copy = '';
    public const char Paste = '';
    public const char Rename = '';
    public const char Delete = '';
    public const char Terminal = '';
    // A wrench, as Explorer's Properties has.
    public const char Properties = '';
    public const char ThisPC = '';

    const string fluentIcons = "Segoe Fluent Icons";
    const string mdl2Assets = "Segoe MDL2 Assets";

    static readonly ConcurrentDictionary<(char, int, int), Bitmap> cache = new();
    static readonly Lazy<string> fontName = new(() =>
    {
        using var installed = new InstalledFontCollection();
        return FontName(installed.Families.Select(_ => _.Name));
    });

    /// <summary>
    /// Segoe Fluent Icons when it is among <paramref name="installed"/>, otherwise Segoe MDL2 Assets.
    /// </summary>
    internal static string FontName(IEnumerable<string> installed)
    {
        if (installed.Contains(fluentIcons, StringComparer.OrdinalIgnoreCase))
        {
            return fluentIcons;
        }

        return mdl2Assets;
    }

    /// <summary>
    /// The glyph as a square bitmap <paramref name="size"/> pixels across, in
    /// <paramref name="color"/>, on a transparent background. Cached: do not dispose it.
    /// </summary>
    public static Bitmap Bitmap(char glyph, int size, Color color) =>
        cache.GetOrAdd((glyph, size, color.ToArgb()), _ => Draw(glyph, size, color));

    static Bitmap Draw(char glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        // ClearType needs an opaque background to blend with; this one is transparent.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Icon font glyphs fill the em square, so a font as many pixels high as the bitmap fits it.
        using var font = new Font(fontName.Value, size, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        // The font's line is a little taller than its glyphs, and with LineLimit a line taller
        // than the bitmap draws nothing at all.
        format.FormatFlags &= ~StringFormatFlags.LineLimit;
        graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}
