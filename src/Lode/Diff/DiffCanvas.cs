/// <summary>
/// The lines of a text file's diff, drawn as GitHub Desktop draws them: one above the other, the
/// old and new line numbers side by side in the gutter, or the old file beside the new, each
/// with its numbers; added lines green, deleted red, the part of a line that changed darker; each
/// hunk's @@ heading on blue, its gutter holding the arrows that show more of the file. Lines of
/// an uncommitted change can be picked to commit by clicking or dragging down their numbers, the
/// numbers of those picked blue; the strip beside the numbers picks a run of changes at once.
/// Rows are one height, and long lines scroll sideways, so only the rows showing are drawn,
/// however long the diff. The text is selected as an editor's is, from a character to another,
/// by dragging, a word by a double-click, a line by a third click, and copied with Ctrl+C.
/// </summary>
sealed class DiffCanvas : Control
{
    // Pixels at 100% scale.
    const int numberPadding = 6;
    const int handleWidth = 10;
    const int textInset = 8;
    const int rowPadding = 4;

    // How much of the selection's blue shows over a line, the colours under it showing through.
    const int selectionAlpha = 0x55;

    const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;

    VScrollBar vertical = new();
    HScrollBar horizontal = new();
    ToolTip tip = new();
    Font codeFont;
    int rowHeight;
    int charWidth;

    TextDiff? diff;
    IReadOnlyList<DiffHunk> hunks = [];
    IReadOnlyList<DiffHunk> unexpanded = [];
    List<DiffRow> rows = [];
    IReadOnlyList<string>? newLines;
    bool split;
    LinePicks? picks;
    string? pickHint;
    int widest;

    // What the pointer is over.
    int hoverRow = -1;
    Part hoverPart;
    bool hoverRight;

    // The text selected, for Ctrl+C: from where the drag started to where it is now, each a row
    // and a character of its line, nothing while they are the same place; and, side by side,
    // the side, which a drag stays on.
    TextPlace? anchor;
    TextPlace active;
    bool selectRight;
    bool selecting;

    // Scrolls while a drag selecting is past the text, to where the pointer is.
    Timer scrollTimer = new()
    {
        Interval = 50
    };

    Point selectPoint;

    // The last double-click, as Environment.TickCount64, and where: a third click there selects
    // the line.
    long doubleClickedAt = long.MinValue / 2;
    Point doubleClickPoint;

    // Numbers dragged down to pick lines: from where, which side, picking or not, and what was
    // picked before.
    bool dragging;
    int dragStart;
    bool dragRight;
    bool dragPick;
    LinePicks? dragBase;

    enum Part
    {
        None,
        Text,
        Gutter,
        Handle,
        ExpandUp,
        ExpandDown,
        ExpandShort
    }

    /// <summary>
    /// A place in the text: a row, and the character of its line it is before.
    /// </summary>
    internal readonly record struct TextPlace(int Row, int Column) : IComparable<TextPlace>
    {
        public int CompareTo(TextPlace other)
        {
            if (Row != other.Row)
            {
                return Row.CompareTo(other.Row);
            }

            return Column.CompareTo(other.Column);
        }
    }

    // The characters of a row's line selected, and whether the newline after them is.
    readonly record struct Selected(int Start, int End, bool Newline);

    /// <summary>
    /// The lines picked to commit changed, by a click or drag here.
    /// </summary>
    public event Action<LinePicks>? PicksChanged;

    public DiffCanvas()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "Diff";
        AccessibleRole = AccessibleRole.Table;
        BackColor = DiffStyle.Background;
        codeFont = ExplorerStyle.CodeFont(Font);
        Measure();
        vertical.Visible = false;
        horizontal.Visible = false;
        vertical.Scroll += (_, _) => Invalidate();
        horizontal.Scroll += (_, _) => Invalidate();
        vertical.ValueChanged += (_, _) => Invalidate();
        horizontal.ValueChanged += (_, _) => Invalidate();
        scrollTimer.Tick += (_, _) => ScrollWhileSelecting();
        foreach (var bar in (ScrollBar[]) [vertical, horizontal])
        {
            bar.TabStop = false;
            bar.HandleCreated += (_, _) => _ = SetWindowTheme(bar.Handle, ExplorerStyle.ControlTheme, null);
            Controls.Add(bar);
        }
    }

    void Measure()
    {
        rowHeight = codeFont.Height + LogicalToDeviceUnits(rowPadding);
        charWidth = Math.Max(1, TextRenderer.MeasureText(new('0', 20), codeFont, Size.Empty, TextFormatFlags.NoPadding).Width / 20);
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        codeFont.Dispose();
        codeFont = ExplorerStyle.CodeFont(Font);
        Measure();
        Arrange();
    }

    /// <summary>
    /// The rows drawn. For tests.
    /// </summary>
    internal IReadOnlyList<DiffRow> Rows => rows;

    /// <summary>
    /// The hunks drawn, as expanded. For tests.
    /// </summary>
    internal IReadOnlyList<DiffHunk> Hunks => hunks;

    internal LinePicks? Picks => picks;

    /// <summary>
    /// The first row showing.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int TopRow
    {
        get => vertical.Value;
        set => vertical.Value = Math.Clamp(value, 0, MaxTop);
    }

    int MaxTop => Math.Max(0, rows.Count - VisibleRows);

    int VisibleRows => Math.Max(1, TextHeight / Math.Max(1, rowHeight));

    int TextHeight
    {
        get
        {
            var height = ClientSize.Height;
            if (horizontal.Visible)
            {
                height -= horizontal.Height;
            }

            return height;
        }
    }

    int TextWidth
    {
        get
        {
            var width = ClientSize.Width;
            if (vertical.Visible)
            {
                width -= vertical.Width;
            }

            return Math.Max(1, width);
        }
    }

    /// <summary>
    /// Shows <paramref name="shown"/>, the arrows that show more of the file only given
    /// <paramref name="lines"/>, the file's lines, and lines to pick only given
    /// <paramref name="linePicks"/>. <paramref name="hint"/>, when there is one, is said when the
    /// numbers are clicked instead: why lines can't be picked. With <paramref name="keepPlace"/>,
    /// the same rows show as before, as for the same file read again.
    /// </summary>
    public void Show(TextDiff shown, IReadOnlyList<string>? lines, bool sideBySide, LinePicks? linePicks, string? hint, bool keepPlace)
    {
        var top = vertical.Value;
        var left = horizontal.Value;
        diff = shown;
        newLines = lines;
        split = sideBySide;
        picks = linePicks;
        pickHint = hint;
        var start = shown.Hunks;
        if (lines != null)
        {
            var oldCount = lines.Count - shown.Added + shown.Deleted;
            start = DiffExpansion.WithBottom(start, oldCount, lines.Count);
        }

        unexpanded = start;
        hunks = start;
        dragging = false;
        BuildRows();
        if (keepPlace)
        {
            TopRow = top;
            horizontal.Value = Math.Clamp(left, horizontal.Minimum, Math.Max(horizontal.Minimum, horizontal.Maximum - horizontal.LargeChange + 1));
            return;
        }

        vertical.Value = 0;
        horizontal.Value = 0;
    }

    /// <summary>
    /// The old file beside the new, or one above the other, keeping the place.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Split
    {
        get => split;
        set
        {
            if (split == value)
            {
                return;
            }

            split = value;
            var top = vertical.Value;
            BuildRows();
            TopRow = top;
        }
    }

    /// <summary>
    /// Shows the lines picked as picked elsewhere: the file's tick in the Changes pane clicked.
    /// </summary>
    public void SetPicks(LinePicks? linePicks)
    {
        if (dragging)
        {
            return;
        }

        picks = linePicks;
        Invalidate();
    }

    // The rows made again, which the selection was a place in.
    void BuildRows()
    {
        anchor = null;
        StopSelecting();
        rows = DiffRows.Build(hunks, split);
        widest = 0;
        foreach (var hunk in hunks)
        {
            foreach (var line in hunk.Lines)
            {
                widest = Math.Max(widest, ShownLength(line.Content));
            }
        }

        Arrange();
        Invalidate();
    }

    // How many columns a line takes, its tabs four.
    static int ShownLength(string content)
    {
        var length = 0;
        foreach (var character in content)
        {
            if (character == '\t')
            {
                length += 4 - length % 4;
                continue;
            }

            length++;
        }

        return length;
    }

    // The line with its tabs as the spaces they take, and where each of its characters went, to
    // find the part that changed.
    static string ExpandTabs(string content, out int[] columns)
    {
        columns = new int[content.Length + 1];
        if (!content.Contains('\t'))
        {
            for (var index = 0; index <= content.Length; index++)
            {
                columns[index] = index;
            }

            return content;
        }

        var builder = new StringBuilder(content.Length + 8);
        for (var index = 0; index < content.Length; index++)
        {
            columns[index] = builder.Length;
            var character = content[index];
            if (character == '\t')
            {
                builder.Append(' ', 4 - builder.Length % 4);
                continue;
            }

            builder.Append(character);
        }

        columns[content.Length] = builder.Length;
        return builder.ToString();
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        Arrange();
    }

    // The scrollbars: down the right while the rows run past the bottom, along the bottom while
    // the widest line runs past the side.
    void Arrange()
    {
        if (ClientSize.Width <= 0 ||
            ClientSize.Height <= 0)
        {
            return;
        }

        var barWidth = SystemInformation.VerticalScrollBarWidth;
        var barHeight = SystemInformation.HorizontalScrollBarHeight;
        var needsVertical = rows.Count * rowHeight > ClientSize.Height;
        var room = TextRoom(ClientSize.Width - (needsVertical ? barWidth : 0));
        var content = ContentWidth();
        var needsHorizontal = content > room;
        if (needsHorizontal &&
            !needsVertical &&
            rows.Count * rowHeight > ClientSize.Height - barHeight)
        {
            needsVertical = true;
            room = TextRoom(ClientSize.Width - barWidth);
            needsHorizontal = content > room;
        }

        vertical.Visible = needsVertical;
        horizontal.Visible = needsHorizontal;
        var width = ClientSize.Width - (needsVertical ? barWidth : 0);
        var height = ClientSize.Height - (needsHorizontal ? barHeight : 0);
        vertical.SetBounds(ClientSize.Width - barWidth, 0, barWidth, height);
        horizontal.SetBounds(0, ClientSize.Height - barHeight, width, barHeight);

        var visibleRows = Math.Max(1, height / Math.Max(1, rowHeight));
        vertical.Minimum = 0;
        vertical.Maximum = Math.Max(0, rows.Count - 1);
        vertical.LargeChange = Math.Max(1, visibleRows);
        vertical.SmallChange = 1;
        if (vertical.Value > MaxTop)
        {
            vertical.Value = MaxTop;
        }

        horizontal.Minimum = 0;
        horizontal.Maximum = Math.Max(0, content);
        horizontal.LargeChange = Math.Max(1, room);
        horizontal.SmallChange = Math.Max(1, charWidth * 4);
        var maxLeft = Math.Max(0, content - room + 1);
        if (!needsHorizontal)
        {
            horizontal.Value = 0;
        }
        else if (horizontal.Value > maxLeft)
        {
            horizontal.Value = maxLeft;
        }
    }

    // How wide the widest line is drawn, its +, - or space and the room either side included.
    int ContentWidth() =>
        LogicalToDeviceUnits(textInset) * 2 + (widest + 2) * charWidth;

    // How wide the text is shown in a width of the control: one side's, side by side.
    int TextRoom(int width)
    {
        var columns = ColumnsFor(width);
        return Math.Max(1, columns.TextRight - columns.TextLeft);
    }

    /// <summary>
    /// Where each part of a row is across: one above the other, the old number, the new, the
    /// strip that picks a run, then the text; side by side, the old number and text, the strip,
    /// then the new number and text.
    /// </summary>
    readonly record struct Columns(
        int NumberWidth,
        int HandleLeft,
        int TextLeft,
        int TextRight,
        int RightNumberLeft,
        int RightTextLeft,
        int RightTextRight);

    Columns ColumnsFor(int width)
    {
        var digits = 3;
        if (diff != null)
        {
            digits = Math.Max(3, diff.MaxLineNumber.ToString(CultureInfo.InvariantCulture).Length);
        }

        var numberWidth = digits * charWidth + LogicalToDeviceUnits(numberPadding) * 2;
        var handle = 0;
        if (picks != null)
        {
            handle = LogicalToDeviceUnits(handleWidth);
        }

        if (!split)
        {
            var handleLeft = numberWidth * 2;
            return new(numberWidth, handleLeft, handleLeft + handle, width, 0, 0, 0);
        }

        var half = Math.Max(numberWidth + 1, (width - handle) / 2);
        var rightNumber = half + handle;
        return new(numberWidth, half, numberWidth, half, rightNumber, rightNumber + numberWidth, width);
    }

    Columns Current => ColumnsFor(TextWidth);

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        using (var background = new SolidBrush(DiffStyle.Background))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        if (rows.Count == 0)
        {
            return;
        }

        var columns = Current;
        var width = TextWidth;
        var top = vertical.Value;
        var visible = TextHeight / rowHeight + 1;
        for (var offset = 0; offset < visible && top + offset < rows.Count; offset++)
        {
            var index = top + offset;
            var bounds = new Rectangle(0, offset * rowHeight, width, rowHeight);
            DrawRow(graphics, index, rows[index], bounds, columns);
        }

        if (split)
        {
            using var pen = new Pen(DiffStyle.Border);
            var height = Math.Min(TextHeight, (rows.Count - top) * rowHeight);
            graphics.DrawLine(pen, columns.HandleLeft - 1, 0, columns.HandleLeft - 1, height);
        }

        // The corner between the two scrollbars.
        if (vertical.Visible &&
            horizontal.Visible)
        {
            using var corner = new SolidBrush(SystemColors.Control);
            graphics.FillRectangle(corner, vertical.Left, horizontal.Top, vertical.Width, horizontal.Height);
        }
    }

    void DrawRow(Graphics graphics, int index, DiffRow row, Rectangle bounds, Columns columns)
    {
        if (row.Kind == DiffRowKind.Hunk)
        {
            DrawHunk(graphics, index, row, bounds, columns);
            return;
        }

        if (!split)
        {
            var line = row.After ?? row.Before!;
            var changed = row.AfterChanged ?? row.BeforeChanged;
            var gutter = Rectangle.FromLTRB(bounds.Left, bounds.Top, columns.HandleLeft, bounds.Bottom);
            var (background, gutterColour, inner) = Colours(line.Kind);
            DrawGutter(graphics, index, line, gutter, gutterColour, false, row.Before?.OldNumber, row.After?.NewNumber, columns.NumberWidth);
            DrawHandle(graphics, index, line, Rectangle.FromLTRB(columns.HandleLeft, bounds.Top, columns.TextLeft, bounds.Bottom), background);
            DrawText(graphics, line, changed, Rectangle.FromLTRB(columns.TextLeft, bounds.Top, columns.TextRight, bounds.Bottom), background, inner, SelectedOn(index, false));
            return;
        }

        DrawSide(graphics, index, row.Before, row.BeforeChanged, false, Rectangle.FromLTRB(bounds.Left, bounds.Top, columns.TextRight, bounds.Bottom), columns.NumberWidth);
        var handleLine = row.Before;
        if (handleLine is not { IsChange: true })
        {
            handleLine = row.After;
        }

        var (handleBackground, _, _) = Colours(handleLine?.Kind ?? DiffLineKind.Context);
        if (handleLine != null)
        {
            DrawHandle(graphics, index, handleLine, Rectangle.FromLTRB(columns.HandleLeft, bounds.Top, columns.RightNumberLeft, bounds.Bottom), handleBackground);
        }

        DrawSide(graphics, index, row.After, row.AfterChanged, true, Rectangle.FromLTRB(columns.RightNumberLeft, bounds.Top, columns.RightTextRight, bounds.Bottom), columns.NumberWidth);
    }

    // One side of a row side by side: the line's number and text, or, with no line, the grey of
    // a side with nothing on it.
    void DrawSide(Graphics graphics, int index, DiffLine? line, ChangedRange? changed, bool right, Rectangle bounds, int numberWidth)
    {
        if (line == null)
        {
            using var empty = new SolidBrush(DiffStyle.Empty);
            graphics.FillRectangle(empty, bounds);
            return;
        }

        var (background, gutterColour, inner) = Colours(line.Kind);
        var gutter = new Rectangle(bounds.Left, bounds.Top, numberWidth, bounds.Height);
        var number = line.OldNumber;
        if (right)
        {
            number = line.NewNumber;
        }

        DrawGutter(graphics, index, line, gutter, gutterColour, right, number, null, numberWidth);
        DrawText(graphics, line, changed, Rectangle.FromLTRB(gutter.Right, bounds.Top, bounds.Right, bounds.Bottom), background, inner, SelectedOn(index, right));
    }

    static (Color Background, Color Gutter, Color Inner) Colours(DiffLineKind kind) =>
        kind switch
        {
            DiffLineKind.Add => (DiffStyle.AddBackground, DiffStyle.AddGutter, DiffStyle.AddInner),
            DiffLineKind.Delete => (DiffStyle.DeleteBackground, DiffStyle.DeleteGutter, DiffStyle.DeleteInner),
            _ => (DiffStyle.Background, DiffStyle.Background, DiffStyle.Background)
        };

    // The numbers: blue for a line picked to commit, a shade lighter under the pointer.
    void DrawGutter(Graphics graphics, int index, DiffLine line, Rectangle gutter, Color colour, bool right, int? first, int? second, int numberWidth)
    {
        var text = DiffStyle.LineNumber;
        var pickable = picks != null && line is { IsChange: true, Index: not null };
        var hovered = index == hoverRow &&
                      hoverPart == Part.Gutter &&
                      (!split || hoverRight == right);
        if (pickable &&
            picks!.IsPicked(line.Index!.Value))
        {
            colour = DiffStyle.Picked;
            text = DiffStyle.PickedText;
            if (hovered)
            {
                colour = DiffStyle.PickedHover;
            }
        }
        else if (hovered &&
                 (pickable || (pickHint != null && line.IsChange)))
        {
            colour = line.Kind == DiffLineKind.Add ? DiffStyle.AddHover : DiffStyle.DeleteHover;
        }

        using (var brush = new SolidBrush(colour))
        {
            graphics.FillRectangle(brush, gutter);
        }

        var padding = LogicalToDeviceUnits(numberPadding);
        if (split)
        {
            DrawNumber(graphics, first, new(gutter.Left + padding, gutter.Top, numberWidth - padding * 2, gutter.Height), text);
            return;
        }

        DrawNumber(graphics, first, new(gutter.Left + padding, gutter.Top, numberWidth - padding * 2, gutter.Height), text);
        DrawNumber(graphics, second, new(gutter.Left + numberWidth + padding, gutter.Top, numberWidth - padding * 2, gutter.Height), text);
        using var pen = new Pen(DiffStyle.Border);
        graphics.DrawLine(pen, gutter.Left + numberWidth - 1, gutter.Top, gutter.Left + numberWidth - 1, gutter.Bottom);
    }

    void DrawNumber(Graphics graphics, int? number, Rectangle bounds, Color colour)
    {
        if (number is not { } value)
        {
            return;
        }

        TextRenderer.DrawText(graphics, value.ToString(CultureInfo.InvariantCulture), codeFont, bounds, colour, flags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // The strip that picks a run of changes: blue beside a line picked, the run lighter under the
    // pointer. Nothing beside a line both sides have.
    void DrawHandle(Graphics graphics, int index, DiffLine line, Rectangle bounds, Color background)
    {
        if (bounds.Width <= 0)
        {
            return;
        }

        var colour = background;
        if (picks != null &&
            line is { IsChange: true, Index: { } lineIndex })
        {
            colour = DiffStyle.Empty;
            if (picks.IsPicked(lineIndex))
            {
                colour = DiffStyle.Picked;
            }

            if (hoverPart == Part.Handle &&
                hoverRow >= 0 &&
                RunOf(hoverRow).Contains(index))
            {
                colour = DiffStyle.PickedHover;
            }
        }

        using var brush = new SolidBrush(colour);
        graphics.FillRectangle(brush, bounds);
    }

    // A line's text, the part that changed darker, and what of it is selected in the selection's
    // blue over that, its newline as a space after it.
    void DrawText(Graphics graphics, DiffLine line, ChangedRange? changed, Rectangle bounds, Color background, Color inner, Selected? selected)
    {
        if (bounds.Width <= 0)
        {
            return;
        }

        using (var brush = new SolidBrush(background))
        {
            graphics.FillRectangle(brush, bounds);
        }

        var state = graphics.Save();
        graphics.SetClip(bounds);
        var x = bounds.Left + LogicalToDeviceUnits(textInset) - horizontal.Value;
        var y = bounds.Top + (bounds.Height - codeFont.Height) / 2;
        var ink = DiffStyle.Text;
        if (line.Kind == DiffLineKind.Delete)
        {
            ink = DiffStyle.DeleteText;
        }

        var prefix = line.Kind switch
        {
            DiffLineKind.Add => "+",
            DiffLineKind.Delete => "-",
            _ => ""
        };
        if (prefix.Length > 0)
        {
            TextRenderer.DrawText(graphics, prefix, codeFont, new Point(x, y), ink, flags);
        }

        var textLeft = x + charWidth * 2;
        var content = ExpandTabs(line.Content, out var columns);

        // Where the part that changed starts and ends in the line with its tabs as spaces, and
        // across.
        (int Start, int End, int Left, int Right)? changedAt = null;
        if (changed is { } range &&
            range.Start + range.Length <= line.Content.Length)
        {
            var start = columns[range.Start];
            var end = columns[range.Start + range.Length];
            changedAt = (start, end, textLeft + Measured(graphics, content[..start]), textLeft + Measured(graphics, content[..end]));
            using var highlight = new SolidBrush(inner);
            graphics.FillRectangle(highlight, changedAt.Value.Left, bounds.Top, Math.Max(1, changedAt.Value.Right - changedAt.Value.Left), bounds.Height);
        }

        if (selected is { } chosen)
        {
            var left = textLeft + Measured(graphics, content[..columns[chosen.Start]]);
            var right = textLeft + Measured(graphics, content[..columns[chosen.End]]);
            if (chosen.Newline)
            {
                right += charWidth;
            }

            using var selection = new SolidBrush(Color.FromArgb(selectionAlpha, DiffStyle.Selected));
            graphics.FillRectangle(selection, left, bounds.Top, Math.Max(1, right - left), bounds.Height);
        }

        if (changedAt is { } at)
        {
            TextRenderer.DrawText(graphics, content[..at.Start], codeFont, new Point(textLeft, y), ink, flags);
            TextRenderer.DrawText(graphics, content[at.Start..at.End], codeFont, new Point(at.Left, y), DiffStyle.InnerText(ink), flags);
            TextRenderer.DrawText(graphics, content[at.End..], codeFont, new Point(at.Right, y), ink, flags);
        }
        else
        {
            TextRenderer.DrawText(graphics, content, codeFont, new Point(textLeft, y), ink, flags);
        }

        if (line.NoTrailingNewline)
        {
            DrawNoNewline(graphics, textLeft + Measured(graphics, content) + charWidth, bounds);
        }

        graphics.Restore(state);
    }

    // Off the screen's, outside painting: where the pointer is in the text.
    int Measured(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return TextRenderer.MeasureText(text, codeFont, Size.Empty, flags).Width;
    }

    int Measured(Graphics graphics, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return TextRenderer.MeasureText(graphics, text, codeFont, Size.Empty, flags).Width;
    }

    // The mark GitHub Desktop puts after a line with no newline after it: a circle struck through.
    void DrawNoNewline(Graphics graphics, int left, Rectangle bounds)
    {
        var size = Math.Max(6, codeFont.Height * 2 / 3);
        var box = new Rectangle(left, bounds.Top + (bounds.Height - size) / 2, size, size);
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(DiffStyle.DeletedCount, Math.Max(1, LogicalToDeviceUnits(1)));
        graphics.DrawEllipse(pen, box);
        graphics.DrawLine(pen, box.Left + size / 6, box.Bottom - size / 6, box.Right - size / 6, box.Top + size / 6);
        graphics.SmoothingMode = smoothing;
    }

    // A hunk's heading on blue, and in its gutter the arrows that show more of the file.
    void DrawHunk(Graphics graphics, int index, DiffRow row, Rectangle bounds, Columns columns)
    {
        var hunk = hunks[row.HunkIndex];
        using (var background = new SolidBrush(DiffStyle.HunkBackground))
        {
            graphics.FillRectangle(background, bounds);
        }

        var gutterWidth = columns.NumberWidth;
        if (!split)
        {
            gutterWidth = columns.NumberWidth * 2;
        }

        var gutter = new Rectangle(bounds.Left, bounds.Top, gutterWidth, bounds.Height);
        using (var gutterBrush = new SolidBrush(DiffStyle.HunkGutter))
        {
            graphics.FillRectangle(gutterBrush, gutter);
        }

        var expansion = ExpansionShown(hunk);
        var hovered = index == hoverRow;
        switch (expansion)
        {
            case HunkExpansion.Up:
                DrawArrow(graphics, gutter, up: true, hovered && hoverPart == Part.ExpandUp);
                break;
            case HunkExpansion.Down:
                DrawArrow(graphics, gutter, up: false, hovered && hoverPart == Part.ExpandDown);
                break;
            case HunkExpansion.Short:
                DrawShort(graphics, gutter, hovered && hoverPart == Part.ExpandShort);
                break;
            case HunkExpansion.Both:
                var upper = gutter with
                {
                    Height = gutter.Height / 2
                };
                var lower = Rectangle.FromLTRB(gutter.Left, upper.Bottom, gutter.Right, gutter.Bottom);
                DrawArrow(graphics, upper, up: false, hovered && hoverPart == Part.ExpandDown);
                DrawArrow(graphics, lower, up: true, hovered && hoverPart == Part.ExpandUp);
                break;
        }

        var state = graphics.Save();
        var text = Rectangle.FromLTRB(gutter.Right, bounds.Top, bounds.Right, bounds.Bottom);
        graphics.SetClip(text);
        var y = bounds.Top + (bounds.Height - codeFont.Height) / 2;
        TextRenderer.DrawText(graphics, hunk.Heading, codeFont, new Point(text.Left + LogicalToDeviceUnits(textInset) - horizontal.Value, y), DiffStyle.HunkText, flags);
        graphics.Restore(state);
    }

    // The arrows a hunk shows: only where the file's lines were read to show more of it.
    HunkExpansion ExpansionShown(DiffHunk hunk)
    {
        if (newLines == null)
        {
            return HunkExpansion.None;
        }

        return hunk.Expansion;
    }

    void DrawArrow(Graphics graphics, Rectangle bounds, bool up, bool hovered)
    {
        if (hovered)
        {
            using var hover = new SolidBrush(DiffStyle.HunkHover);
            graphics.FillRectangle(hover, bounds);
        }

        var size = Math.Min(bounds.Height - 2, LogicalToDeviceUnits(8));
        var centreX = bounds.Left + bounds.Width / 2f;
        var centreY = bounds.Top + bounds.Height / 2f;
        var half = size / 2f;
        var rise = half / 2f;
        if (up)
        {
            rise = -rise;
        }

        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Ink(hovered), Math.Max(1.5f, LogicalToDeviceUnits(2) * 0.75f));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        graphics.DrawLines(pen, new PointF(centreX - half, centreY - rise), new PointF(centreX, centreY + rise), new PointF(centreX + half, centreY - rise));
        graphics.SmoothingMode = smoothing;
    }

    // Arrows apart, up above down: show all of the short gap.
    void DrawShort(Graphics graphics, Rectangle bounds, bool hovered)
    {
        if (hovered)
        {
            using var hover = new SolidBrush(DiffStyle.HunkHover);
            graphics.FillRectangle(hover, bounds);
        }

        var upper = bounds with
        {
            Height = bounds.Height / 2
        };
        var lower = Rectangle.FromLTRB(bounds.Left, upper.Bottom, bounds.Right, bounds.Bottom);
        DrawArrow(graphics, upper with { Y = upper.Y + 1 }, up: true, hovered: false);
        DrawArrow(graphics, lower with { Y = lower.Y - 1 }, up: false, hovered: false);
    }

    static Color Ink(bool hovered)
    {
        if (hovered)
        {
            return DiffStyle.Text;
        }

        return DiffStyle.HunkText;
    }

    // The selection, its first place and its last. Null with nothing selected.
    (TextPlace Start, TextPlace End)? Selection
    {
        get
        {
            if (anchor is not { } from ||
                from == active)
            {
                return null;
            }

            if (from.CompareTo(active) < 0)
            {
                return (from, active);
            }

            return (active, from);
        }
    }

    // What of the line on a side of a row is selected: from the selection's start on its first
    // row, to its end on its last, the newline after each line but the last. Null for none, a
    // hunk's heading, or the other side.
    Selected? SelectedOn(int index, bool right)
    {
        if (Selection is not { } selection ||
            index < selection.Start.Row ||
            index > selection.End.Row ||
            (split && right != selectRight) ||
            LineAt(index, right) is not { } line)
        {
            return null;
        }

        var length = line.Content.Length;
        var start = 0;
        if (index == selection.Start.Row)
        {
            start = Math.Min(selection.Start.Column, length);
        }

        var end = length;
        if (index == selection.End.Row)
        {
            end = Math.Min(selection.End.Column, length);
        }

        var newline = index < selection.End.Row;
        if (start >= end &&
            !newline)
        {
            return null;
        }

        return new(start, end, newline);
    }

    // Where a side's characters start across, as scrolled: after the room before the text, and
    // the +, - or space with the gap after it.
    int CharactersLeft(bool right)
    {
        var columns = Current;
        var left = columns.TextLeft;
        if (split && right)
        {
            left = columns.RightTextLeft;
        }

        return left + LogicalToDeviceUnits(textInset) - horizontal.Value + charWidth * 2;
    }

    // The text of a side across, between the numbers and the side's edge.
    (int Left, int Right) SideText(bool right)
    {
        var columns = Current;
        if (split && right)
        {
            return (columns.RightTextLeft, columns.RightTextRight);
        }

        return (columns.TextLeft, columns.TextRight);
    }

    // The character of a line a point across is over, as measured, not counted, so a wide one
    // or a tab is found too: the line's length past its end, 0 before its start.
    int CharacterAt(DiffLine line, int x, bool right)
    {
        var offset = x - CharactersLeft(right);
        if (offset <= 0)
        {
            return 0;
        }

        // The last character starting at or before the point, the widths growing with each.
        var content = ExpandTabs(line.Content, out var columns);
        var low = 0;
        var high = line.Content.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Measured(content[..columns[middle]]) <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    // The place between characters of a line nearest a point across: where a click there puts
    // the selection's end.
    int ColumnAt(DiffLine line, int x, bool right)
    {
        var character = CharacterAt(line, x, right);
        if (character >= line.Content.Length)
        {
            return character;
        }

        var content = ExpandTabs(line.Content, out var columns);
        var offset = x - CharactersLeft(right);
        var before = Measured(content[..columns[character]]);
        var after = Measured(content[..columns[character + 1]]);
        if (offset - before > after - offset)
        {
            return character + 1;
        }

        return character;
    }

    // The row a point down is on, kept to those showing, for a drag past the top or the bottom.
    int RowAt(int y)
    {
        var top = vertical.Value;
        var last = Math.Min(rows.Count - 1, top + Math.Max(0, TextHeight - 1) / rowHeight);
        return Math.Clamp(top + (int) Math.Floor(y / (double) rowHeight), top, Math.Max(top, last));
    }

    // The place in the text on a side nearest a point: past the last row, the end of its line.
    TextPlace PlaceAt(Point point, bool right)
    {
        var row = RowAt(point.Y);
        if (LineAt(row, right) is not { } line)
        {
            return new(row, 0);
        }

        var below = (row - vertical.Value + 1) * rowHeight <= point.Y;
        if (below &&
            row == rows.Count - 1)
        {
            return new(row, line.Content.Length);
        }

        return new(row, ColumnAt(line, point.X, right));
    }

    /// <summary>
    /// Where in the control a place in the text on a side is: in the middle of its row, just
    /// after the gap before the character. For tests.
    /// </summary>
    internal Point PointOf(TextPlace place, bool right)
    {
        var line = LineAt(place.Row, right)!;
        var content = ExpandTabs(line.Content, out var columns);
        var x = CharactersLeft(right) + Measured(content[..columns[Math.Min(place.Column, line.Content.Length)]]);
        return new(x + 1, (place.Row - vertical.Value) * rowHeight + rowHeight / 2);
    }

    /// <summary>
    /// The word a character of a line is in, as a double-click selects it, from its first
    /// character to after its last: a run of letters, digits and underscores, or of whitespace,
    /// or else the one character.
    /// </summary>
    internal static (int Start, int End) WordAt(string content, int character)
    {
        if (content.Length == 0)
        {
            return (0, 0);
        }

        var at = Math.Clamp(character, 0, content.Length - 1);
        var kind = KindOf(content[at]);
        if (kind == CharacterKind.Other)
        {
            return (at, at + 1);
        }

        var start = at;
        while (start > 0 &&
               KindOf(content[start - 1]) == kind)
        {
            start--;
        }

        var end = at + 1;
        while (end < content.Length &&
               KindOf(content[end]) == kind)
        {
            end++;
        }

        return (start, end);
    }

    enum CharacterKind
    {
        Word,
        Space,
        Other
    }

    static CharacterKind KindOf(char character)
    {
        if (char.IsLetterOrDigit(character) ||
            character == '_')
        {
            return CharacterKind.Word;
        }

        if (char.IsWhiteSpace(character))
        {
            return CharacterKind.Space;
        }

        return CharacterKind.Other;
    }

    // What is at a point: the row, the part of it, and, side by side, whether on the right.
    (int Row, Part Part, bool Right) HitTest(Point point)
    {
        if (point.Y < 0 ||
            point.Y >= TextHeight ||
            point.X >= TextWidth)
        {
            return (-1, Part.None, false);
        }

        var index = vertical.Value + point.Y / rowHeight;
        if (index >= rows.Count)
        {
            return (-1, Part.None, false);
        }

        var row = rows[index];
        var columns = Current;
        if (row.Kind == DiffRowKind.Hunk)
        {
            var gutterWidth = columns.NumberWidth;
            if (!split)
            {
                gutterWidth *= 2;
            }

            if (point.X >= gutterWidth)
            {
                return (index, Part.Text, false);
            }

            var top = point.Y - point.Y / rowHeight * rowHeight < rowHeight / 2;
            var part = ExpansionShown(hunks[row.HunkIndex]) switch
            {
                HunkExpansion.Up => Part.ExpandUp,
                HunkExpansion.Down => Part.ExpandDown,
                HunkExpansion.Short => Part.ExpandShort,
                HunkExpansion.Both when top => Part.ExpandDown,
                HunkExpansion.Both => Part.ExpandUp,
                _ => Part.None
            };
            return (index, part, false);
        }

        if (!split)
        {
            if (point.X < columns.HandleLeft)
            {
                return (index, Part.Gutter, false);
            }

            if (point.X < columns.TextLeft)
            {
                return (index, Part.Handle, false);
            }

            return (index, Part.Text, false);
        }

        if (point.X < columns.NumberWidth)
        {
            return (index, Part.Gutter, false);
        }

        if (point.X < columns.HandleLeft)
        {
            return (index, Part.Text, false);
        }

        if (point.X < columns.RightNumberLeft)
        {
            return (index, Part.Handle, false);
        }

        if (point.X < columns.RightTextLeft)
        {
            return (index, Part.Gutter, true);
        }

        return (index, Part.Text, true);
    }

    // The line a click on the numbers of a row picks: on the side clicked, side by side.
    DiffLine? LineAt(int index, bool right)
    {
        var row = rows[index];
        if (!split)
        {
            return row.After ?? row.Before;
        }

        if (right)
        {
            return row.After;
        }

        return row.Before;
    }

    // The rows of the run of changes a row is in: those added and deleted together, up to the
    // lines both sides have either side of them.
    List<int> RunOf(int index)
    {
        var run = new List<int>();
        if (index < 0 ||
            index >= rows.Count ||
            !IsChangeRow(rows[index]))
        {
            return run;
        }

        var first = index;
        while (first > 0 &&
               IsChangeRow(rows[first - 1]))
        {
            first--;
        }

        var last = index;
        while (last < rows.Count - 1 &&
               IsChangeRow(rows[last + 1]))
        {
            last++;
        }

        for (var row = first; row <= last; row++)
        {
            run.Add(row);
        }

        return run;
    }

    static bool IsChangeRow(DiffRow row) =>
        row.Kind is DiffRowKind.Added or DiffRowKind.Deleted or DiffRowKind.Modified;

    static IEnumerable<int> ChangeIndexes(DiffRow row)
    {
        if (row.Before is { IsChange: true, Index: { } before })
        {
            yield return before;
        }

        if (row.After is { IsChange: true, Index: { } after } &&
            !ReferenceEquals(row.After, row.Before))
        {
            yield return after;
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var (index, part, right) = HitTest(args.Location);
        if (dragging)
        {
            DragTo(index);
            return;
        }

        if (selecting)
        {
            selectPoint = args.Location;
            SelectTo(args.Location);
            scrollTimer.Enabled = PastText(args.Location);
            Cursor = Cursors.IBeam;
            return;
        }

        if (index != hoverRow ||
            part != hoverPart ||
            right != hoverRight)
        {
            hoverRow = index;
            hoverPart = part;
            hoverRight = right;
            Invalidate();
        }

        Cursor = CursorFor(index, part, right);
    }

    Cursor CursorFor(int index, Part part, bool right)
    {
        var expands = part is Part.ExpandUp or Part.ExpandDown or Part.ExpandShort;
        var picksLine = picks != null &&
                        index >= 0 &&
                        part is Part.Gutter or Part.Handle &&
                        LineAt(index, right) is { IsChange: true };
        if (expands ||
            picksLine)
        {
            return Cursors.Hand;
        }

        if (part == Part.Text &&
            rows[index].Kind != DiffRowKind.Hunk)
        {
            return Cursors.IBeam;
        }

        return Cursors.Default;
    }

    // The selection's end moved to the place nearest a point, on the side it is on.
    void SelectTo(Point point)
    {
        if (anchor == null ||
            rows.Count == 0)
        {
            return;
        }

        var place = PlaceAt(point, selectRight);
        if (place == active)
        {
            return;
        }

        active = place;
        Invalidate();
    }

    // Whether a point is above or below the rows showing, or either side of the side's text, as
    // far as it scrolls that way: a drag selecting there scrolls.
    bool PastText(Point point)
    {
        if (point.Y < 0)
        {
            return TopRow > 0;
        }

        if (point.Y >= TextHeight)
        {
            return TopRow < MaxTop;
        }

        var (left, right) = SideText(selectRight);
        if (point.X < left)
        {
            return horizontal.Value > 0;
        }

        return horizontal.Visible &&
               point.X >= Math.Min(right, TextWidth);
    }

    // A drag selecting past the text: a row at a time up or down, a few characters sideways, the
    // selection following.
    void ScrollWhileSelecting()
    {
        if (!selecting ||
            !PastText(selectPoint))
        {
            scrollTimer.Stop();
            return;
        }

        var (left, right) = SideText(selectRight);
        if (selectPoint.Y < 0)
        {
            TopRow--;
        }
        else if (selectPoint.Y >= TextHeight)
        {
            TopRow++;
        }
        else if (selectPoint.X < left)
        {
            ScrollSideways(-charWidth * 2);
        }
        else if (selectPoint.X >= Math.Min(right, TextWidth))
        {
            ScrollSideways(charWidth * 2);
        }

        SelectTo(selectPoint);
    }

    void StopSelecting()
    {
        selecting = false;
        scrollTimer.Stop();
    }

    // The text pressed: the selection starts there, or, with Shift, ends there; a word for a
    // double-click, the line for a third click. Side by side, it stays on the side pressed.
    void StartSelecting(MouseEventArgs args, bool right)
    {
        var extend = (ModifierKeys & Keys.Shift) != 0 &&
                     anchor != null &&
                     (!split || right == selectRight);
        if (!extend)
        {
            selectRight = right;
        }

        var place = PlaceAt(args.Location, selectRight);
        if (args.Clicks == 2)
        {
            doubleClickedAt = Environment.TickCount64;
            doubleClickPoint = args.Location;
            SelectWord(place.Row, args.Location.X);
            return;
        }

        if (IsThirdClick(args.Location))
        {
            doubleClickedAt = long.MinValue / 2;
            SelectLine(place.Row);
            return;
        }

        if (!extend)
        {
            anchor = place;
        }

        active = place;
        selecting = true;
        selectPoint = args.Location;
        Capture = true;
        Invalidate();
    }

    bool IsThirdClick(Point point)
    {
        var size = SystemInformation.DoubleClickSize;
        return Environment.TickCount64 - doubleClickedAt <= SystemInformation.DoubleClickTime &&
               Math.Abs(point.X - doubleClickPoint.X) <= size.Width / 2 &&
               Math.Abs(point.Y - doubleClickPoint.Y) <= size.Height / 2;
    }

    // The word under a point across on a row selected, on the side the selection is on.
    void SelectWord(int row, int x)
    {
        if (LineAt(row, selectRight) is not { } line)
        {
            return;
        }

        var (start, end) = WordAt(line.Content, CharacterAt(line, x, selectRight));
        anchor = new(row, start);
        active = new(row, end);
        Invalidate();
    }

    // A row's line selected, with its newline: to the start of the row after, or the end of the
    // line on the last row.
    void SelectLine(int row)
    {
        if (LineAt(row, selectRight) is not { } line)
        {
            return;
        }

        anchor = new(row, 0);
        if (row + 1 < rows.Count)
        {
            active = new(row + 1, 0);
        }
        else
        {
            active = new(row, line.Content.Length);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        if (hoverRow >= 0)
        {
            hoverRow = -1;
            hoverPart = Part.None;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        Focus();
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        var (index, part, right) = HitTest(args.Location);
        if (index < 0)
        {
            return;
        }

        switch (part)
        {
            case Part.ExpandUp:
                ExpandAt(index, up: true);
                return;
            case Part.ExpandDown:
                ExpandAt(index, up: false);
                return;
            case Part.ExpandShort:
                ExpandAt(index, up: true);
                return;
            case Part.Gutter:
                StartDrag(index, right, args.Location);
                return;
            case Part.Handle:
                PickRun(index);
                return;
            case Part.Text:
                StartSelecting(args, right);
                return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (selecting)
        {
            StopSelecting();
            Capture = false;
        }

        if (args.Button == MouseButtons.Right)
        {
            ShowMenu(args.Location);
            return;
        }

        if (!dragging)
        {
            return;
        }

        dragging = false;
        dragBase = null;
        if (picks != null)
        {
            PicksChanged?.Invoke(picks);
        }
    }

    void StartDrag(int index, bool right, Point location)
    {
        var line = LineAt(index, right);
        if (line is not { IsChange: true, Index: { } lineIndex })
        {
            return;
        }

        if (picks == null)
        {
            if (pickHint != null)
            {
                tip.Show(pickHint, this, location.X + LogicalToDeviceUnits(12), location.Y + LogicalToDeviceUnits(12), 5000);
            }

            return;
        }

        dragging = true;
        dragStart = index;
        dragRight = right;
        dragBase = picks;
        dragPick = !picks.IsPicked(lineIndex);
        DragTo(index);
    }

    // Every line between where the drag started and the row now under the pointer takes the
    // state the first took, on the side the drag started on.
    void DragTo(int index)
    {
        if (dragBase == null ||
            index < 0)
        {
            return;
        }

        var lines = new List<int>();
        for (var row = Math.Min(dragStart, index); row <= Math.Max(dragStart, index); row++)
        {
            if (LineAt(row, dragRight) is { IsChange: true, Index: { } lineIndex })
            {
                lines.Add(lineIndex);
            }
        }

        picks = dragBase.With(lines, dragPick);
        Invalidate();
    }

    /// <summary>
    /// Picks every change in the run a row is in, or, with every one picked, none.
    /// </summary>
    internal void PickRun(int index)
    {
        if (picks == null)
        {
            return;
        }

        var lines = RunOf(index)
            .SelectMany(_ => ChangeIndexes(rows[_]))
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var pick = !lines.All(picks.IsPicked);
        picks = picks.With(lines, pick);
        Invalidate();
        PicksChanged?.Invoke(picks);
    }

    /// <summary>
    /// Picks the line on a row, or leaves it out: its numbers clicked.
    /// </summary>
    internal void PickLine(int index, bool right)
    {
        if (picks == null ||
            LineAt(index, right) is not { IsChange: true, Index: { } lineIndex })
        {
            return;
        }

        picks = picks.With([lineIndex], !picks.IsPicked(lineIndex));
        Invalidate();
        PicksChanged?.Invoke(picks);
    }

    /// <summary>
    /// Shows more of the file at the hunk heading on row <paramref name="index"/>: above it, or
    /// below the hunk before it.
    /// </summary>
    internal void ExpandAt(int index, bool up)
    {
        if (newLines == null ||
            index < 0 ||
            index >= rows.Count ||
            rows[index].Kind != DiffRowKind.Hunk)
        {
            return;
        }

        var hunkIndex = rows[index].HunkIndex;
        var hunk = hunks[hunkIndex];
        IReadOnlyList<DiffHunk>? expanded;
        if (up)
        {
            expanded = DiffExpansion.Expand(hunks, hunkIndex, up: true, newLines);
        }
        else if (hunk.Expansion is HunkExpansion.Down or HunkExpansion.Both)
        {
            expanded = DiffExpansion.Expand(hunks, hunkIndex - 1, up: false, newLines);
        }
        else
        {
            expanded = null;
        }

        Expanded(expanded);
    }

    void Expanded(IReadOnlyList<DiffHunk>? expanded)
    {
        if (expanded == null)
        {
            return;
        }

        var top = vertical.Value;
        hunks = expanded;
        BuildRows();
        TopRow = top;
    }

    internal bool CanExpand =>
        newLines != null &&
        (hunks.Count > 1 || hunks is [{ Expansion: HunkExpansion.Up }]);

    internal bool IsExpanded => !ReferenceEquals(hunks, unexpanded);

    internal void ExpandWhole()
    {
        if (newLines != null)
        {
            Expanded(DiffExpansion.ExpandWhole(hunks, newLines));
        }
    }

    internal void Collapse() =>
        Expanded(unexpanded);

    void ShowMenu(Point location)
    {
        var commands = new List<QuickMenu.Command?>();
        if (Selection != null)
        {
            commands.Add(
                new("Copy", Copy)
                {
                    Glyph = Icons.Copy,
                    Shortcut = "Ctrl+C"
                });
        }

        if (newLines != null)
        {
            if (commands.Count > 0)
            {
                commands.Add(null);
            }

            commands.Add(new("Expand whole file", ExpandWhole, CanExpand));
            commands.Add(new("Collapse expanded lines", Collapse, IsExpanded));
        }

        PaneMenu.Show(this, location, commands, _ => { });
    }

    /// <summary>
    /// The text selected, as the lines have it, without the +, - or space before each: side by
    /// side, of the side it is on. Hunks' headings, and rows the side has no line on, are left
    /// out.
    /// </summary>
    internal string SelectedText()
    {
        if (Selection is not { } selection)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (var index = selection.Start.Row; index <= selection.End.Row; index++)
        {
            if (SelectedOn(index, selectRight) is not { } selected)
            {
                continue;
            }

            builder.Append(LineAt(index, selectRight)!.Content, selected.Start, selected.End - selected.Start);
            if (selected.Newline)
            {
                builder.Append(Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Selects from one place in the text to another, on a side, as a drag would. For tests.
    /// </summary>
    internal void Select(TextPlace from, TextPlace to, bool right)
    {
        selectRight = right;
        anchor = from;
        active = to;
        Invalidate();
    }

    /// <summary>
    /// Ctrl+A: everything, from the start of the first row to the end of the last line, on the
    /// side the selection was on, rather than to the heading of more of the file after it.
    /// </summary>
    internal void SelectAll()
    {
        var last = rows.Count - 1;
        while (last >= 0 &&
               LineAt(last, selectRight) == null)
        {
            last--;
        }

        if (last < 0)
        {
            return;
        }

        anchor = new(0, 0);
        active = new(last, LineAt(last, selectRight)!.Content.Length);
        Invalidate();
    }

    void Copy()
    {
        var text = SelectedText();
        if (text.Length > 0)
        {
            FolderView.SetClipboardText(text);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        var handled = true;
        switch (args.KeyData)
        {
            case Keys.Up:
                TopRow--;
                break;
            case Keys.Down:
                TopRow++;
                break;
            case Keys.PageUp:
                TopRow -= VisibleRows;
                break;
            case Keys.PageDown:
                TopRow += VisibleRows;
                break;
            case Keys.Home:
            case Keys.Control | Keys.Home:
                TopRow = 0;
                break;
            case Keys.End:
            case Keys.Control | Keys.End:
                TopRow = MaxTop;
                break;
            case Keys.Left:
                ScrollSideways(-horizontal.SmallChange);
                break;
            case Keys.Right:
                ScrollSideways(horizontal.SmallChange);
                break;
            case Keys.Control | Keys.C:
                Copy();
                break;
            case Keys.Control | Keys.A:
                SelectAll();
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
        }
    }

    void ScrollSideways(int by)
    {
        if (!horizontal.Visible)
        {
            return;
        }

        var maxLeft = Math.Max(0, horizontal.Maximum - horizontal.LargeChange + 1);
        horizontal.Value = Math.Clamp(horizontal.Value + by, 0, maxLeft);
    }

    protected override void OnMouseWheel(MouseEventArgs args)
    {
        base.OnMouseWheel(args);
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            ScrollSideways(-args.Delta / 120 * horizontal.SmallChange);
            return;
        }

        var lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
        TopRow -= args.Delta * lines / 120;
    }

    const int mouseHorizontalWheel = 0x020E;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == mouseHorizontalWheel)
        {
            var delta = (short) ((message.WParam.ToInt64() >> 16) & 0xFFFF);
            ScrollSideways(delta / 120 * horizontal.SmallChange);
            message.Result = 1;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnGotFocus(EventArgs args)
    {
        base.OnGotFocus(args);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            scrollTimer.Dispose();
            tip.Dispose();
            codeFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
