/// <summary>
/// A split container whose splitter works like File Explorer's: dragging it resizes the panes as
/// the mouse moves, and pressing it leaves the focus where it was, so a click that does not drag
/// changes nothing. SplitContainer's own splitter takes the focus when pressed and is dragged as
/// a hatched bar, and a click without a drag left it focused and drawn as selected. Over the
/// splitter it shows Explorer's resize arrows too, rather than SplitContainer's split cursor, the
/// arrows with a bar between them.
/// </summary>
sealed class PaneSplitContainer : SplitContainer
{
    const int setCursorMessage = 0x0020;
    const int clientArea = 1;
    const uint redrawAllChildren = 0x0080;
    const uint redrawUpdateNow = 0x0100;

    // While the splitter is being dragged, how far into it the mouse pressed it.
    int? grip;

    public PaneSplitContainer()
    {
        // Nor does Tab stop on the splitter.
        TabStop = false;

        // The splitter white, as the panes either side are, with only a faint line down it
        // (see OnPaint), as Explorer has between its panes. In the window's gray, it joined
        // the gray track of the tree's scrollbar into a wide gray bar.
        BackColor = ExplorerStyle.Window;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var bounds = SplitterRectangle;
        using var pen = new Pen(ExplorerStyle.PaneDivider);
        if (Orientation == Orientation.Vertical)
        {
            var x = bounds.Left + bounds.Width / 2;
            eventArgs.Graphics.DrawLine(pen, x, bounds.Top, x, bounds.Bottom - 1);
            return;
        }

        var y = bounds.Top + bounds.Height / 2;
        eventArgs.Graphics.DrawLine(pen, bounds.Left, y, bounds.Right - 1, y);
    }

    /// <summary>
    /// The cursor over a point of the container: Explorer's resize arrows over the splitter, and
    /// null elsewhere, for whatever SplitContainer would show.
    /// </summary>
    internal Cursor? CursorAt(Point point)
    {
        if (IsSplitterFixed ||
            !SplitterRectangle.Contains(point))
        {
            return null;
        }

        if (Orientation == Orientation.Vertical)
        {
            return Cursors.SizeWE;
        }

        return Cursors.SizeNS;
    }

    // SplitContainer picks its split cursor whenever the mouse moves over the splitter, and
    // keeps it to itself, so the cursor is put right as Windows asks for it instead. Not asked
    // for while the mouse is captured, so the arrows stay through a drag, as in Explorer.
    protected override void WndProc(ref Message message)
    {
        // Only when the mouse is over the container itself, not over a pane inside it, which
        // asks its parents too.
        if (message.Msg == setCursorMessage &&
            message.WParam == message.HWnd &&
            (message.LParam & 0xFFFF) == clientArea &&
            CursorAt(PointToClient(Cursor.Position)) is { } cursor)
        {
            Cursor.Current = cursor;
            message.Result = 1;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left ||
            IsSplitterFixed ||
            !SplitterRectangle.Contains(eventArgs.Location))
        {
            base.OnMouseDown(eventArgs);
            return;
        }

        // Not base, which is what takes the focus and starts the hatched bar. WinForms has
        // already captured the mouse, so the moves keep coming wherever it goes.
        grip = Along(eventArgs.Location) - SplitterDistance;
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (grip is not { } offset)
        {
            base.OnMouseMove(eventArgs);
            return;
        }

        // Not base either, which would put the arrow back when the mouse runs on past where a
        // pane stops shrinking.
        var length = Orientation == Orientation.Vertical ? Width : Height;
        var furthest = Math.Max(Panel1MinSize, length - SplitterWidth - Panel2MinSize);
        var distance = Math.Clamp(Along(eventArgs.Location) - offset, Panel1MinSize, furthest);
        if (distance == SplitterDistance)
        {
            return;
        }

        SplitterDistance = distance;
        // The panes painted now, before the next move is taken. Windows paints only once no
        // other message is waiting, and while the mouse keeps moving one always is, so until it
        // stopped, what a pane grew into showed what was there before: for the folder tree,
        // which paints through a buffer and so does not wipe its background first, its
        // scrollbar at every width it passed through. Only what the resize left to be painted,
        // in the container and every pane in it.
        RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, redrawUpdateNow | redrawAllChildren);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        grip = null;
        base.OnMouseUp(eventArgs);
    }

    // The mouse taken by another window part way through (Alt+Tab, say) ends the drag.
    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        grip = null;
        base.OnMouseCaptureChanged(eventArgs);
    }

    // A point's position along the direction the splitter moves.
    int Along(Point point)
    {
        if (Orientation == Orientation.Vertical)
        {
            return point.X;
        }

        return point.Y;
    }
}
