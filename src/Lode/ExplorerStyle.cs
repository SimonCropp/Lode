/// <summary>
/// The colours Windows 11's File Explorer draws in, in light mode, measured from it, and the
/// rounded shapes it draws them in. With high contrast on, the system's own colours instead, as
/// Explorer then uses.
/// </summary>
static class ExplorerStyle
{
    /// <summary>
    /// The row the address bar and search box are in.
    /// </summary>
    public static Color AddressRow => Pick(Color.FromArgb(0xF8, 0xF8, 0xF8), SystemColors.Control);

    /// <summary>
    /// The line along the foot of the command bar, between it and the panes.
    /// </summary>
    public static Color Divider => Pick(Color.FromArgb(0xD6, 0xD6, 0xD6), SystemColors.ControlDark);

    /// <summary>
    /// The line between the folder tree and the folder: barely there, as the two panes are the
    /// same white.
    /// </summary>
    public static Color PaneDivider => Pick(Color.FromArgb(0xF7, 0xF7, 0xF7), SystemColors.ControlDark);

    /// <summary>
    /// The selected tab's outline, the line the tabs sit on and the lines between tabs.
    /// </summary>
    public static Color TabBorder => Pick(Color.FromArgb(0xDA, 0xDA, 0xDA), SystemColors.ActiveBorder);

    /// <summary>
    /// Text that is there to be read second: every column of the folder but the name.
    /// </summary>
    public static Color SecondaryText => Pick(Color.FromArgb(0x6D, 0x6D, 0x6D), SystemColors.WindowText);

    /// <summary>
    /// A button's glyph while it does nothing: Back with nowhere to go back to.
    /// </summary>
    public static Color DisabledText => Pick(Color.FromArgb(0xA4, 0xA4, 0xA4), SystemColors.GrayText);

    /// <summary>
    /// Behind a menu's entries: off white, as Windows 11's menus are.
    /// </summary>
    public static Color MenuBackground => Pick(Color.FromArgb(0xF9, 0xF9, 0xF9), SystemColors.Menu);

    /// <summary>
    /// The line between groups of a menu's entries.
    /// </summary>
    public static Color MenuSeparator => Pick(Color.FromArgb(0xE5, 0xE5, 0xE5), SystemColors.ControlDark);

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

    static Color Pick(Color explorer, Color system)
    {
        if (SystemInformation.HighContrast)
        {
            return system;
        }

        return explorer;
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
