/// <summary>
/// A split container whose splitter works like File Explorer's: dragging it resizes the panes as
/// the mouse moves, and pressing it leaves the focus where it was, so a click that does not drag
/// changes nothing. SplitContainer's own splitter takes the focus when pressed and is dragged as
/// a hatched bar, and a click without a drag left it focused and drawn as selected.
/// </summary>
sealed class PaneSplitContainer : SplitContainer
{
    // While the splitter is being dragged, how far into it the mouse pressed it.
    int? grip;

    // Nor does Tab stop on the splitter.
    public PaneSplitContainer() =>
        TabStop = false;

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
        if (distance != SplitterDistance)
        {
            SplitterDistance = distance;
        }
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
