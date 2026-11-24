/// <summary>
/// The actions bar's strip, which says where it was right-clicked, over a greyed out button as
/// much as any other. A button greyed out has no mouse events, and over a button the strip has
/// none of its own, so the right-click is taken from the menu Windows asks for after it.
/// </summary>
sealed class ActionsStrip : ToolStrip
{
    /// <summary>
    /// Right-clicked, at that point on the screen.
    /// </summary>
    public event Action<Point>? MenuRequested;

    protected override void WndProc(ref Message message)
    {
        if (MenuPoint(message, Handle) is { } point)
        {
            MenuRequested?.Invoke(point);
            return;
        }

        base.WndProc(ref message);
    }

    /// <summary>
    /// Where on the screen <paramref name="window"/> was right-clicked, when
    /// <paramref name="message"/> is the menu Windows asks it for after a right-click. From the
    /// keyboard the menu has no point, and neither the strip nor a drop-down is ever focused to
    /// be asked.
    /// </summary>
    internal static Point? MenuPoint(Message message, IntPtr window)
    {
        if (message.Msg != WindowMessageContextMenu ||
            message.WParam != window ||
            unchecked((int)message.LParam.ToInt64()) is not (var packed and not -1))
        {
            return null;
        }

        return new((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }
}
