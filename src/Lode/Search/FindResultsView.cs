/// <summary>
/// The Find Results tab: what Find in Files has found, laid out as Sublime's Find Results are
/// (see <see cref="FindResults"/>), each match marked. Enter, or a double-click, opens the file
/// in the text editor at the line; F4 and Shift+F4 go to the next match and the one before and
/// open it there; Delete takes a line that matches out of the results, and so out of Replace,
/// which leaves it as it is, and on any other row of a file, the file; Space previews what
/// Replace would make of the file, in the diff view. Like the file list, the list holds no
/// rows: it is told how many there are, and draws each as it shows, so any number found costs
/// it only the rows on screen. What the search finds is taken in every tenth of a second, added
/// at the end.
/// </summary>
sealed class FindResultsView : UserControl
{
    ResultsList list = new();
    Timer taking = new()
    {
        Interval = 100
    };

    Font codeFont;
    Dictionary<int, Bitmap> icons = [];
    FindResults results = new(new("", ""));

    // The search running, and what it hands its results over in.
    CancelSource? search;
    ConcurrentQueue<FileResult> found = new();

    // What was last searched for, for Refresh to search again.
    (FindQuery Query, SearchWhere Where, Matcher Matcher, SystemItems? HiddenSystem)? last;

    /// <summary>
    /// Opens a file in the text editor at a line and column. Replaced in tests.
    /// </summary>
    internal Action<string, int, int> OpenAt;

    /// <summary>
    /// Lists what git doesn't ignore in a repository: kept in the code folder until it changes.
    /// Replaced in tests.
    /// </summary>
    internal Func<string, Cancel, GitScope?> ListGit = (top, cancel) => GitCache.Searchable(top, () => GitScope.List(top, cancel));

    /// <summary>
    /// Show in folder: opens the folder a file is in, with it selected.
    /// </summary>
    public event Action<string>? ShowInFolderRequested;

    /// <summary>
    /// A search started or finished, for the tab's title.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Preview replace, on a file's row or one of its lines: what Replace would make of the file.
    /// </summary>
    public event Action<FileResult>? PreviewRequested;

    public FindResults Results => results;

    public bool IsSearching => search != null;

    /// <summary>
    /// The search running. Completes once all it found is shown.
    /// </summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The list the rows are in. For tests.
    /// </summary>
    internal ListView List => list;

    public FindResultsView(Settings settings)
    {
        OpenAt = (path, line, column) => TextEditors.OpenAt(this, settings, path, line, column);
        BackColor = ExplorerStyle.Window;
        codeFont = ExplorerStyle.CodeFont(Font);
        Padding = new(LogicalToDeviceUnits(12), 0, 0, 0);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.HeaderStyle = ColumnHeaderStyle.None;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = ExplorerStyle.Window;
        list.VirtualMode = true;
        list.OwnerDraw = true;
        list.Columns.Add("", 100);

        // Each row the height of a line of code or of a small icon, whichever is taller.
        list.SmallImageList = new()
        {
            ImageSize = new(1, Math.Max(codeFont.Height, ShellIcons.SmallIconSize.Height) + LogicalToDeviceUnits(4))
        };
        list.RetrieveVirtualItem += (_, args) => args.Item = new(results.TextOf(results.RowAt(args.ItemIndex)));
        list.DrawColumnHeader += (_, args) => args.DrawDefault = true;
        list.DrawItem += (_, args) => DrawRow(args.Graphics, args.Bounds, args.ItemIndex);
        list.ItemActivate += (_, _) => OpenRow(FocusedRow);
        list.KeyDown += OnListKeyDown;
        list.MouseUp += OnListMouseUp;
        list.Resize += (_, _) => FitColumn();
        list.HandleCreated += (_, _) =>
        {
            _ = SetWindowTheme(list.Handle, ExplorerStyle.ControlTheme, null);
            FitColumn();
        };
        Controls.Add(list);

        taking.Tick += (_, _) => Take();
        ShowRows();
    }

    // The one column across the list, less a scrollbar's room when the rows run past the
    // bottom, as RowList fits its own: as wide as the list, a scrollbar coming down the side
    // would bring one along the bottom too. From the list's own height, not its client area's,
    // which a scrollbar along the bottom takes from.
    void FitColumn()
    {
        if (list.Columns.Count == 0)
        {
            return;
        }

        var width = list.Width;
        if (list.VirtualListSize * list.RowHeight() > list.Height)
        {
            // As wide as on the screen the list is on, not the one Windows started on.
            width -= SystemInformation.GetVerticalScrollBarWidthForDpi(list.DeviceDpi);
        }

        width = Math.Max(width, 50);
        var column = list.Columns[0];

        // The column is never wider than the list, so no scrollbar along the bottom is wanted.
        // But one the list put there, for a column wider than it for a moment, stays once the
        // column is as wide as the list again, and goes only for a column narrower than that:
        // so the column is made a pixel narrower first. The moment comes as the tab strip,
        // fitting its tabs, makes itself a pixel wider and back, and as rows running past the
        // bottom bring a scrollbar down the side before the column is narrowed for it.
        if (list.ScrollsAlongTheBottom)
        {
            column.Width = width - 1;
        }

        if (column.Width != width)
        {
            column.Width = width;
        }
    }

    /// <summary>
    /// Searches for <paramref name="query"/>, in place of what was found before. With
    /// <paramref name="error"/>, says that instead: there is nothing to search for, or nowhere.
    /// </summary>
    public void Run(FindQuery query, SearchWhere? where, Matcher? matcher, string? error, SystemItems? hiddenSystem)
    {
        Stop();
        results = new(query);
        if (error != null ||
            where == null ||
            matcher == null)
        {
            last = null;
            results.Error = error ?? "Nothing to search for.";
            results.Done = true;
            ShowRows();
            StateChanged?.Invoke();
            return;
        }

        last = (query, where, matcher, hiddenSystem);
        var cancel = new CancelSource();
        search = cancel;
        found = new();
        results.Counts = new();
        results.Report = new();
        ShowRows();
        taking.Start();
        Searching = SearchAsync(results, where, matcher, hiddenSystem, cancel);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Searches again for what was last searched for, git asked again what it ignores.
    /// </summary>
    public void SearchAgain()
    {
        if (last is not { } again)
        {
            return;
        }

        GitCache.ForgetSearchable();
        Run(again.Query, again.Where, again.Matcher, null, again.HiddenSystem);
    }

    async Task SearchAsync(FindResults mine, SearchWhere where, Matcher matcher, SystemItems? hiddenSystem, CancelSource cancel)
    {
        var queue = found;
        try
        {
            await Task.Run(
                () => ContentSearch.Run(mine.Query, where, matcher, hiddenSystem, _ => ListGit(_, cancel.Token), queue, mine.Counts, mine.Report, cancel.Token),
                cancel.Token);
        }
        catch (OperationCanceledException)
        {
            mine.Stopped = true;
        }
        catch (Exception exception)
        {
            // Lode's own, as what a file couldn't give is counted rather than thrown.
            IssueLauncher.LaunchForException("Find in Files failed", exception);
            mine.Stopped = true;
        }

        if (IsDisposed ||
            mine != results)
        {
            return;
        }

        Take();
        mine.Done = true;
        search = null;
        taking.Stop();
        cancel.Dispose();
        ShowRows();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Stops the search running, keeping what it found.
    /// </summary>
    public void Stop()
    {
        if (search == null)
        {
            return;
        }

        search.Cancel();
        search = null;
        taking.Stop();
        Take();
        results.Stopped = true;
        results.Done = true;
        ShowRows();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Shows what Replace did in place of what was found.
    /// </summary>
    public void ShowReplaced(FindResults replaced)
    {
        Stop();
        results = replaced;
        ShowRows();
        StateChanged?.Invoke();
    }

    // Adds what the search has found since it was last looked at.
    void Take()
    {
        var added = false;
        while (found.TryDequeue(out var result))
        {
            results.Add(result);
            added = true;
        }

        if (added ||
            !results.Done)
        {
            ShowRows();
            StateChanged?.Invoke();
        }
    }

    // The column fitted again, as rows running past the bottom bring a scrollbar down the side,
    // which leaves the list's own size as it was.
    void ShowRows()
    {
        if (list.VirtualListSize != results.RowCount)
        {
            list.VirtualListSize = results.RowCount;
            FitColumn();
        }

        list.Invalidate();
    }

    int FocusedRow
    {
        get
        {
            if (list.FocusedItem is { } item)
            {
                return item.Index;
            }

            if (list.SelectedIndices.Count > 0)
            {
                return list.SelectedIndices[0];
            }

            return -1;
        }
    }

    public void FocusList()
    {
        list.Focus();
        if (list.VirtualListSize > 0 &&
            FocusedRow < 0)
        {
            Select(Math.Max(0, results.NextMatchRow(0, true)));
        }
    }

    /// <summary>
    /// Picks a row, and gives it the focus, which the keys act on.
    /// </summary>
    internal void Select(int index)
    {
        if (index < 0 ||
            index >= list.VirtualListSize)
        {
            return;
        }

        list.SelectedIndices.Clear();
        list.SelectedIndices.Add(index);
        list.Items[index].Focused = true;
        list.EnsureVisible(index);
    }

    /// <summary>
    /// Opens what a row is of in the text editor: a line at that line, a file at its first match.
    /// </summary>
    internal void OpenRow(int index)
    {
        if (index < 0 ||
            index >= results.RowCount)
        {
            return;
        }

        var row = results.RowAt(index);
        if (row.File < 0)
        {
            return;
        }

        var file = results.FileAt(row.File);
        if (row.Kind == FindResults.RowKind.Line)
        {
            var line = file.Lines[row.Line];
            OpenAt(file.Path, line.Number, line.Column);
            return;
        }

        var first = file.Lines.FirstOrDefault(_ => !_.IsContext);
        if (first == null)
        {
            OpenAt(file.Path, 1, 1);
            return;
        }

        OpenAt(file.Path, first.Number, first.Column);
    }

    /// <summary>
    /// F4 and Shift+F4: the next match, or the one before, going round at the end, opened.
    /// </summary>
    internal void GoToMatch(bool forward)
    {
        var from = FocusedRow;
        var step = 1;
        if (!forward)
        {
            step = -1;
        }

        var next = results.NextMatchRow(from + step, forward);
        if (next < 0)
        {
            var start = 0;
            if (!forward)
            {
                start = results.RowCount - 1;
            }

            next = results.NextMatchRow(start, forward);
        }

        if (next < 0)
        {
            return;
        }

        Select(next);
        OpenRow(next);
    }

    /// <summary>
    /// Takes the file a row is of out of the results.
    /// </summary>
    internal void RemoveFileAt(int index)
    {
        if (index < 0 ||
            index >= results.RowCount)
        {
            return;
        }

        var row = results.RowAt(index);
        if (row.File < 0)
        {
            return;
        }

        results.Remove(row.File);
        ShowRows();
        Select(Math.Min(index, results.RowCount - 1));
    }

    /// <summary>
    /// Delete: a line that matches taken out of the results, so Replace leaves it as it is; on
    /// any other row of a file, the file.
    /// </summary>
    internal void RemoveAt(int index)
    {
        if (!IsMatchingLine(index))
        {
            RemoveFileAt(index);
            return;
        }

        var row = results.RowAt(index);
        results.RemoveLine(row.File, row.Line);
        ShowRows();
        Select(Math.Min(index, results.RowCount - 1));
    }

    bool IsMatchingLine(int index)
    {
        if (index < 0 ||
            index >= results.RowCount)
        {
            return false;
        }

        var row = results.RowAt(index);
        return row.Kind == FindResults.RowKind.Line &&
               !results.FileAt(row.File).Lines[row.Line].IsContext;
    }

    // Whether what Replace would make of the files can be previewed: not once it has replaced,
    // nor with nothing searched for.
    bool CanPreview =>
        results is
        {
            Replaced: null,
            Error: null
        };

    /// <summary>
    /// Space: what Replace would make of the file a row is of, in the diff view.
    /// </summary>
    internal void PreviewAt(int index)
    {
        if (!CanPreview ||
            index < 0 ||
            index >= results.RowCount)
        {
            return;
        }

        var row = results.RowAt(index);
        if (row.File < 0)
        {
            return;
        }

        PreviewRequested?.Invoke(results.FileAt(row.File));
    }

    // What Copy takes from a row: a file's path, a line's text.
    string CopyText(int index)
    {
        var row = results.RowAt(index);
        return row.Kind switch
        {
            FindResults.RowKind.File => results.FileAt(row.File).Path,
            FindResults.RowKind.Line => results.FileAt(row.File).Lines[row.Line].Text,
            _ => results.TextOf(row)
        };
    }

    static void Copy(string text)
    {
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    // F4 is the address bar's elsewhere, so it is taken here first, while the results have the
    // focus.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F4:
                GoToMatch(true);
                return true;
            case Keys.Shift | Keys.F4:
                GoToMatch(false);
                return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    void OnListKeyDown(object? sender, KeyEventArgs args)
    {
        if (!Key(args.KeyData))
        {
            return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
    }

    /// <summary>
    /// A key pressed in the list, on the row with the focus: whether it was one of the results'.
    /// </summary>
    internal bool Key(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Delete:
                RemoveAt(FocusedRow);
                return true;
            case Keys.Space:
                PreviewAt(FocusedRow);
                return true;
            case Keys.Control | Keys.C:
                if (FocusedRow >= 0)
                {
                    Copy(CopyText(FocusedRow));
                }

                return true;
            case Keys.Apps:
            case Keys.Shift | Keys.F10:
            {
                var bounds = list.GetItemRect(Math.Max(0, FocusedRow));
                ShowMenu(FocusedRow, new(bounds.Left + 20, bounds.Bottom));
                return true;
            }
            default:
                return false;
        }
    }

    void OnListMouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = list.HitTest(args.Location);
        var index = hit.Item?.Index ?? -1;
        if (index >= 0)
        {
            Select(index);
        }

        ShowMenu(index, args.Location);
    }

    void ShowMenu(int index, Point location)
    {
        var menu = MenuAt(index);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(list, location);
    }

    /// <summary>
    /// The right-click menu of a row, or of none, -1, for below the last.
    /// </summary>
    internal ExplorerMenu MenuAt(int index)
    {
        var menu = new ExplorerMenu();
        var row = default(FindResults.Row);
        var ofFile = false;
        if (index >= 0 &&
            index < results.RowCount)
        {
            row = results.RowAt(index);
            ofFile = row.File >= 0;
        }

        if (ofFile)
        {
            var path = results.FileAt(row.File).Path;
            var open = new ToolStripMenuItem("Open", null, (_, _) => OpenRow(index))
            {
                ShortcutKeyDisplayString = "Enter"
            };
            open.Font = new(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Show in folder", null, (_, _) => ShowInFolderRequested?.Invoke(path));
            menu.Items.Add("Copy path", null, (_, _) => Copy(path));
            if (row.Kind == FindResults.RowKind.Line)
            {
                menu.Items.Add(new ToolStripMenuItem("Copy line", null, (_, _) => Copy(CopyText(index))) { ShortcutKeyDisplayString = "Ctrl+C" });
            }
        }

        menu.Items.Add("Copy all results", null, (_, _) => Copy(results.ToText()));
        if (ofFile)
        {
            menu.Items.Add(new ToolStripSeparator());
            if (CanPreview)
            {
                menu.Items.Add(new ToolStripMenuItem("Preview replace", null, (_, _) => PreviewAt(index)) { ShortcutKeyDisplayString = "Space" });
            }

            if (IsMatchingLine(index))
            {
                menu.Items.Add(new ToolStripMenuItem("Remove line from results", null, (_, _) => RemoveAt(index)) { ShortcutKeyDisplayString = "Delete" });
                menu.Items.Add("Remove file from results", null, (_, _) => RemoveFileAt(index));
            }
            else
            {
                menu.Items.Add(new ToolStripMenuItem("Remove from results", null, (_, _) => RemoveFileAt(index)) { ShortcutKeyDisplayString = "Delete" });
            }
        }

        return menu;
    }

    const TextFormatFlags textFlags =
        TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine |
        TextFormatFlags.NoPadding |
        TextFormatFlags.VerticalCenter |
        TextFormatFlags.ExpandTabs;

    void DrawRow(Graphics graphics, Rectangle bounds, int index)
    {
        if (index >= results.RowCount)
        {
            return;
        }

        var selected = list.SelectedIndices.Contains(index);
        var background = ExplorerStyle.Window;
        var text = list.ForeColor;
        var secondary = ExplorerStyle.SecondaryText;
        if (selected)
        {
            if (SystemInformation.HighContrast)
            {
                background = SystemColors.Highlight;
                text = SystemColors.HighlightText;
                secondary = SystemColors.HighlightText;
            }
            else if (list.Focused)
            {
                background = ExplorerStyle.Blend(ExplorerStyle.Window, ExplorerStyle.Accent, 0.2f);
            }
            else
            {
                background = ExplorerStyle.Hover(ExplorerStyle.Window);
            }
        }

        using (var fill = new SolidBrush(background))
        {
            graphics.FillRectangle(fill, bounds);
        }

        var row = results.RowAt(index);
        var x = bounds.Left;
        var lineBounds = Rectangle.FromLTRB(x, bounds.Top, bounds.Right, bounds.Bottom);
        switch (row.Kind)
        {
            case FindResults.RowKind.Heading:
            case FindResults.RowKind.Summary:
                TextRenderer.DrawText(graphics, results.TextOf(row), Font, lineBounds, secondary, textFlags | TextFormatFlags.EndEllipsis);
                break;
            case FindResults.RowKind.File:
            {
                var file = results.FileAt(row.File);
                var icon = IconFor(file.Path);
                var iconSize = ShellIcons.SmallIconSize;
                graphics.DrawImage(icon, x, bounds.Top + (bounds.Height - iconSize.Height) / 2, iconSize.Width, iconSize.Height);
                var textBounds = Rectangle.FromLTRB(x + iconSize.Width + LogicalToDeviceUnits(6), bounds.Top, bounds.Right, bounds.Bottom);
                TextRenderer.DrawText(graphics, results.TextOf(row), Font, textBounds, text, textFlags | TextFormatFlags.PathEllipsis);
                break;
            }
            case FindResults.RowKind.Gap:
                TextRenderer.DrawText(graphics, "   ..", codeFont, lineBounds, secondary, textFlags);
                break;
            case FindResults.RowKind.Line:
                DrawLine(graphics, bounds, results.FileAt(row.File).Lines[row.Line], background, text, secondary);
                break;
        }
    }

    // The number, a colon after it for a line that matches, then the line, each match on a shade
    // of the accent with an accent border.
    void DrawLine(Graphics graphics, Rectangle bounds, ResultLine line, Color background, Color text, Color secondary)
    {
        var number = FindResults.LineNumber(line) + " ";
        var numberWidth = TextRenderer.MeasureText(graphics, number, codeFont, Size.Empty, textFlags).Width;
        TextRenderer.DrawText(graphics, number, codeFont, bounds, secondary, textFlags);
        var left = bounds.Left + numberWidth;
        var textBounds = Rectangle.FromLTRB(left, bounds.Top, bounds.Right, bounds.Bottom);
        if (!SystemInformation.HighContrast)
        {
            using var shade = new SolidBrush(ExplorerStyle.Blend(background, ExplorerStyle.Accent, 0.3f));
            using var border = new Pen(ExplorerStyle.Accent);
            var inset = LogicalToDeviceUnits(2);
            foreach (var (start, length) in line.Matches)
            {
                if (start > line.Text.Length)
                {
                    continue;
                }

                var before = TextWidth(graphics, line.Text[..start]);
                var through = TextWidth(graphics, line.Text[..Math.Min(line.Text.Length, start + length)]);
                var match = new Rectangle(left + before, bounds.Top + inset, Math.Max(2, through - before), bounds.Height - inset * 2 - 1);
                graphics.FillRectangle(shade, match);
                graphics.DrawRectangle(border, match);
            }
        }

        var color = text;
        if (line.IsContext)
        {
            color = secondary;
        }

        TextRenderer.DrawText(graphics, line.Text, codeFont, textBounds, color, textFlags | TextFormatFlags.EndEllipsis);
    }

    int TextWidth(Graphics graphics, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return TextRenderer.MeasureText(graphics, text, codeFont, Size.Empty, textFlags).Width;
    }

    Bitmap IconFor(string path)
    {
        var icon = ShellIcons.GetIcon(path, false);
        if (!icons.TryGetValue(icon, out var bitmap))
        {
            bitmap = ShellIcons.Render(icon, ShellIcons.SmallIconSize, Point.Empty);
            icons[icon] = bitmap;
        }

        return bitmap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            search?.Cancel();
            search = null;
            taking.Dispose();
            codeFont.Dispose();
            foreach (var bitmap in icons.Values)
            {
                bitmap.Dispose();
            }

            list.SmallImageList?.Dispose();
        }

        base.Dispose(disposing);
    }

    // Drawn through a buffer, so rows being added as the search goes don't flicker.
    sealed class ResultsList : ListView
    {
        const int listViewGetItemRect = 0x100E;
        const int horizontalScrollbar = 0x00100000;

        public ResultsList() =>
            DoubleBuffered = true;

        /// <summary>
        /// How tall a row is as the list lays them out, or, with none laid out yet, as tall as
        /// asked. Asked of the list itself, as RowList asks it, rather than through GetItemRect,
        /// which throws for a list with no first row yet.
        /// </summary>
        public int RowHeight()
        {
            var height = SmallImageList?.ImageSize.Height ?? 1;
            if (!IsHandleCreated ||
                VirtualListSize == 0)
            {
                return height;
            }

            // LVIR_BOUNDS, the whole row, in Left.
            var bounds = new NativeRect();
            if (SendMessage(Handle, listViewGetItemRect, IntPtr.Zero, ref bounds) == IntPtr.Zero)
            {
                return height;
            }

            return Math.Max(height, bounds.Bottom - bounds.Top);
        }

        /// <summary>
        /// Whether the list shows a scrollbar along the bottom.
        /// </summary>
        public bool ScrollsAlongTheBottom =>
            IsHandleCreated &&
            (GetWindowLong(Handle, WindowStyle) & horizontalScrollbar) != 0;
    }
}
