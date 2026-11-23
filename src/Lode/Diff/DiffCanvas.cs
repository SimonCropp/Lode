/// <summary>
/// The lines of a text file's diff, drawn as GitHub Desktop draws them: one above the other, the
/// old and new line numbers side by side in the gutter, or the old file beside the new, each
/// with its numbers; added lines green, deleted red, the part of a line that changed darker; each
/// hunk's @@ heading on blue, its gutter holding the arrows that show more of the file. Lines of
/// an uncommitted change can be picked to commit by clicking or dragging down their numbers, the
/// numbers of those picked blue; the strip beside the numbers picks a run of changes at once.
/// Rows are one height, and long lines scroll sideways, so only the rows showing are drawn,
/// however long the diff. Rows clicked are copied with Ctrl+C.
/// </summary>
sealed class DiffCanvas : Control
{
    // Pixels at 100% scale.
    const int numberPadding = 6;
    const int handleWidth = 10;
    const int textInset = 8;
    const int rowPadding = 4;

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

    // The rows clicked, from the first clicked to the last, for Ctrl+C, and the side, side by
    // side.
    int anchor = -1;
    int active = -1;
    bool selectRight;
    bool selecting;

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
        anchor = -1;
        active = -1;
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

    void BuildRows()
    {
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

        var selected = IsSelected(index);
        if (!split)
        {
            var line = row.After ?? row.Before!;
            var changed = row.AfterChanged ?? row.BeforeChanged;
            var gutter = Rectangle.FromLTRB(bounds.Left, bounds.Top, columns.HandleLeft, bounds.Bottom);
            var (background, gutterColour, inner) = Colours(line.Kind);
            DrawGutter(graphics, index, line, gutter, gutterColour, false, row.Before?.OldNumber, row.After?.NewNumber, columns.NumberWidth);
            DrawHandle(graphics, index, line, Rectangle.FromLTRB(columns.HandleLeft, bounds.Top, columns.TextLeft, bounds.Bottom), background);
            DrawText(graphics, line, changed, Rectangle.FromLTRB(columns.TextLeft, bounds.Top, columns.TextRight, bounds.Bottom), background, inner, selected);
            return;
        }

        DrawSide(graphics, index, row.Before, row.BeforeChanged, false, Rectangle.FromLTRB(bounds.Left, bounds.Top, columns.TextRight, bounds.Bottom), columns.NumberWidth, selected && !selectRight);
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

        DrawSide(graphics, index, row.After, row.AfterChanged, true, Rectangle.FromLTRB(columns.RightNumberLeft, bounds.Top, columns.RightTextRight, bounds.Bottom), columns.NumberWidth, selected && selectRight);
    }

    // One side of a row side by side: the line's number and text, or, with no line, the grey of
    // a side with nothing on it.
    void DrawSide(Graphics graphics, int index, DiffLine? line, ChangedRange? changed, bool right, Rectangle bounds, int numberWidth, bool selected)
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
        DrawText(graphics, line, changed, Rectangle.FromLTRB(gutter.Right, bounds.Top, bounds.Right, bounds.Bottom), background, inner, selected);
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

    void DrawText(Graphics graphics, DiffLine line, ChangedRange? changed, Rectangle bounds, Color background, Color inner, bool selected)
    {
        if (bounds.Width <= 0)
        {
            return;
        }

        if (selected)
        {
            background = ExplorerStyle.Blend(background, DiffStyle.Selected, 0.2f);
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
        if (changed is { } range &&
            range.Start + range.Length <= line.Content.Length)
        {
            var start = columns[range.Start];
            var end = columns[range.Start + range.Length];
            var left = textLeft + Measured(graphics, content[..start]);
            var right = textLeft + Measured(graphics, content[..end]);
            using var highlight = new SolidBrush(inner);
            graphics.FillRectangle(highlight, left, bounds.Top, Math.Max(1, right - left), bounds.Height);
            TextRenderer.DrawText(graphics, content[..start], codeFont, new Point(textLeft, y), ink, flags);
            TextRenderer.DrawText(graphics, content[start..end], codeFont, new Point(left, y), DiffStyle.InnerText(ink), flags);
            TextRenderer.DrawText(graphics, content[end..], codeFont, new Point(right, y), ink, flags);
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

    bool IsSelected(int index)
    {
        if (anchor < 0 ||
            active < 0)
        {
            return false;
        }

        return index >= Math.Min(anchor, active) &&
               index <= Math.Max(anchor, active);
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

        if (selecting &&
            index >= 0)
        {
            active = index;
            Invalidate();
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

        return Cursors.Default;
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
                if ((ModifierKeys & Keys.Shift) == 0 ||
                    anchor < 0)
                {
                    anchor = index;
                }

                active = index;
                selectRight = right;
                selecting = true;
                Invalidate();
                return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        selecting = false;
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
        anchor = -1;
        active = -1;
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
        if (anchor >= 0)
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
    /// The text of the rows clicked: one above the other, each line's; side by side, those of
    /// the side clicked.
    /// </summary>
    internal string SelectedText()
    {
        if (anchor < 0 ||
            active < 0)
        {
            return "";
        }

        var lines = new List<string>();
        for (var index = Math.Min(anchor, active); index <= Math.Max(anchor, active) && index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Kind == DiffRowKind.Hunk)
            {
                continue;
            }

            DiffLine? line;
            if (!split)
            {
                line = row.After ?? row.Before;
            }
            else if (selectRight)
            {
                line = row.After;
            }
            else
            {
                line = row.Before;
            }

            if (line != null)
            {
                lines.Add(line.Content);
            }
        }

        return string.Join(Environment.NewLine, lines);
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
                anchor = 0;
                active = rows.Count - 1;
                Invalidate();
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
            tip.Dispose();
            codeFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
