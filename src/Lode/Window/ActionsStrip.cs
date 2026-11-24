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
        // From the keyboard the menu has no point, and the strip is never focused to be asked.
        if (message.Msg == WindowMessageContextMenu &&
            message.WParam == Handle &&
            unchecked((int)message.LParam.ToInt64()) is var packed and not -1)
        {
            MenuRequested?.Invoke(new((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)));
            return;
        }

        base.WndProc(ref message);
    }
}
