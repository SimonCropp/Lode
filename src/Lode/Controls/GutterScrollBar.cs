/// <summary>
/// The folder list's horizontal scrollbar, run from the left edge of the view as Explorer's is.
/// The list's own starts where the list does, past the margin left of it, which left a gap
/// beside it. So this lies over the list's own, as wide again as the margin, and follows it:
/// it shows when that does, with its range, page and position, however the list was scrolled.
/// Moved, it scrolls the list: as far as the list's own would have for a click on an arrow or
/// the track, or to wherever the thumb is dragged.
/// </summary>
sealed class GutterScrollBar : HScrollBar
{
    const int horizontalScrollMessage = 0x0114;
    const int listViewScroll = 0x1014;

    readonly ShellListView list;

    public GutterScrollBar(ShellListView list)
    {
        this.list = list;
        Visible = false;

        // Clicked, it leaves the focus with the list, as the list's own scrollbar does.
        TabStop = false;
        list.HorizontalScrollChanged += Follow;
        list.HorizontalScrollBarCover = this;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);

        // Drawn as the list's own is: a thin thumb, which widens and shows its arrows under the
        // mouse.
        SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
    }

    void Follow()
    {
        if (IsDisposed)
        {
            return;
        }

        if (list.HorizontalScrollBar is not { } scroll)
        {
            Visible = false;
            return;
        }

        // The range before the position, which has to fall within it.
        Minimum = scroll.Minimum;
        Maximum = scroll.Maximum;
        LargeChange = scroll.Page;
        Value = Math.Clamp(scroll.Position, Minimum, Maximum);

        // The list has no border, so below its rows is only its scrollbar, which ends where they
        // do, short of the vertical one. This one starts at the edge of the view.
        Bounds = Rectangle.FromLTRB(
            0,
            list.Top + list.ClientSize.Height,
            list.Left + list.ClientSize.Width,
            list.Bottom);
        Visible = true;
    }

    protected override void OnScroll(ScrollEventArgs eventArgs)
    {
        if (list.HorizontalScrollBar is { } scroll)
        {
            if (eventArgs.Type is
                ScrollEventType.SmallDecrement or
                ScrollEventType.SmallIncrement or
                ScrollEventType.LargeDecrement or
                ScrollEventType.LargeIncrement or
                ScrollEventType.EndScroll)
            {
                // The codes are Windows' own, which the list answers as it does for its own
                // scrollbar.
                SendMessage(list.Handle, horizontalScrollMessage, (int)eventArgs.Type, IntPtr.Zero);
            }
            else
            {
                // The thumb dragged, or Home or End: the list, in pixels, by as far as it moved.
                SendMessage(list.Handle, listViewScroll, eventArgs.NewValue - scroll.Position, IntPtr.Zero);
            }

            // Where the list got to.
            if (list.HorizontalScrollBar is { } scrolled)
            {
                eventArgs.NewValue = scrolled.Position;
            }
        }

        base.OnScroll(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            list.HorizontalScrollChanged -= Follow;
            list.HorizontalScrollBarCover = null;
        }

        base.Dispose(disposing);
    }
}
