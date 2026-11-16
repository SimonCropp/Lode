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
    public const char Undo = '\uE7A7';
    public const char Redo = '\uE7A6';
    public const char Cut = '\uE8C6';
    public const char Copy = '\uE8C8';
    public const char Paste = '\uE77F';
    public const char Rename = '\uE8AC';
    public const char Delete = '\uE74D';
    public const char Terminal = '\uE756';
    // A wrench, as Explorer's Properties has.
    public const char Properties = '\uE90F';
    public const char ThisPC = '\uE7F4';
    // A machine on the network, at the top of a share's path in the address bar.
    public const char Network = '\uE968';
    public const char ChevronDown = '\uE70D';
    public const char Open = '\uE8E5';
    public const char OpenInNewTab = '\uE8A7';
    public const char Edit = '\uE70F';
    public const char OpenWith = '\uE7AC';
    // A window with a shield, as Windows marks what runs elevated.
    public const char AdminTerminal = '\uE7EF';
    // Not a glyph of either font, which have no Claude: an eight spoked asterisk, which neither
    // has either, stands for Claude's symbol, drawn from its outline. See claudeOutline.
    public const char Claude = '\u2733';
    public const char Shortcut = '\uE71B';
    public const char CopyAsPath = '\uE62F';
    public const char Folder = '\uE8B7';
    public const char Git = '\uE895';
    public const char Pull = '\uE896';
    public const char Push = '\uE898';
    public const char Discard = '\uE7A7';
    // A broom.
    public const char Purge = '\uEA99';
    public const char Package = '\uE7B8';
    public const char Build = '\uE9F5';
    public const char More = '\uE712';
    public const char NewTab = '\uE710';
    public const char NewWindow = '\uE737';
    public const char Reopen = '\uE7A7';
    public const char CommandPalette = '\uE765';
    public const char ShowHidden = '\uE890';
    public const char ShowSystem = '\uEA18';
    public const char Pin = '\uE718';
    public const char Unpin = '\uE77A';
    public const char Options = '\uE713';
    public const char About = '\uE946';
    public const char Exit = '\uE7E8';
    // A list, for Show more options: the full Windows menu.
    public const char MoreOptions = '\uE8FD';
    // The pages of Options.
    public const char View = '\uE890';
    // A lightning bolt, for the actions the bar and menus offer.
    public const char Actions = '\uE945';
    public const char Code = '\uE943';
    public const char FileExplorer = '\uEC50';
    // Arrows round in a circle, as Windows Update has.
    public const char Updates = '\uE777';
    // A magnifier, as Explorer's search box has.
    public const char Search = '\uE721';
    // A menu entry's submenu, and a tick beside one that is on.
    public const char ChevronRight = '\uE76C';
    public const char CheckMark = '\uE73E';

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

    static readonly ConcurrentDictionary<(char, int, int, int), Bitmap> spacedCache = new();

    /// <summary>
    /// The glyph as <see cref="Bitmap(char, int, Color)"/> draws it, with <paramref name="gap"/>
    /// transparent pixels after it: for a button that shows text beside its icon, which a tool
    /// strip puts closer than Explorer does. Cached: do not dispose it.
    /// </summary>
    public static Bitmap Bitmap(char glyph, int size, Color color, int gap) =>
        spacedCache.GetOrAdd(
            (glyph, size, color.ToArgb(), gap),
            _ =>
            {
                var bitmap = new Bitmap(size + gap, size);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.DrawImageUnscaled(Bitmap(glyph, size, color), 0, 0);
                return bitmap;
            });

    static Bitmap Draw(char glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        if (glyph == Claude)
        {
            DrawOutline(graphics, claudeOutline, size, color);
            return bitmap;
        }

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

    // Claude's symbol, the path of https://commons.wikimedia.org/wiki/File:Claude_AI_symbol.svg,
    // in a square 100 across.
    const string claudeOutline =
        "m19.6 66.5 19.7-11 .3-1-.3-.5h-1l-3.3-.2-11.2-.3L14 53l-9.5-.5-2.4-.5L0 49l.2-1.5 2-1.3 2.9.2 6.3.5 9.5.6 6.9.4L38 49.1h1.6l.2-.7-.5-.4-.4-.4L29 41l-10.6-7-5.6-4.1-3-2-1.5-2-.6-4.2 2.7-3 3.7.3.9.2 3.7 2.9 8 6.1L37 36l1.5 1.2.6-.4.1-.3-.7-1.1L33 25l-6-10.4-2.7-4.3-.7-2.6c-.3-1-.4-2-.4-3l3-4.2L28 0l4.2.6L33.8 2l2.6 6 4.1 9.3L47 29.9l2 3.8 1 3.4.3 1h.7v-.5l.5-7.2 1-8.7 1-11.2.3-3.2 1.6-3.8 3-2L61 2.6l2 2.9-.3 1.8-1.1 7.7L59 27.1l-1.5 8.2h.9l1-1.1 4.1-5.4 6.9-8.6 3-3.5L77 13l2.3-1.8h4.3l3.1 4.7-1.4 4.9-4.4 5.6-3.7 4.7-5.3 7.1-3.2 5.7.3.4h.7l12-2.6 6.4-1.1 7.6-1.3 3.5 1.6.4 1.6-1.4 3.4-8.2 2-9.6 2-14.3 3.3-.2.1.2.3 6.4.6 2.8.2h6.8l12.6 1 3.3 2 1.9 2.7-.3 2-5.1 2.6-6.8-1.6-16-3.8-5.4-1.3h-.8v.4l4.6 4.5 8.3 7.5L89 80.1l.5 2.4-1.3 2-1.4-.2-9.2-7-3.6-3-8-6.8h-.5v.7l1.8 2.7 9.8 14.7.5 4.5-.7 1.4-2.6 1-2.7-.6-5.8-8-6-9-4.7-8.2-.5.4-2.9 30.2-1.3 1.5-3 1.2-2.5-2-1.4-3 1.4-6.2 1.6-8 1.3-6.4 1.2-7.9.7-2.6v-.2H49L43 72l-9 12.3-7.2 7.6-1.7.7-3-1.5.3-2.8L24 86l10-12.8 6-7.9 4-4.6-.1-.5h-.3L17.2 77.4l-4.7.6-2-2 .2-3 1-1 8-5.5Z";

    // An SVG outline in a square 100 across, filled in the colour, inset as the font's glyphs
    // keep from their edges.
    static void DrawOutline(Graphics graphics, string outline, int size, Color color)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SvgPath.Parse(outline);
        var scale = size * outlineFill / 100;
        var inset = (size - 100 * scale) / 2;
        using var matrix = new Matrix(scale, 0, 0, scale, inset, inset);
        path.Transform(matrix);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    const float outlineFill = 0.875f;
}
