/// <summary>
/// Explorer leaves a margin to the left of its file list, and dragging from it draws a
/// selection rectangle that selects the rows it covers. A Windows list view has no such margin,
/// so the folder view leaves one beside its list, and this tracks drags that start there: it
/// draws the rectangle, selects the rows it spans (Ctrl adds to the selection, toggling what it
/// covers), and scrolls the list when the pointer goes above or below it. A click in the margin
/// without a drag clears the selection, as a click on empty space does.
/// </summary>
sealed class GutterMarquee : IDisposable
{
    const int listViewScroll = 0x1014;

    Control host;
    ListView list;
    Timer scrollTimer = new()
    {
        Interval = 50
    };

    MarqueeWindow? window;
    bool tracking;
    bool toggle;
    HashSet<int> initial = [];

    // Where the drag started: X in list coordinates, Y measured from the top of the first row,
    // so it stays put as the list scrolls.
    Point anchor;
    Point last;

    public GutterMarquee(Control host, ListView list)
    {
        this.host = host;
        this.list = list;
        host.MouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                Begin(ToList(args.Location), Control.ModifierKeys.HasFlag(Keys.Control));
            }
        };
        host.MouseMove += (_, args) =>
        {
            if (tracking)
            {
                Update(ToList(args.Location));
            }
        };
        host.MouseUp += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                End();
            }
        };
        host.MouseCaptureChanged += (_, _) =>
        {
            if (!host.Capture)
            {
                End();
            }
        };
        scrollTimer.Tick += (_, _) => ScrollTowardsPointer();
    }

    Point ToList(Point hostPoint) =>
        list.PointToClient(host.PointToScreen(hostPoint));

    int RowHeight
    {
        get
        {
            if (list.Items.Count > 0)
            {
                return list.GetItemRect(0).Height;
            }

            return 0;
        }
    }

    int FirstRowTop
    {
        get
        {
            if (list.Items.Count > 0)
            {
                return list.GetItemRect(0).Top;
            }

            return 0;
        }
    }

    // The rows the rectangle covered when it was last moved: some of them it may since have left.
    int coveredFirst;
    int coveredLast = -1;

    // The list's header sits above the first visible row.
    int HeaderBottom => list.TopItem?.Bounds.Top ?? 0;

    /// <summary>
    /// Starts a drag at <paramref name="point"/>, in list coordinates. With
    /// <paramref name="add"/> (Ctrl), the selection is kept and the rows covered are toggled.
    /// </summary>
    internal void Begin(Point point, bool add)
    {
        tracking = true;
        toggle = add;
        initial = add ? list.SelectedIndices.Cast<int>().ToHashSet() : [];
        coveredFirst = 0;
        coveredLast = -1;
        anchor = point with
        {
            Y = point.Y - FirstRowTop
        };
        last = point;
        list.Focus();
        if (!add)
        {
            list.SelectedIndices.Clear();
        }

        scrollTimer.Start();
    }

    /// <summary>
    /// Moves the far corner of the rectangle to <paramref name="point"/>, in list coordinates,
    /// and selects the rows it spans.
    /// </summary>
    internal void Update(Point point)
    {
        if (!tracking)
        {
            return;
        }

        last = point;
        var start = anchor with
        {
            Y = anchor.Y + FirstRowTop
        };
        var marquee = Rectangle.FromLTRB(
            Math.Min(start.X, point.X),
            Math.Min(start.Y, point.Y),
            Math.Max(start.X, point.X),
            Math.Max(start.Y, point.Y));
        SelectRows(marquee);
        Show(marquee);
    }

    internal void End()
    {
        if (!tracking)
        {
            return;
        }

        tracking = false;
        scrollTimer.Stop();
        window?.Hide();
    }

    // A row is covered when the rectangle overlaps it from top to bottom, wherever it is across,
    // so a drag straight down the margin selects the rows beside it. The rows are all one height,
    // so which are covered is worked out rather than looked for, and only those, and the ones
    // covered last time, are touched: the list may hold a great many, and has to be asked about
    // each one.
    void SelectRows(Rectangle marquee)
    {
        var height = RowHeight;
        if (height == 0)
        {
            return;
        }

        // Row n runs from top + n * height to the same again plus height.
        var top = FirstRowTop;
        var count = list.Items.Count;
        var first = Math.Max((int)Math.Floor((marquee.Top - top) / (double)height), 0);
        var last = Math.Min((int)Math.Ceiling((marquee.Bottom - top) / (double)height) - 1, count - 1);

        // Those it covered before, to put back any it has left, then those it covers now.
        Select(coveredFirst, Math.Min(coveredLast, count - 1), first, last);
        Select(first, last, first, last);
        coveredFirst = first;
        coveredLast = last;
    }

    // Brings the rows from one to another in line with the rectangle, which covers those from
    // first to last.
    void Select(int from, int to, int first, int last)
    {
        var selection = list.SelectedIndices;
        for (var index = from; index <= to; index++)
        {
            var covered = index >= first &&
                          index <= last;
            var selected = toggle ? initial.Contains(index) != covered : covered;
            if (selection.Contains(index) == selected)
            {
                continue;
            }

            if (selected)
            {
                selection.Add(index);
            }
            else
            {
                selection.Remove(index);
            }
        }
    }

    // Drawn as a translucent window over the list, clipped to the margin and the rows below
    // the header. Only for a view on screen.
    void Show(Rectangle marquee)
    {
        if (host.FindForm() is not { Visible: true } form)
        {
            return;
        }

        // In list coordinates the margin is the strip left of zero, as wide as the list is in.
        var visible = Rectangle.FromLTRB(-list.Left, HeaderBottom, list.ClientSize.Width, list.ClientSize.Height);
        var clipped = Rectangle.Intersect(marquee, visible);
        if (clipped.Width < 1 ||
            clipped.Height < 1)
        {
            window?.Hide();
            return;
        }

        window ??= new();
        window.Bounds = list.RectangleToScreen(clipped);
        if (!window.Visible)
        {
            window.Show(form);
        }
    }

    // While the pointer is above or below the list, the list scrolls a row at a time towards it
    // and the rectangle follows.
    void ScrollTowardsPointer()
    {
        if (!tracking)
        {
            return;
        }

        var height = RowHeight;
        var step = 0;
        if (last.Y < HeaderBottom)
        {
            step = -height;
        }
        else if (last.Y > list.ClientSize.Height)
        {
            step = height;
        }

        if (step == 0)
        {
            return;
        }

        SendMessage(list.Handle, listViewScroll, IntPtr.Zero, step);
        Update(last);
    }

    public void Dispose()
    {
        scrollTimer.Dispose();
        window?.Dispose();
    }

}