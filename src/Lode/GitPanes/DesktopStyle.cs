/// <summary>
/// The colours and sizes GitHub Desktop draws its Changes and History tabs in, which the Changes
/// and History panes are drawn after: from its stylesheets, app/styles/_variables.scss for light
/// and themes/_dark.scss for dark. In dark, the panes' backgrounds and lines are Lode's own
/// greys, so they sit with the list beside them, and the rest Desktop's. With high contrast on,
/// the system's own colours.
/// </summary>
static class DesktopStyle
{
    // Pixels at 100% scale.
    public const int Inset = 10;
    public const int Radius = 6;
    public const int BoxHeight = 25;
    public const int CommitRowHeight = 50;

    /// <summary>
    /// A row of a list of files: 29 pixels at Desktop's 12 pixel text, taller for larger text.
    /// </summary>
    public static int RowHeight(Font font) =>
        font.Height + 14;

    public static Color Text => Pick(Rgb(0x24292E), Rgb(0xF6F8FA), SystemColors.WindowText);

    public static Color SecondaryText => Pick(Rgb(0x6A737D), Rgb(0x959DA5), SystemColors.WindowText);

    public static Color Background => ExplorerStyle.Window;

    /// <summary>
    /// Behind the header above the files, the commit message, and the bar above a commit's files.
    /// </summary>
    public static Color AltBackground => Pick(Rgb(0xF6F8FA), Rgb(0x1F1F1F), SystemColors.Window);

    /// <summary>
    /// The lines between rows, and round the boxes Desktop draws.
    /// </summary>
    public static Color Border => Pick(Rgb(0xE1E4E8), Rgb(0x333333), SystemColors.WindowText);

    /// <summary>
    /// Round a text box: darker, for three to one contrast on the grey behind it.
    /// </summary>
    public static Color BoxBorder => Pick(Rgb(0x879099), Rgb(0x717B85), SystemColors.WindowText);

    /// <summary>
    /// Desktop's blue: the Commit button, the row picked in a list with the focus, the line under
    /// the tab chosen, and round a text box with the focus.
    /// </summary>
    public static Color Blue => Pick(Rgb(0x0366D6), Rgb(0x0366D6), SystemColors.Highlight);

    /// <summary>
    /// The Commit button under the pointer: a shade lighter.
    /// </summary>
    public static Color BlueHover => Pick(Rgb(0x0372EF), Rgb(0x0372EF), SystemColors.Highlight);

    /// <summary>
    /// Text on <see cref="Blue"/>.
    /// </summary>
    public static Color OnBlue => Pick(Color.White, Color.White, SystemColors.HighlightText);

    /// <summary>
    /// A tick box ticked.
    /// </summary>
    public static Color Tick => Pick(Rgb(0x2188FF), Rgb(0x2188FF), SystemColors.Highlight);

    public static Color Hover => Pick(Rgb(0xF6F8FA), Rgb(0x2A2A2A), SystemColors.Window);

    /// <summary>
    /// The row picked in a list without the focus.
    /// </summary>
    public static Color SelectedUnfocused => Pick(Rgb(0xEBEEF1), Rgb(0x383838), SystemColors.Highlight);

    /// <summary>
    /// Behind a tag, and the count on Changes' heading.
    /// </summary>
    public static Color Pill => Pick(Rgb(0xE1E4E8), Rgb(0x3A3A3A), SystemColors.Window);

    public static Color PillText => Pick(Rgb(0x2F363D), Rgb(0xF0F0F0), SystemColors.WindowText);

    /// <summary>
    /// Behind a tag on the row picked in a list without the focus.
    /// </summary>
    public static Color SelectedPill => Pick(Rgb(0xD1D5DA), Rgb(0x4A4A4A), SystemColors.Window);

    /// <summary>
    /// The colour of a file's status icon: green for one added or new to git, amber for one
    /// modified, red for one deleted, blue for one renamed, orange for one left to resolve.
    /// </summary>
    public static Color StatusColor(GitChange change) =>
        change switch
        {
            GitChange.Added or GitChange.Untracked => Pick(Rgb(0x22863A), Rgb(0x28A745), SystemColors.WindowText),
            GitChange.Deleted => Pick(Rgb(0xCB2431), Rgb(0xD73A49), SystemColors.WindowText),
            GitChange.Renamed => Pick(Rgb(0x0366D6), Rgb(0x2188FF), SystemColors.WindowText),
            GitChange.Conflicted => Pick(Rgb(0xC24E00), Rgb(0xF66A0A), SystemColors.WindowText),
            _ => Pick(Rgb(0xAA8507), Rgb(0xDBAB09), SystemColors.WindowText)
        };

    /// <summary>
    /// <paramref name="font"/> in its semibold face, as Desktop sets a commit's summary: Segoe UI
    /// Semibold for Segoe UI. Bold where the family has no semibold face.
    /// </summary>
    public static Font Semibold(Font font, float size)
    {
        var semibold = new Font($"{font.FontFamily.Name} Semibold", size, FontStyle.Regular, font.Unit);
        if (semibold.FontFamily.Name.EndsWith("Semibold", StringComparison.OrdinalIgnoreCase))
        {
            return semibold;
        }

        semibold.Dispose();
        return new(font.FontFamily, size, FontStyle.Bold, font.Unit);
    }

    /// <summary>
    /// A rounded pill of <paramref name="fill"/> with <paramref name="text"/> centred in it.
    /// </summary>
    public static void DrawPill(Graphics graphics, Rectangle bounds, int radius, Color fill, string text, Font font, Color ink)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shape = ExplorerStyle.RoundedRectangle(bounds, radius))
        using (var brush = new SolidBrush(fill))
        {
            graphics.FillPath(brush, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(graphics, text, font, bounds, ink, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    internal static Color Rgb(int rgb) =>
        Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);

    internal static Color Pick(Color light, Color dark, Color system)
    {
        if (ExplorerStyle.HighContrast())
        {
            return system;
        }

        if (ExplorerStyle.IsDark)
        {
            return dark;
        }

        return light;
    }
}

/// <summary>
/// The icons Desktop marks a changed file with, the octicons diff-added, diff-modified,
/// diff-removed, diff-renamed and alert: a rounded square with a plus, a dot, a minus or an arrow
/// in it, or a warning triangle for a conflict. Drawn on the octicons' 16 unit grid, scaled.
/// </summary>
static class StatusIcons
{
    public static void Draw(Graphics graphics, Rectangle bounds, GitChange change, Color color)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
        using var pen = new Pen(color, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);
        if (change == GitChange.Conflicted)
        {
            graphics.DrawPolygon(pen, new PointF(8, 1.6f), new PointF(14.9f, 14.25f), new PointF(1.1f, 14.25f));
            graphics.DrawLine(pen, 8, 5.75f, 8, 8.25f);
            graphics.FillEllipse(brush, 7, 10, 2, 2);
            graphics.Restore(state);
            return;
        }

        using (var square = new GraphicsPath())
        {
            square.AddRoundedRectangle(new(1.75f, 1.75f, 12.5f, 12.5f), new SizeF(2, 2));
            graphics.DrawPath(pen, square);
        }

        switch (change)
        {
            case GitChange.Added or GitChange.Untracked:
                graphics.DrawLine(pen, 8, 4.75f, 8, 11.25f);
                graphics.DrawLine(pen, 4.75f, 8, 11.25f, 8);
                break;
            case GitChange.Deleted:
                graphics.DrawLine(pen, 4.75f, 8, 11.25f, 8);
                break;
            case GitChange.Renamed:
                graphics.DrawLine(pen, 4.75f, 8, 10.75f, 8);
                graphics.DrawLines(pen, new PointF(8, 4.75f), new PointF(11.25f, 8), new PointF(8, 11.25f));
                break;
            default:
                graphics.FillEllipse(brush, 6, 6, 4, 4);
                break;
        }

        graphics.Restore(state);
    }
}
