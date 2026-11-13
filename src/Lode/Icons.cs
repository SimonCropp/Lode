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
    public const char Open = '\uE8E5';
    public const char OpenInNewTab = '\uE8A7';
    public const char Edit = '\uE70F';
    // A window with a shield, as Windows marks what runs elevated.
    public const char AdminTerminal = '\uE7EF';
    public const char Shortcut = '\uE71B';
    public const char CopyAsPath = '\uE62F';
    public const char Folder = '\uE8B7';
    public const char Git = '\uE895';
    public const char Pull = '\uE896';
    public const char Push = '\uE898';
    public const char Discard = '\uE7A7';
    public const char Package = '\uE7B8';
    public const char Build = '\uE9F5';
    public const char More = '\uE712';
    public const char NewTab = '\uE710';
    public const char NewWindow = '\uE737';
    public const char Reopen = '\uE7A7';
    public const char CommandPalette = '\uE765';
    public const char ShowHidden = '\uE890';
    public const char Pin = '\uE718';
    public const char Unpin = '\uE77A';
    public const char Options = '\uE713';
    public const char About = '\uE946';
    public const char Exit = '\uE7E8';
    // A list, for Show more options: the full Windows menu.
    public const char MoreOptions = '\uE8FD';

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
        DrawGlyph(graphics, glyph, new(0, 0, size, size), color);
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
