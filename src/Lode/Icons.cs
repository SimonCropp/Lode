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
    public const char New = '\uECC8';
    public const char Cut = '\uE8C6';
    public const char Copy = '\uE8C8';
    public const char Paste = '\uE77F';
    public const char Rename = '\uE8AC';
    public const char Delete = '\uE74D';
    public const char Terminal = '\uE756';
    // A wrench, as Explorer's Properties has.
    public const char Properties = '\uE90F';
    public const char ThisPC = '\uE7F4';
    public const char ChevronDown = '\uE70D';
    public const char Shield = '\uEA18';

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
        cache.GetOrAdd((glyph, size, color.ToArgb()), _ => Draw(glyph, size, color, null));

    /// <summary>
    /// The glyph with <paramref name="badge"/> small in its bottom right corner, cut out of the
    /// glyph beneath it. Cached: do not dispose it.
    /// </summary>
    public static Bitmap Bitmap(char glyph, int size, Color color, char badge) =>
        badgedCache.GetOrAdd((glyph, badge, size, color.ToArgb()), _ => Draw(glyph, size, color, badge));

    static readonly ConcurrentDictionary<(char, char, int, int), Bitmap> badgedCache = new();

    static Bitmap Draw(char glyph, int size, Color color, char? badge)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        // ClearType needs an opaque background to blend with; this one is transparent.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawGlyph(graphics, glyph, new(0, 0, size, size), color);
        if (badge is { } badgeGlyph)
        {
            var badgeSize = (int)Math.Round(size * 0.6);
            var bounds = new Rectangle(size - badgeSize, size - badgeSize, badgeSize, badgeSize);
            // A gap round the badge, so it reads apart from the glyph under it.
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                graphics.FillRectangle(clear, Rectangle.Inflate(bounds, 1, 1));
            }

            graphics.CompositingMode = CompositingMode.SourceOver;
            DrawGlyph(graphics, badgeGlyph, bounds, color);
        }

        return bitmap;
    }

    static void DrawGlyph(Graphics graphics, char glyph, Rectangle bounds, Color color)
    {
        // Icon font glyphs fill the em square, so a font as many pixels high as the bounds fits it.
        using var font = new Font(fontName.Value, bounds.Height, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        // The font's line is a little taller than its glyphs, and with LineLimit a line taller
        // than the bounds draws nothing at all.
        format.FormatFlags &= ~StringFormatFlags.LineLimit;
        graphics.DrawString(glyph.ToString(), font, brush, bounds, format);
    }
}
