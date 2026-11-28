/// <summary>
/// Under Changes' files, as GitHub Desktop has its Stashed changes: the repository's stashes,
/// newest first, Lode's, left on a branch switched off, and any made in a terminal or by another
/// tool, under a row saying how many, which shows them or puts them away again. Right-click a
/// stash to restore it or discard it; Delete discards it. Hidden while there are none.
/// </summary>
sealed class StashList : Panel
{
    // Pixels at 100% scale.
    const int inset = DesktopStyle.Inset;
    const int iconSize = 16;

    // As many rows as show before the stashes scroll, so the files keep their room.
    const int mostRows = 4;

    const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

    Heading heading;
    RowList rows;
    RowTip rowTip;
    IReadOnlyList<GitStashes.Stash> stashes = [];
    bool expanded;

    /// <summary>
    /// A stash right-clicked, and where in <see cref="Rows"/>, for its menu.
    /// </summary>
    public event Action<GitStashes.Stash, Point>? MenuRequested;

    /// <summary>
    /// Delete pressed on a stash.
    /// </summary>
    public event Action<GitStashes.Stash>? DiscardRequested;

    public StashList()
    {
        Dock = DockStyle.Bottom;
        Visible = false;
        BackColor = DesktopStyle.Background;
        heading = new(this);
        rows = new(DesktopStyle.RowHeight(Font))
        {
            AccessibleName = "Stashes",
            MultiSelect = false,
            Visible = false
        };
        rows.DrawRow += DrawRow;
        rows.MouseUp += OnRowsMouseUp;
        rows.KeyDown += OnRowsKeyDown;
        rows.GotFocus += (_, _) => rows.Invalidate();
        rows.LostFocus += (_, _) => rows.Invalidate();
        rowTip = new(rows, TipAt);

        // Docked from the last added: the heading along the top, the stashes under it.
        Controls.Add(rows);
        Controls.Add(heading);
        Fit();
    }

    /// <summary>
    /// The stashes listed. For tests.
    /// </summary>
    internal IReadOnlyList<GitStashes.Stash> Stashes => stashes;

    /// <summary>
    /// Whether the stashes show under the heading, or only the heading.
    /// </summary>
    internal bool Expanded => expanded;

    internal RowList Rows => rows;

    /// <summary>
    /// What the heading says.
    /// </summary>
    internal string Caption => $"Stashed changes ({stashes.Count:N0})";

    /// <summary>
    /// Lists <paramref name="listing"/>, the stash picked staying picked where it is still
    /// stashed, and shows only with some.
    /// </summary>
    public void SetStashes(IReadOnlyList<GitStashes.Stash> listing)
    {
        string? picked = null;
        if (rows.SelectedIndices.Count == 1 &&
            rows.SelectedIndices[0] < stashes.Count)
        {
            picked = stashes[rows.SelectedIndices[0]].Commit;
        }

        stashes = listing;
        rows.BeginUpdate();
        rows.SelectedIndices.Clear();
        rows.SetRows(stashes.Count);
        for (var index = 0; index < stashes.Count; index++)
        {
            if (stashes[index].Commit == picked)
            {
                rows.SelectedIndices.Add(index);
                rows.FocusedItem = rows.Items[index];
            }
        }

        rows.EndUpdate();
        rows.Invalidate();
        heading.AccessibleName = Caption;
        heading.Invalidate();
        Visible = stashes.Count > 0;
        Fit();
    }

    /// <summary>
    /// Shows the stashes under the heading, or puts them away, as a click on it does.
    /// </summary>
    internal void Toggle()
    {
        expanded = !expanded;
        heading.Invalidate();
        Fit();
    }

    // As tall as the heading, and the stashes under it while they show, as many as fit without
    // taking the files' room.
    void Fit()
    {
        // Shown first, so the rows are laid out, and measured, as they are.
        var shown = expanded && stashes.Count > 0;
        rows.Visible = shown;
        var height = heading.Height;
        if (shown)
        {
            height += rows.RowHeight() * Math.Min(stashes.Count, mostRows);
        }

        heading.AccessibleDescription = expanded ? "Put the stashes away" : "Show the stashes";
        if (Height != height)
        {
            Height = height;
        }
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        Fit();
    }

    /// <summary>
    /// A stash in words, for its tooltip and a screen reader: what it is, the branch it was made
    /// on, how long ago, and its name.
    /// </summary>
    internal static string Describe(GitStashes.Stash stash, DateTimeOffset now)
    {
        var text = $"{stash.Text}, stashed";
        if (stash.Branch is { } branch)
        {
            text += $" on {branch}";
        }

        if (stash.Made is { } made)
        {
            text += $" {Ago(made, now)}";
        }

        return $"{text}, {stash.Name}";
    }

    static string Ago(DateTimeOffset made, DateTimeOffset now)
    {
        var ago = GitBranches.Ago(made, now);
        if (ago == "just now")
        {
            return ago;
        }

        return $"{ago} ago";
    }

    string? TipAt(Point point)
    {
        if (rows.HitTest(point).Item is not { } item ||
            item.Index >= stashes.Count)
        {
            return null;
        }

        return Describe(stashes[item.Index], HistoryPane.Time.GetUtcNow());
    }

    void OnRowsMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right ||
            rows.HitTest(args.Location).Item is not { } item ||
            item.Index >= stashes.Count)
        {
            return;
        }

        rows.SelectedIndices.Clear();
        rows.SelectedIndices.Add(item.Index);
        rows.FocusedItem = item;
        MenuRequested?.Invoke(stashes[item.Index], args.Location);
    }

    void OnRowsKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyData != Keys.Delete ||
            rows.SelectedIndices.Count != 1 ||
            rows.SelectedIndices[0] >= stashes.Count)
        {
            return;
        }

        DiscardRequested?.Invoke(stashes[rows.SelectedIndices[0]]);
        args.Handled = true;
    }

    // The stash's icon, what it is, and at the right, dimmed, the branch it was made on and how
    // long ago, as History's rows say who made a commit and when.
    void DrawRow(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= stashes.Count)
        {
            return;
        }

        var stash = stashes[index];
        var (background, text, secondary, _) = rows.Colours(index);
        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        RowList.DrawRowLine(graphics, bounds);
        var left = bounds.Left + LogicalToDeviceUnits(inset);
        var size = LogicalToDeviceUnits(iconSize);
        graphics.DrawImage(Icons.Bitmap(Icons.Stash, size, secondary), left, bounds.Top + (bounds.Height - size) / 2, size, size);
        var room = Rectangle.FromLTRB(left + size + LogicalToDeviceUnits(7), bounds.Top, bounds.Right - LogicalToDeviceUnits(inset), bounds.Bottom);

        var when = stash.Branch ?? "";
        if (stash.Made is { } made)
        {
            var ago = GitBranches.Ago(made, HistoryPane.Time.GetUtcNow());
            when = when.Length == 0 ? ago : $"{when} • {ago}";
        }

        if (when.Length > 0)
        {
            // The branch and when give way to what the stash is, to at most half the row.
            var whenWidth = Math.Min(room.Width / 2, TextRenderer.MeasureText(graphics, when, rows.Font, Size.Empty, flags).Width);
            TextRenderer.DrawText(graphics, when, rows.Font, Rectangle.FromLTRB(room.Right - whenWidth, room.Top, room.Right, room.Bottom), secondary, flags);
            room = Rectangle.FromLTRB(room.Left, room.Top, room.Right - whenWidth - LogicalToDeviceUnits(10), room.Bottom);
        }

        TextRenderer.DrawText(graphics, stash.Text, rows.Font, room, text, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rowTip.Dispose();
        }

        base.Dispose(disposing);
    }

    // The row over the stashes: an arrow, pointing down while they show, and how many there are,
    // with the line between it and the files above. A click, or Space or Enter, shows them or puts
    // them away.
    sealed class Heading : Control
    {
        StashList list;

        public Heading(StashList list)
        {
            this.list = list;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Dock = DockStyle.Top;
            Height = 29;
            TabStop = true;
            BackColor = DesktopStyle.AltBackground;
            ForeColor = DesktopStyle.Text;
            AccessibleRole = AccessibleRole.OutlineButton;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs args)
        {
            base.OnMouseClick(args);
            if (args.Button == MouseButtons.Left)
            {
                Focus();
                list.Toggle();
            }
        }

        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (args.KeyData is Keys.Space or Keys.Enter)
            {
                list.Toggle();
                args.Handled = true;
            }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Enter or Keys.Space ||
            base.IsInputKey(keyData);

        protected override void OnGotFocus(EventArgs args)
        {
            base.OnGotFocus(args);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs args)
        {
            base.OnLostFocus(args);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var graphics = args.Graphics;
            graphics.Clear(BackColor);
            using var pen = new Pen(DesktopStyle.Border);
            graphics.DrawLine(pen, 0, 0, Width, 0);

            var left = LogicalToDeviceUnits(inset);
            var arrowSize = LogicalToDeviceUnits(12);
            var arrow = Icons.ChevronRight;
            if (list.expanded)
            {
                arrow = Icons.ChevronDown;
            }

            graphics.DrawImage(Icons.Bitmap(arrow, arrowSize, DesktopStyle.SecondaryText), left, (Height - arrowSize) / 2, arrowSize, arrowSize);
            var textBounds = Rectangle.FromLTRB(left + arrowSize + LogicalToDeviceUnits(8), 0, Width - LogicalToDeviceUnits(inset), Height);
            TextRenderer.DrawText(graphics, list.Caption, Font, textBounds, ForeColor, flags);
            if (Focused &&
                ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
            }
        }
    }
}
