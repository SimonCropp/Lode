/// <summary>
/// The list a parent's drop-down on the actions bar opens, which says where it was
/// right-clicked, over an action greyed out as much as any other, as <see cref="ActionsStrip"/>
/// does for the bar.
/// </summary>
sealed class ActionsDropDown : ToolStripDropDownMenu
{
    /// <summary>
    /// Right-clicked, at that point on the screen.
    /// </summary>
    public event Action<Point>? MenuRequested;

    protected override void WndProc(ref Message message)
    {
        if (ActionsStrip.MenuPoint(message, Handle) is { } point)
        {
            MenuRequested?.Invoke(point);
            return;
        }

        base.WndProc(ref message);
    }
}
