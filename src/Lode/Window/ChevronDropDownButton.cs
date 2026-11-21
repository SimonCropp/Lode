/// <summary>
/// A toolbar drop-down button with a chevron after its text, as Explorer's New button has,
/// in place of the small arrow the toolbar renderer draws, which does not grow with the font.
/// The chevron is drawn in the right padding, which <see cref="ChevronWidth"/> leaves room for.
/// The status bar's points up, as what it opens opens above it.
/// </summary>
sealed class ChevronDropDownButton : ToolStripDropDownButton
{
    readonly char chevron;

    public ChevronDropDownButton(string text, char chevron = Icons.ChevronDown) :
        base(text)
    {
        this.chevron = chevron;
        ShowDropDownArrow = false;
    }

    int Scale(int logical) =>
        Owner?.LogicalToDeviceUnits(logical) ?? logical;

    /// <summary>
    /// The room the chevron takes, gap included, to add to the right padding.
    /// </summary>
    public int ChevronWidth => Scale(14);

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var size = Scale(8);
        var color = Enabled ? ForeColor : ExplorerStyle.DisabledText;
        var glyph = Icons.Bitmap(chevron, size, color);
        var right = Width - Padding.Right + ChevronWidth - Scale(2);
        args.Graphics.DrawImage(glyph, right - size, (Height - size) / 2, size, size);
    }
}
