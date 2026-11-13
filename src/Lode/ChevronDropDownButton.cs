/// <summary>
/// A toolbar drop-down button with a chevron after its text, as Explorer's New button has,
/// in place of the small arrow the toolbar renderer draws, which does not grow with the font.
/// The chevron is drawn in the right padding, which <see cref="ChevronWidth"/> leaves room for.
/// </summary>
sealed class ChevronDropDownButton : ToolStripDropDownButton
{
    public ChevronDropDownButton(string text) :
        base(text) =>
        ShowDropDownArrow = false;

    int Scale(int logical) =>
        Owner?.LogicalToDeviceUnits(logical) ?? logical;

    /// <summary>
    /// The room the chevron takes, gap included, to add to the right padding.
    /// </summary>
    public int ChevronWidth => Scale(16);

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var size = Scale(12);
        var color = Enabled ? ForeColor : SystemColors.GrayText;
        var chevron = Icons.Bitmap(Icons.ChevronDown, size, color);
        var right = Width - Padding.Right + ChevronWidth - Scale(2);
        eventArgs.Graphics.DrawImage(chevron, right - size, (Height - size) / 2, size, size);
    }
}
