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

    static Color Pick(Color light, Color dark, Color system)
    {
        if (SystemInformation.HighContrast)
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

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new(radius * 2, radius * 2));
        return path;
    }
}
