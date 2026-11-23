/// <summary>
/// The colours Windows 11's File Explorer draws in, in light mode and in dark, measured from it,
/// and the rounded shapes it draws them in. With high contrast on, the system's own colours
/// instead, as Explorer then uses.
/// </summary>
static class ExplorerStyle
{
    /// <summary>
    /// Has Windows Forms draw its controls, and Windows the title bars, scroll bars and dialogs,
    /// in <paramref name="theme"/>. Once, before the first window: changing it later tells every
    /// thread with a window and waits for each, which never ends if one is not reading its
    /// messages.
    /// </summary>
    public static void Use(Theme theme)
    {
        var mode = theme switch
        {
            Theme.Light => SystemColorMode.Classic,
            Theme.Dark => SystemColorMode.Dark,
            _ => SystemColorMode.System
        };
        Application.SetColorMode(mode);
        isDark = Application.IsDarkModeEnabled;
        UseDarkMenus(isDark);
    }

    // uxtheme's PreferredAppMode: Windows' own, and forced dark.
    internal const int DefaultMenus = 0;
    internal const int DarkMenus = 2;

    /// <summary>
    /// Has Windows draw its own popup menus, Show more options and the shell's submenus in it, in
    /// dark, as Explorer's are, or as it would. Windows Forms draws its own menus, so leaves
    /// these light: they go dark only for a process that asks. Dark is as <see cref="IsDark"/>
    /// has it, so they stay as the rest of Lode is when Windows changes. Gives what they were.
    /// </summary>
    internal static int UseDarkMenus(bool dark)
    {
        var mode = DefaultMenus;
        if (dark)
        {
            mode = DarkMenus;
        }

        var previous = SetPreferredAppMode(mode);
        FlushMenuThemes();
        return previous;
    }

    static bool isDark;

    /// <summary>
    /// True when windows are drawn dark: the Dark theme, or the System one with Windows' apps
    /// set to dark. Never with high contrast on, which has colours of its own. As it was when
    /// <see cref="Use"/> set the theme, as Windows Forms' own colours are: with the System
    /// theme, Windows Forms reads Windows' setting from the registry each time it is asked, so
    /// Windows changing from light to dark while Lode runs would have Lode's colours go dark
    /// while Windows Forms' stayed light, black text in dark menus. Asked of every colour in
    /// every paint, too.
    /// </summary>
    public static bool IsDark => isDark;

    /// <summary>
    /// The name of the theme Windows draws a list, tree or scroll bar in, as Explorer's are.
    /// </summary>
    public static string ControlTheme
    {
        get
        {
            if (IsDark)
            {
                return "DarkMode_Explorer";
            }

            return "Explorer";
        }
    }

    /// <summary>
    /// Behind the folder tree and the folder, and the selected tab. In dark, Explorer's near
    /// black, rather than the lighter grey Windows Forms gives a window in dark mode.
    /// </summary>
    public static Color Window => Pick(Color.White, Color.FromArgb(0x19, 0x19, 0x19), SystemColors.Window);

    /// <summary>
    /// Text on <see cref="SystemColors.Highlight"/>. In dark, white, as Windows Forms' dark mode
    /// has it black, which barely shows on the blue.
    /// </summary>
    public static Color HighlightText => Pick(SystemColors.HighlightText, Color.White, SystemColors.HighlightText);
    /// <summary>
    /// The row the address bar and search box are in.
    /// </summary>
    public static Color AddressRow => Pick(Color.FromArgb(0xF8, 0xF8, 0xF8), Color.FromArgb(0x1C, 0x1C, 0x1C), SystemColors.Control);

    /// <summary>
    /// The line along the foot of the command bar, between it and the panes, and along the top
    /// of the status bar, between the panes and it.
    /// </summary>
    public static Color Divider => Pick(Color.FromArgb(0xD6, 0xD6, 0xD6), Color.FromArgb(0x2B, 0x2B, 0x2B), SystemColors.ControlDark);

    /// <summary>
    /// The line between the folder tree and the folder: barely there, as the two panes are the
    /// same white.
    /// </summary>
    public static Color PaneDivider => Pick(Color.FromArgb(0xF7, 0xF7, 0xF7), Color.FromArgb(0x1D, 0x1D, 0x1D), SystemColors.ControlDark);

    /// <summary>
    /// The selected tab's outline, the line the tabs sit on and the lines between tabs.
    /// </summary>
    public static Color TabBorder => Pick(Color.FromArgb(0xDA, 0xDA, 0xDA), Color.FromArgb(0x3A, 0x3A, 0x3A), SystemColors.ActiveBorder);

    /// <summary>
    /// Text that is there to be read second: every column of the folder but the name.
    /// </summary>
    public static Color SecondaryText => Pick(Color.FromArgb(0x6D, 0x6D, 0x6D), Color.FromArgb(0xA6, 0xA6, 0xA6), SystemColors.WindowText);

    /// <summary>
    /// The Git column's word for what has happened to a file: amber for changes not yet staged,
    /// green for those staged and for new files, red for a deletion or a conflict. High contrast
    /// keeps its own text colour, the word saying it.
    /// </summary>
    public static Color ChangeColor(GitChange change) =>
        change switch
        {
            GitChange.Modified => Pick(Color.FromArgb(0x89, 0x55, 0x03), Color.FromArgb(0xE2, 0xC0, 0x8D), SystemColors.WindowText),
            GitChange.Untracked => Pick(Color.FromArgb(0x00, 0x71, 0x00), Color.FromArgb(0x73, 0xC9, 0x91), SystemColors.WindowText),
            GitChange.Deleted or GitChange.Conflicted => Pick(Color.FromArgb(0xAD, 0x07, 0x07), Color.FromArgb(0xE4, 0x67, 0x6B), SystemColors.WindowText),
            _ => Pick(Color.FromArgb(0x58, 0x7C, 0x0C), Color.FromArgb(0x81, 0xB8, 0x8B), SystemColors.WindowText)
        };

    /// <summary>
    /// A button's glyph while it does nothing: Back with nowhere to go back to.
    /// </summary>
    public static Color DisabledText => Pick(Color.FromArgb(0xA4, 0xA4, 0xA4), Color.FromArgb(0x6E, 0x6E, 0x6E), SystemColors.GrayText);

    /// <summary>
    /// A command bar icon while it does nothing: lighter still than disabled text.
    /// </summary>
    public static Color DisabledIcon => Pick(Color.FromArgb(0xC2, 0xC2, 0xC2), Color.FromArgb(0x5A, 0x5A, 0x5A), SystemColors.GrayText);

    /// <summary>
    /// Behind a menu's entries: off white, as Windows 11's menus are.
    /// </summary>
    public static Color MenuBackground => Pick(Color.FromArgb(0xF9, 0xF9, 0xF9), Color.FromArgb(0x2C, 0x2C, 0x2C), SystemColors.Menu);

    /// <summary>
    /// The line between groups of the command bar's buttons: faint.
    /// </summary>
    public static Color BarSeparator => Pick(Color.FromArgb(0xF0, 0xF0, 0xF0), Color.FromArgb(0x2B, 0x2B, 0x2B), SystemColors.ControlDark);

    /// <summary>
    /// The line between groups of a menu's entries.
    /// </summary>
    public static Color MenuSeparator => Pick(Color.FromArgb(0xE5, 0xE5, 0xE5), Color.FromArgb(0x40, 0x40, 0x40), SystemColors.ControlDark);

    /// <summary>
    /// The colour Windows 11 marks focus and the current choice in: the line under a focused text
    /// box, the bar beside the page shown. The user's accent, picked in Windows' settings, rather
    /// than the highlight colour, which stays blue whatever the accent. Read as each mark is
    /// drawn: Windows Forms repaints every control when the accent changes.
    /// </summary>
    public static Color Accent => Pick(UserAccent(), UserAccent(), SystemColors.Highlight);

    /// <summary>
    /// Where <see cref="Accent"/> comes from. Tests pin it, as every machine's accent is its own.
    /// </summary>
    internal static Func<Color> UserAccent { get; set; } = () => Application.SystemVisualSettings.AccentColor;

    /// <summary>
    /// Something drawn flat on <paramref name="background"/>, under the mouse: a shade darker.
    /// </summary>
    public static Color Hover(Color background) =>
        Blend(background, SystemColors.ControlText, 0.06f);

    /// <summary>
    /// Something drawn flat on <paramref name="background"/>, pressed: a shade lighter than
    /// hovered, as Windows 11 has it.
    /// </summary>
    public static Color Pressed(Color background) =>
        Blend(background, SystemColors.ControlText, 0.04f);

    /// <summary>
    /// Behind a menu's entry, or a button, under the mouse, or pressed: a shade of
    /// <paramref name="background"/>, or, with high contrast on, the system's highlight, as a
    /// shade would be lost on a high contrast background, black say. Text on it is in
    /// <see cref="HighlightedText"/>.
    /// </summary>
    public static Color Highlighted(Color background, bool pressed = false)
    {
        if (HighContrast())
        {
            return SystemColors.Highlight;
        }

        if (pressed)
        {
            return Pressed(background);
        }

        return Hover(background);
    }

    /// <summary>
    /// Text on <see cref="Highlighted"/>: <paramref name="color"/>, or, with high contrast on,
    /// the system's colour for text on its highlight.
    /// </summary>
    public static Color HighlightedText(Color color)
    {
        if (HighContrast())
        {
            return SystemColors.HighlightText;
        }

        return color;
    }

    /// <summary>
    /// Whether high contrast is on. Tests turn it on.
    /// </summary>
    internal static Func<bool> HighContrast { get; set; } = () => SystemInformation.HighContrast;

    static Color Pick(Color light, Color dark, Color system)
    {
        if (HighContrast())
        {
            return system;
        }

        if (IsDark)
        {
            return dark;
        }

        return light;
    }

    /// <summary>
    /// <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static Color Blend(Color from, Color to, float amount) =>
        Color.FromArgb(
            (int) (from.R + (to.R - from.R) * amount),
            (int) (from.G + (to.G - from.G) * amount),
            (int) (from.B + (to.B - from.B) * amount));

    /// <summary>
    /// The font code is shown in, in Find Results and in a diff: Cascadia Mono, as Windows
    /// Terminal has it, or Consolas where it isn't installed, at <paramref name="font"/>'s size.
    /// </summary>
    public static Font CodeFont(Font font)
    {
        var cascadia = new Font("Cascadia Mono", font.SizeInPoints);
        if (cascadia.Name == "Cascadia Mono")
        {
            return cascadia;
        }

        cascadia.Dispose();
        return new("Consolas", font.SizeInPoints);
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new(radius * 2, radius * 2));
        return path;
    }

    /// <summary>
    /// How round the corners of Lode's own buttons are, as Explorer's are: the toolbar's, a glyph
    /// button, Find in Files' toggles, and a tab's close button. Pixels at 100% scale.
    /// </summary>
    public const int ButtonRadius = 4;

    /// <summary>
    /// How far in from each edge Windows draws a push button. Its theme draws one from a single
    /// small image whatever the scale, so this is in pixels at every scale, not scaled.
    /// </summary>
    public const int PushButtonInset = 1;

    /// <summary>
    /// How round Windows draws a push button's corners: pixels at every scale, not scaled, as
    /// with <see cref="PushButtonInset"/>.
    /// </summary>
    public const int PushButtonRadius = 4;

    /// <summary>
    /// Fills a button's rounded shape over the whole of <paramref name="bounds"/>, each pixel of
    /// its straight edges whole, so they are as sharp as a push button's. Only its corners are
    /// smoothed.
    /// </summary>
    public static void FillButton(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        var state = SmoothButton(graphics);
        using var brush = new SolidBrush(color);
        using var shape = RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, shape);
        graphics.Restore(state);
    }

    /// <summary>
    /// A border a pixel wide round a button's shape, on the outermost pixels of
    /// <paramref name="bounds"/>, so its outside is the edge <see cref="FillButton"/> fills to.
    /// </summary>
    public static void DrawButtonBorder(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        var state = SmoothButton(graphics);
        using var pen = new Pen(color);
        using var shape = new GraphicsPath();

        // Down the middle of the outermost pixels, its corners half a pixel less round.
        var diameter = Math.Max(0, radius * 2 - 1);
        shape.AddRoundedRectangle(
            new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1),
            new SizeF(diameter, diameter));
        graphics.DrawPath(pen, shape);
        graphics.Restore(state);
    }

    // Smoothed, with each pixel's centre half a pixel in, so a shape on whole pixels covers them
    // exactly. With the default, its edges fall across the middle of the pixels, and blur.
    static GraphicsState SmoothButton(Graphics graphics)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        return state;
    }
}
