/// <summary>
/// The colours GitHub Desktop draws a diff in, from its stylesheets: app/styles/_variables.scss
/// for light and themes/_dark.scss for dark, Primer's greens, reds and blues worked out from
/// Sass's darken. In dark, the plain parts are Lode's own greys, as the panes' are. With high
/// contrast on, the system's own colours, the + and - saying what each line is.
/// </summary>
static class DiffStyle
{
    static Color Pick(Color light, Color dark, Color system) =>
        DesktopStyle.Pick(light, dark, system);

    static Color Rgb(int rgb) =>
        DesktopStyle.Rgb(rgb);

    public static Color Text => DesktopStyle.Text;

    public static Color Background => ExplorerStyle.Window;

    public static Color LineNumber => Pick(Rgb(0x444D56), Rgb(0x959DA5), SystemColors.WindowText);

    public static Color Border => DesktopStyle.Border;

    public static Color AddBackground => Pick(Rgb(0xE6FFED), Rgb(0x113A1B), SystemColors.Window);

    public static Color AddGutter => Pick(Rgb(0xCDFFD8), Rgb(0x0B2611), SystemColors.Window);

    public static Color AddInner => Pick(Rgb(0xACF2BD), Rgb(0x22863A), SystemColors.Highlight);

    public static Color AddHover => Pick(Rgb(0x85E89D), Rgb(0x144620), SystemColors.Highlight);

    public static Color DeleteBackground => Pick(Rgb(0xFFEEF0), Rgb(0x450C0F), SystemColors.Window);

    public static Color DeleteGutter => Pick(Rgb(0xFFDCE0), Rgb(0x30090A), SystemColors.Window);

    public static Color DeleteInner => Pick(Rgb(0xFDB8C0), Rgb(0xB31D28), SystemColors.Highlight);

    public static Color DeleteHover => Pick(Rgb(0xFDAEB7), Rgb(0x9E1C23), SystemColors.Highlight);

    /// <summary>
    /// A deleted line's text, which dark has a shade lighter red, as Desktop does.
    /// </summary>
    public static Color DeleteText => Pick(Rgb(0x24292E), Rgb(0xFFDCE0), SystemColors.WindowText);

    /// <summary>
    /// Text in the part of a line that changed: the highlight's text with high contrast on.
    /// </summary>
    public static Color InnerText(Color text) =>
        Pick(text, text, SystemColors.HighlightText);

    public static Color HunkBackground => Pick(Rgb(0xF1F8FF), Rgb(0x1F1F1F), SystemColors.Control);

    public static Color HunkGutter => Pick(Rgb(0xDBEDFF), Rgb(0x2A2A2A), SystemColors.Control);

    public static Color HunkText => Pick(Rgb(0x586069), Rgb(0x959DA5), SystemColors.ControlText);

    /// <summary>
    /// A hunk's arrow under the pointer.
    /// </summary>
    public static Color HunkHover => Pick(Rgb(0xC8E1FF), Rgb(0x3A3A3A), SystemColors.Highlight);

    /// <summary>
    /// The side of a row that has no line, beside an added or deleted one.
    /// </summary>
    public static Color Empty => Pick(Rgb(0xFAFBFC), Rgb(0x1D1D1D), SystemColors.Window);

    /// <summary>
    /// The numbers of a line picked to commit: Desktop's blue.
    /// </summary>
    public static Color Picked => Pick(Rgb(0x2188FF), Rgb(0x044289), SystemColors.Highlight);

    public static Color PickedText => Pick(Color.White, Rgb(0xF6F8FA), SystemColors.HighlightText);

    public static Color PickedHover => Pick(Rgb(0x79B8FF), Rgb(0x0366D6), SystemColors.Highlight);

    /// <summary>
    /// Rows clicked, to copy.
    /// </summary>
    public static Color Selected => Pick(Rgb(0x0366D6), Rgb(0x2188FF), SystemColors.Highlight);

    public static Color AddedCount => DesktopStyle.StatusColor(GitChange.Added);

    public static Color DeletedCount => DesktopStyle.StatusColor(GitChange.Deleted);
}
