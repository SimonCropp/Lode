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

    readonly Control host;
    readonly ListView list;
    readonly Timer scrollTimer = new()
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
        host.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                Begin(ToList(eventArgs.Location), Control.ModifierKeys.HasFlag(Keys.Control));
            }
        };
        host.MouseMove += (_, eventArgs) =>
        {
            if (tracking)
            {
                Update(ToList(eventArgs.Location));
            }
        };
        host.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
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

    int RowHeight => list.Items.Count > 0 ? list.Items[0].Bounds.Height : 0;

    int FirstRowTop => list.Items.Count > 0 ? list.Items[0].Bounds.Top : 0;

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
        anchor = point with { Y = point.Y - FirstRowTop };
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
        var start = anchor with { Y = anchor.Y + FirstRowTop };
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
    // so a drag straight down the margin selects the rows beside it.
    void SelectRows(Rectangle marquee)
    {
        var height = RowHeight;
        if (height == 0)
        {
            return;
        }

        var top = FirstRowTop;
        for (var index = 0; index < list.Items.Count; index++)
        {
            var rowTop = top + index * height;
            var covered = rowTop < marquee.Bottom && rowTop + height > marquee.Top;
            var selected = toggle ? initial.Contains(index) != covered : covered;
            var item = list.Items[index];
            if (item.Selected != selected)
            {
                item.Selected = selected;
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

    /// <summary>
    /// The selection rectangle: a borderless, translucent window in the highlight colour that
    /// takes neither the focus nor the mouse.
    /// </summary>
    sealed class MarqueeWindow : Form
    {
        const int layered = 0x80000;
        const int transparent = 0x20;
        const int toolWindow = 0x80;
        const int noActivate = 0x8000000;

        public MarqueeWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = SystemColors.Highlight;
            Opacity = 0.3;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= layered | transparent | toolWindow | noActivate;
                return parameters;
            }
        }
    }
}
