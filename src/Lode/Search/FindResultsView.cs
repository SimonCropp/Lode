/// <summary>
/// The Find Results tab: what Find in Files has found, laid out as Sublime's Find Results are
/// (see <see cref="FindResults"/>), each match marked. Enter, or a double-click, opens the file
/// in the text editor at the line; F4 and Shift+F4 go to the next match and the one before and
/// open it there; Delete takes a file out of the results, and so out of Replace. Like the file
/// list, the list holds no rows: it is told how many there are, and draws each as it shows, so
/// any number found costs it only the rows on screen. What the search finds is taken in every
/// tenth of a second, added at the end.
/// </summary>
sealed class FindResultsView : UserControl
{
    ResultsList list = new();
    Timer taking = new()
    {
        Interval = 100
    };

    readonly Font codeFont;
    readonly Dictionary<int, Bitmap> icons = [];
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
        codeFont = CodeFont(Font);
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

    // Cascadia Mono, as Windows Terminal has it, or Consolas where it isn't installed, at the
    // window's size.
    static Font CodeFont(Font font)
    {
        var cascadia = new Font("Cascadia Mono", font.SizeInPoints);
        if (cascadia.Name == "Cascadia Mono")
        {
            return cascadia;
        }

        cascadia.Dispose();
        return new("Consolas", font.SizeInPoints);
    }

    void FitColumn()
    {
        if (list.Columns.Count > 0)
        {
            list.Columns[0].Width = Math.Max(list.ClientSize.Width, 50);
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

    void ShowRows()
    {
        if (list.VirtualListSize != results.RowCount)
        {
            list.VirtualListSize = results.RowCount;
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

    void Select(int index)
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
        switch (args.KeyData)
        {
            case Keys.Delete:
                RemoveFileAt(FocusedRow);
                break;
            case Keys.Control | Keys.C:
                if (FocusedRow >= 0)
                {
                    Copy(CopyText(FocusedRow));
                }

                break;
            case Keys.Apps:
            case Keys.Shift | Keys.F10:
            {
                var bounds = list.GetItemRect(Math.Max(0, FocusedRow));
                ShowMenu(FocusedRow, new(bounds.Left + 20, bounds.Bottom));
                break;
            }
            default:
                return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
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
            menu.Items.Add(new ToolStripMenuItem("Remove from results", null, (_, _) => RemoveFileAt(index)) { ShortcutKeyDisplayString = "Delete" });
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(list, location);
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
        public ResultsList() =>
            DoubleBuffered = true;
    }
}
