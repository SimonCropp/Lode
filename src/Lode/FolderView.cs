/// <summary>
/// One tab's worth of file browsing: the file list, its navigation history, and the file
/// operations that act on the selection.
/// </summary>
sealed class FolderView : UserControl
{
    const int nameColumn = 0;
    const int dateColumn = 1;
    const int typeColumn = 2;
    const int sizeColumn = 3;

    // The list's columns, in order, by the index sorting and the cells use. Name is always
    // shown; the others can be hidden from the header's right-click menu, in every view at once.
    // On This PC, Date modified and Size show each drive's free space and total size.
    sealed record ColumnDefinition(string Key, string Title, string ThisPCTitle, int Width, HorizontalAlignment Alignment);

    static readonly ColumnDefinition[] columnDefinitions =
    [
        new("Name", "Name", "Name", 380, HorizontalAlignment.Left),
        new("Date", "Date modified", "Free space", 150, HorizontalAlignment.Left),
        new("Type", "Type", "Type", 180, HorizontalAlignment.Left),
        new("Size", "Size", "Total size", 110, HorizontalAlignment.Right)
    ];

    // The columns the list has now, in the order shown, and the widths of any taken away, so
    // one shown again comes back as wide as it was.
    List<int> shownColumns = [];
    readonly Dictionary<int, int> columnWidths = [];

    /// <summary>
    /// A column was hidden or shown. Every view shares the setting, so each should refresh.
    /// </summary>
    public event Action? ColumnsChanged;

    readonly Settings settings;
    readonly Places places;
    readonly ShellListView list = new();

    // Watcher events arrive on thread-pool threads and can come in bursts. This turns them into
    // at most one reload per interval, on the UI thread.
    readonly ChangeSignal changes;
    readonly List<string> history = [];
    int historyIndex = -1;

    FolderWatcher? watcher;
    bool loading;
    int loadVersion;

    // False while nobody can see the view. It then reads nothing: the folder is stale, and read
    // when the view shows again.
    bool showing = true;
    bool stale;

    List<FileEntry> entries = [];
    List<FileEntry> displayed = [];
    int sortColumn;
    bool sortAscending = true;

    // How the entries are ordered now, so they are only sorted again when that changes.
    (int Column, bool Ascending) entriesSort;
    string filter = "";
    string? error;

    string? pendingSelection;
    readonly GutterMarquee marquee;

    /// <summary>
    /// Pins a folder to Quick access, or unpins it. Replaced in tests, which must not change the
    /// real list.
    /// </summary>
    internal Action<string, bool> SetPinned;

    /// <summary>
    /// The apps Windows recommends for an extension, for Open with. Replaced in tests, which
    /// must not depend on what is installed.
    /// </summary>
    internal Func<string, IReadOnlyList<OpenWith.App>> FindApps = OpenWith.Find;

    /// <summary>
    /// The selection rectangle dragged from the margin left of the list. For tests.
    /// </summary>
    internal GutterMarquee Marquee => marquee;

    /// <summary>
    /// Where a row is, in the list's coordinates. For tests.
    /// </summary>
    internal Rectangle RowBounds(int index) =>
        list.GetItemRect(index);

    // The rows the list has asked for lately, by where they are in what is displayed. The list
    // holds none of its own: it asks for a row whenever it draws it, measures it or reports on
    // it, a cell at a time, so the ones it asks for are kept. It only wants a screenful or so at
    // once, so however big the folder, few are ever made.
    readonly Dictionary<int, ListViewItem> rows = [];
    const int maximumRows = 512;

    // Stands in for a row that is asked for but not needed: one that has gone, or one of those
    // being selected again, which the list asks for only to say that it changed. A cell for every
    // column there could be, so whichever it is asked for is there.
    readonly ListViewItem blankRow = new(["", "", "", ""]);
    bool restoring;

    // The rows are to be made again even if the folder turns out not to have changed.
    bool rowsStale;

    /// <summary>
    /// How many rows are made at the moment. For tests.
    /// </summary>
    internal int RowsMade => rows.Count;

    string? pendingRename;
    bool editing;
    bool statusPending;

    public event Action? PathChanged;
    public event Action? StatusChanged;
    public event Action<string>? NewTabRequested;

    public string CurrentPath { get; private set; } = Locations.ThisPC;
    public string FreeSpaceText { get; private set; } = "";
    public bool CanGoBack => historyIndex > 0;
    public bool CanGoForward => historyIndex < history.Count - 1;
    public bool CanGoUp => Locations.Parent(CurrentPath) != null;

    /// <summary>
    /// The most recent listing of the folder. Completes once the list shows it.
    /// </summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// What the list is showing, in display order: sorted, and narrowed by the filter.
    /// </summary>
    public IReadOnlyList<FileEntry> Entries => displayed;

    bool IsThisPC => Locations.IsThisPC(CurrentPath);
    IntPtr Owner => FindForm()?.Handle ?? Handle;

    public FolderView(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        SetPinned = (path, pin) => ShellContextMenu.SetPinned(Owner, path, pin);

        // Explorer's margin left of the list, which a selection rectangle can be dragged from.
        Padding = new(LogicalToDeviceUnits(12), 0, 0, 0);
        BackColor = SystemColors.Window;
        marquee = new(this, list);
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.LabelEdit = true;
        list.AllowDrop = true;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;

        // The list holds no rows. It is told how many there are, and asks for each as it needs
        // it, so a folder of any size costs it only the rows on screen.
        list.VirtualMode = true;
        list.RetrieveVirtualItem += (_, eventArgs) => eventArgs.Item = Row(eventArgs.ItemIndex);
        list.SearchForVirtualItem += OnSearchForVirtualItem;
        list.ColumnFitRequested += SizeColumnToFit;
        UpdateColumns();

        list.ItemActivate += (_, _) => OpenEntries(SelectedEntries());
        list.ColumnClick += OnColumnClick;
        list.KeyDown += OnListKeyDown;
        list.SelectedIndexChanged += (_, _) => QueueStatusChanged();

        // A range selected at once, with Shift say, is reported this way instead.
        list.VirtualItemsSelectionRangeChanged += (_, _) => QueueStatusChanged();
        list.BeforeLabelEdit += OnBeforeLabelEdit;
        list.AfterLabelEdit += OnAfterLabelEdit;
        list.ContextMenuRequested += OnContextMenuRequested;
        list.HeaderContextMenuRequested += ShowHeaderMenu;
        list.ItemDrag += OnItemDrag;
        list.DragEnter += OnDragOver;
        list.DragOver += OnDragOver;
        list.DragDrop += OnDragDrop;
        Controls.Add(list);

        changes = new(400, OnChanged);
    }

    /// <summary>
    /// Whether anybody can see the view. A tab in the background, or in a window that is hidden
    /// or minimized, is told it is not showing, and then reads nothing: a folder opened in it, or
    /// changed, is read when it shows again.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Showing
    {
        get => showing;
        set
        {
            if (showing == value)
            {
                return;
            }

            showing = value;
            if (value && stale && !IsDisposed)
            {
                // The rows are still the folder's, a read behind, unless it was never read.
                ReadFolder(entries.Count > 0);
            }
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Filter
    {
        get => filter;
        set
        {
            var trimmed = value.Trim();
            if (trimmed == filter)
            {
                return;
            }

            filter = trimmed;
            Populate(true);
        }
    }

    public string StatusText => StatusFor(SelectedEntries());

    /// <summary>
    /// <see cref="StatusText"/> for a selection the caller has already read from the list, so it
    /// is read once for everything that follows a change of selection.
    /// </summary>
    public string StatusFor(IReadOnlyList<FileEntry> selection)
    {
        if (error != null)
        {
            return error;
        }

        var text = displayed.Count == 1 ? "1 item" : $"{displayed.Count:N0} items";
        if (filter.Length > 0)
        {
            text += $" (filtered from {entries.Count:N0})";
        }

        if (selection.Count == 0)
        {
            return text;
        }

        text += selection.Count == 1 ? "    1 item selected" : $"    {selection.Count:N0} items selected";

        // The size of the files selected. Not with a folder among them, whose size isn't known.
        long size = 0;
        foreach (var entry in selection)
        {
            if (entry.IsDirectory)
            {
                return text;
            }

            size += entry.Size;
        }

        return $"{text}  {Formatting.Size(size)}";
    }

    public void FocusList() =>
        list.Focus();

    public void Navigate(string target, bool record = true)
    {
        var normalized = Locations.Normalize(target);
        if (normalized == null)
        {
            return;
        }

        if (!Locations.IsThisPC(normalized) && !Directory.Exists(normalized))
        {
            if (File.Exists(normalized))
            {
                FileOperations.Open(this, normalized);
                return;
            }

            MessageBox.Show(
                this,
                $"Lode can't find '{target}'. Check the spelling and try again.",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (record)
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(normalized);
            historyIndex = history.Count - 1;
        }

        CurrentPath = normalized;
        filter = "";
        error = null;
        remembered = null;
        if (!showing)
        {
            // Read when the view shows. Until then it lists nothing, rather than the folder left.
            stale = true;
            Unwatch();
            Clear();
            FreeSpaceText = "";
            PathChanged?.Invoke();
            return;
        }

        stale = false;
        Watch();
        UpdateFreeSpace();
        PathChanged?.Invoke();
        Loading = LoadAsync(false);
    }

    // Watches the folder and reads it, for a view that has just shown.
    void ReadFolder(bool preserveState)
    {
        stale = false;
        Watch();
        UpdateFreeSpace();
        Loading = LoadAsync(preserveState);
    }

    // What was selected, which row had the focus and how far down the list was scrolled, when
    // the folder was let go, to put back once it has been read again.
    sealed record Kept(HashSet<string> Selected, string? Focused, int TopIndex);

    Kept? remembered;

    // The most selected items remembered for a folder let go. More than this is most of a big
    // folder, whose paths would be much of what letting it go saves.
    const int mostRemembered = 1000;

    /// <summary>
    /// Lets go of what the view holds of its folder: the entries, the rows and the watch on it.
    /// For a tab nobody will look at for a while, in a window gone to the tray. The folder is
    /// read again when the view next shows, and what was selected, and how far down it was
    /// scrolled, put back.
    /// </summary>
    public void Unload()
    {
        // Not while it is showing. And a folder not read yet, or with nothing in it, holds
        // nothing to let go.
        if (showing ||
            entries.Count == 0)
        {
            return;
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selection = SelectedEntries();
        if (selection.Count <= mostRemembered)
        {
            foreach (var entry in selection)
            {
                selected.Add(entry.FullPath);
            }
        }

        remembered = new(selected, (list.FocusedItem?.Tag as FileEntry)?.FullPath, list.TopItem?.Index ?? -1);
        stale = true;
        Unwatch();
        Clear();
    }

    // Empties the list, for a folder that is not read until the view shows.
    void Clear()
    {
        // Whatever is being read is the folder left.
        loadVersion++;
        loading = false;
        entries = [];
        ShowRows([]);
        QueueStatusChanged();
    }

    // Gives the list the rows to show. It holds none itself, so it is only told how many there
    // are. Emptied first, which drops the selection, the focus and how far down it was scrolled:
    // those were for the rows that were.
    void ShowRows(List<FileEntry> visible)
    {
        list.VirtualListSize = 0;
        rows.Clear();
        rowsStale = false;
        displayed = visible;
        list.VirtualListSize = visible.Count;
    }

    // The row the list asks for, made the first time and kept while it is likely to be asked for
    // again.
    ListViewItem Row(int index)
    {
        if (fitting != null)
        {
            return fitting;
        }

        if (restoring ||
            index >= displayed.Count)
        {
            return blankRow;
        }

        if (rows.TryGetValue(index, out var row))
        {
            return row;
        }

        // Scrolled a long way. What is on screen now is made again as it is asked for.
        if (rows.Count >= maximumRows)
        {
            rows.Clear();
        }

        row = CreateItem(displayed[index]);
        rows[index] = row;
        return row;
    }

    // Typing the start of a name moves to the next item with it, as it does in a list that
    // holds its own rows, which this one can't look through for itself.
    void OnSearchForVirtualItem(object? sender, SearchForVirtualItemEventArgs eventArgs)
    {
        if (eventArgs.IsTextSearch)
        {
            eventArgs.Index = FindRow(eventArgs.Text ?? "", eventArgs.StartIndex);
        }
    }

    /// <summary>
    /// The first row at or after <paramref name="start"/> whose name starts with
    /// <paramref name="text"/>, going round to the top after the last. -1 when there is none.
    /// </summary>
    internal int FindRow(string text, int start)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        var count = displayed.Count;
        for (var offset = 0; offset < count; offset++)
        {
            var index = (Math.Max(start, 0) + offset) % count;
            if (DisplayName(displayed[index]).StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    // Selects the rows for these paths again, and gives one the focus.
    void Restore(HashSet<string> selected, string? focused)
    {
        if (selected.Count == 0 &&
            focused == null)
        {
            return;
        }

        // The list keeps which rows are selected, which it can only do once it has a window.
        _ = list.Handle;
        var indexes = new List<int>();
        var focus = -1;
        for (var index = 0; index < displayed.Count; index++)
        {
            var path = displayed[index].FullPath;
            if (selected.Count > 0 &&
                selected.Contains(path))
            {
                indexes.Add(index);
            }

            if (string.Equals(path, focused, StringComparison.OrdinalIgnoreCase))
            {
                focus = index;
            }
        }

        restoring = true;
        try
        {
            if (indexes.Count > 0 &&
                indexes.Count == displayed.Count)
            {
                list.SelectAll();
            }
            else
            {
                foreach (var index in indexes)
                {
                    list.SelectedIndices.Add(index);
                }
            }
        }
        finally
        {
            restoring = false;
        }

        // One row, and the real one: it is what a screen reader is told has the focus.
        if (focus >= 0)
        {
            list.Items[focus].Focused = true;
        }
    }

    public void GoBack()
    {
        if (CanGoBack)
        {
            historyIndex--;
            Navigate(history[historyIndex], false);
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            historyIndex++;
            Navigate(history[historyIndex], false);
        }
    }

    public void GoUp()
    {
        var parent = Locations.Parent(CurrentPath);
        if (parent == null)
        {
            return;
        }

        // Land with the folder we just left selected, as Explorer does.
        pendingSelection = CurrentPath;
        Navigate(parent);
    }

    /// <summary>
    /// Opens the folder containing <paramref name="path"/> with that item selected.
    /// </summary>
    public void Reveal(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent == null)
        {
            Navigate(path);
            return;
        }

        pendingSelection = path;
        Navigate(parent);
    }

    /// <summary>
    /// As <see cref="Reload"/>, and the rows showing are made again even when the folder has not
    /// changed, for Refresh: what a row shows beyond the folder's own listing, a shortcut's icon
    /// say, may have.
    /// </summary>
    public void ReloadRows()
    {
        rowsStale = true;
        Reload();
    }

    public void Reload()
    {
        if (!showing)
        {
            stale = true;
            return;
        }

        UpdateFreeSpace();
        Loading = LoadAsync(true);
    }

    async Task LoadAsync(bool preserveState)
    {
        var version = ++loadVersion;
        var path = CurrentPath;
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        var column = sortColumn;
        var ascending = sortAscending;
        var started = Stopwatch.GetTimestamp();
        List<FileEntry> loaded;
        string? failure = null;
        loading = true;
        try
        {
            // Sorted there too, which leaves the UI thread only the rows to make.
            loaded = await Task.Run(
                () =>
                {
                    var found = Enumerate(path, showHidden, hiddenSystem);
                    found.Sort((first, second) => Compare(first, second, column, ascending));
                    return found;
                });
        }
        catch (Exception exception)
        {
            loaded = [];
            failure = exception.Message;
        }

        if (version != loadVersion || IsDisposed)
        {
            return;
        }

        loading = false;
        entries = loaded;
        entriesSort = (column, ascending);
        error = failure;
        Populate(preserveState);
        ReadTook(Stopwatch.GetElapsedTime(started));
    }

    // A folder that takes long to read is not read again the moment it changes, but only once
    // this many times as long as its last read took has gone by since. Otherwise a big folder
    // with a file being written in it is read over and over, each read starting as the last
    // ends. The first change after a quiet spell is still read at once, and so is a folder
    // quick to read, whose changes are already no more than an interval apart.
    const int readsApart = 10;

    // However long the read, a folder that keeps changing is read again this often.
    static readonly TimeSpan longestWait = TimeSpan.FromSeconds(10);

    // When the folder may next be read for a change, by Environment.TickCount64.
    long readAgainAt;

    /// <summary>
    /// How long after a read that took <paramref name="read"/> the folder is left before a
    /// change has it read again.
    /// </summary>
    internal static TimeSpan WaitAfter(TimeSpan read)
    {
        var wait = read * readsApart;
        if (wait > longestWait)
        {
            return longestWait;
        }

        return wait;
    }

    /// <summary>
    /// Notes how long reading the folder took, which sets how soon a change may have it read
    /// again. Tests call it to stand in for a folder slow to read.
    /// </summary>
    internal void ReadTook(TimeSpan read) =>
        readAgainAt = Environment.TickCount64 + (long)WaitAfter(read).TotalMilliseconds;

    static List<FileEntry> Enumerate(string path, bool showHidden, SystemItems? hiddenSystem)
    {
        var result = new List<FileEntry>();
        if (Locations.IsThisPC(path))
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                long total = 0;
                long free = 0;
                try
                {
                    if (drive.IsReady)
                    {
                        total = drive.TotalSize;
                        free = drive.AvailableFreeSpace;
                    }
                }
                catch (IOException)
                {
                    // The drive went away between listing and querying it.
                }

                result.Add(new(drive.Name, drive.Name, true, total, default, FileAttributes.Directory, IsDrive: true, FreeSpace: free));
            }

            return result;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? default : FileAttributes.Hidden
        };

        // Listing a folder we have no rights to yields nothing rather than throwing when
        // IgnoreInaccessible is set, so probe first to surface a real error message.
        _ = Directory.EnumerateFileSystemEntries(path).Any();

        // Made straight from what the file system reports, without a FileInfo or DirectoryInfo
        // for each on the way.
        var found = new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry entry) =>
            {
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                long size = 0;
                if (!isDirectory)
                {
                    size = entry.Length;
                }

                return new(entry.FileName.ToString(), entry.ToFullPath(), isDirectory, size, entry.LastWriteTimeUtc.LocalDateTime, entry.Attributes);
            },
            options);
        foreach (var entry in found)
        {
            if (hiddenSystem != null &&
                hiddenSystem.IsSystem(entry.FullPath, entry.Attributes))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    void Populate(bool preserveState)
    {
        // Before sorting: hiding the column the list is sorted by sorts it by name.
        var columnsChanged = UpdateColumns();
        if (entriesSort != (sortColumn, sortAscending))
        {
            var column = sortColumn;
            var ascending = sortAscending;
            entries.Sort((first, second) => Compare(first, second, column, ascending));
            entriesSort = (column, ascending);
        }

        // In order already, as the entries it is taken from are.
        var visible = ApplyFilter().ToList();

        if (preserveState && pendingRename == null && !columnsChanged && visible.SequenceEqual(displayed))
        {
            if (rowsStale)
            {
                rowsStale = false;
                rows.Clear();
                list.Invalidate();
            }

            list.SetSortArrow(SortArrowColumn, sortAscending);
            QueueStatusChanged();
            return;
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? focused = null;
        var topIndex = -1;
        if (preserveState)
        {
            foreach (var entry in SelectedEntries())
            {
                selected.Add(entry.FullPath);
            }

            focused = (list.FocusedItem?.Tag as FileEntry)?.FullPath;
            topIndex = list.TopItem?.Index ?? -1;
        }
        else if (remembered is { } kept)
        {
            // The folder was let go while nobody was looking, and has been read again.
            selected = kept.Selected;
            focused = kept.Focused;
            topIndex = kept.TopIndex;
        }

        remembered = null;

        var reveal = pendingSelection ?? pendingRename;
        if (reveal != null)
        {
            selected.Clear();
            selected.Add(reveal);
            focused = reveal;
            topIndex = -1;
        }

        pendingSelection = null;

        list.BeginUpdate();
        try
        {
            SetColumnText(dateColumn, IsThisPC ? "Free space" : "Date modified");
            SetColumnText(sizeColumn, IsThisPC ? "Total size" : "Size");
            ShowRows(visible);
            Restore(selected, focused);

            if (topIndex > 0 && visible.Count > 0)
            {
                list.TopItem = list.Items[Math.Min(topIndex, visible.Count - 1)];
            }
        }
        finally
        {
            list.EndUpdate();
        }

        list.SetSortArrow(SortArrowColumn, sortAscending);

        if (reveal != null)
        {
            list.FocusedItem?.EnsureVisible();
        }

        if (pendingRename != null)
        {
            var renameTarget = list.FocusedItem;
            var found = Locations.Same((renameTarget?.Tag as FileEntry)?.FullPath, pendingRename);
            pendingRename = null;
            if (found)
            {
                list.Focus();
                renameTarget!.BeginEdit();
            }
        }

        QueueStatusChanged();
    }

    void SetColumnText(int column, string text)
    {
        var shown = shownColumns.IndexOf(column);
        if (shown >= 0 &&
            list.Columns[shown].Text != text)
        {
            list.Columns[shown].Text = text;
        }
    }

    int SortArrowColumn => shownColumns.IndexOf(sortColumn);

    string ColumnTitle(int column)
    {
        var definition = columnDefinitions[column];
        if (IsThisPC)
        {
            return definition.ThisPCTitle;
        }

        return definition.Title;
    }

    bool IsColumnShown(int column) =>
        column == nameColumn ||
        !settings.HiddenColumns.Contains(columnDefinitions[column].Key, StringComparer.OrdinalIgnoreCase);

    // Brings the list's columns in line with the setting. True when they changed, in which case
    // every row needs building again, since a row holds a cell per column shown.
    bool UpdateColumns()
    {
        var wanted = Enumerable.Range(0, columnDefinitions.Length)
            .Where(IsColumnShown)
            .ToList();
        if (wanted.SequenceEqual(shownColumns))
        {
            return false;
        }

        for (var index = 0; index < shownColumns.Count; index++)
        {
            columnWidths[shownColumns[index]] = list.Columns[index].Width;
        }

        // The list can ask for a row at any time, and a row must have a cell for every column the
        // list has. So the columns go first, then the rows made for them are forgotten and made
        // from here on for the columns wanted, and only then are those added.
        list.Columns.Clear();
        shownColumns = wanted;
        rows.Clear();
        foreach (var column in wanted)
        {
            var definition = columnDefinitions[column];
            if (!columnWidths.TryGetValue(column, out var width))
            {
                width = LogicalToDeviceUnits(definition.Width);
            }

            list.Columns.Add(ColumnTitle(column), width, definition.Alignment);
        }

        if (!shownColumns.Contains(sortColumn))
        {
            sortColumn = nameColumn;
            sortAscending = true;
        }

        return true;
    }

    /// <summary>
    /// The titles of the columns shown, in order. For tests.
    /// </summary>
    internal IEnumerable<string> ColumnTitles =>
        list.Columns.Cast<ColumnHeader>().Select(_ => _.Text);

    /// <summary>
    /// The first row's cells, one per column shown. For tests.
    /// </summary>
    internal IEnumerable<string> FirstRowCells
    {
        get
        {
            if (displayed.Count == 0)
            {
                return [];
            }

            return Row(0).SubItems
                .Cast<ListViewItem.ListViewSubItem>()
                .Select(_ => _.Text)
                .ToList();
        }
    }

    // A row's cells for the columns shown, from one for every column.
    string[] ShownCells(string[] cells)
    {
        // Every column, which are in order: the cells as they are.
        if (shownColumns.Count == cells.Length)
        {
            return cells;
        }

        var shown = new string[shownColumns.Count];
        for (var index = 0; index < shown.Length; index++)
        {
            shown[index] = cells[shownColumns[index]];
        }

        return shown;
    }

    /// <summary>
    /// Shows or hides a column in every view. Name cannot be hidden.
    /// </summary>
    internal void SetColumnShown(int column, bool shown)
    {
        if (column == nameColumn ||
            IsColumnShown(column) == shown)
        {
            return;
        }

        var key = columnDefinitions[column].Key;
        settings.HiddenColumns.RemoveAll(_ => string.Equals(_, key, StringComparison.OrdinalIgnoreCase));
        if (!shown)
        {
            settings.HiddenColumns.Add(key);
        }

        RefreshColumns();
        ColumnsChanged?.Invoke();
    }

    /// <summary>
    /// Applies the hidden columns setting, rebuilding the rows from what is already listed.
    /// </summary>
    public void RefreshColumns()
    {
        // A folder not read yet has no rows to rebuild, and may have an item waiting to be
        // selected once it is. Its columns are brought in line when it is read.
        if (stale && entries.Count == 0)
        {
            return;
        }

        Populate(true);
    }

    /// <summary>
    /// The header's right-click menu, as Explorer's: size the column clicked, or all of them,
    /// to fit, then a tick for each column, to hide or show it. Name cannot be hidden.
    /// </summary>
    internal ContextMenuStrip HeaderMenu(int clickedColumn)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(
            new ToolStripMenuItem("Size column to fit", null, (_, _) => SizeColumnToFit(clickedColumn))
            {
                Enabled = clickedColumn >= 0
            });
        menu.Items.Add(
            "Size all columns to fit",
            null,
            (_, _) =>
            {
                for (var index = 0; index < shownColumns.Count; index++)
                {
                    SizeColumnToFit(index);
                }
            });
        menu.Items.Add(new ToolStripSeparator());
        for (var column = 0; column < columnDefinitions.Length; column++)
        {
            var current = column;
            var shown = IsColumnShown(column);
            menu.Items.Add(
                new ToolStripMenuItem(ColumnTitle(column), null, (_, _) => SetColumnShown(current, !shown))
                {
                    Checked = shown,
                    Enabled = column != nameColumn
                });
        }

        return menu;
    }

    void ShowHeaderMenu(Point screen)
    {
        var menu = HeaderMenu(list.HeaderColumnAt(screen));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(list, list.PointToClient(screen));
    }

    // While the list measures a column: the row it is given, whichever it asks for.
    ListViewItem? fitting;

    /// <summary>
    /// Sizes a column to fit its header and the widest of its cells in the whole folder, as
    /// Explorer's Size Column to Fit does. The list measures the rows it asks for, which are only
    /// those on screen. So the widest cell is found here, and for as long as the list is
    /// measuring, every row it asks for has that in the column.
    /// </summary>
    void SizeColumnToFit(int shown)
    {
        var cells = new string[shownColumns.Count];
        Array.Fill(cells, "");
        cells[shown] = WidestCell(shownColumns[shown]);
        fitting = new(cells);
        try
        {
            list.SizeColumnToFit(shown);
        }
        finally
        {
            fitting = null;

            // Anything drawn meanwhile showed the row being measured.
            list.Invalidate();
        }
    }

    // The text of the widest cell a column has, in any row.
    string WidestCell(int column)
    {
        var widest = "";
        var widestWidth = 0;

        // Dates, types and sizes repeat. Each is measured once.
        var measured = new HashSet<string>();
        foreach (var entry in displayed)
        {
            var text = entry.IsDrive ? DescribeDrive(entry).Cells[column] : Cell(entry, column);
            if (!measured.Add(text))
            {
                continue;
            }

            var width = list.TextWidth(text);
            if (width > widestWidth)
            {
                widestWidth = width;
                widest = text;
            }
        }

        return widest;
    }

    /// <summary>
    /// How wide a column is, by where it is among those shown. For tests.
    /// </summary>
    internal int ColumnWidth(int shown) =>
        list.Columns[shown].Width;

    // A drive's cells, one for every column, and its icon, as the shell describes it.
    static (string[] Cells, int Icon) DescribeDrive(FileEntry entry)
    {
        var (displayName, typeName, icon) = ShellIcons.Describe(entry.FullPath);
        string[] cells =
        [
            displayName,
            entry.Size > 0 ? Formatting.Size(entry.FreeSpace) : "",
            typeName,
            entry.Size > 0 ? Formatting.Size(entry.Size) : ""
        ];
        return (cells, icon);
    }

    // What a file or folder shows in a column.
    static string Cell(FileEntry entry, int column)
    {
        switch (column)
        {
            case nameColumn:
                return DisplayName(entry);
            case dateColumn:
                return entry.Modified.ToString("g");
            case typeColumn:
                return ShellIcons.GetTypeName(entry.FullPath, entry.IsDirectory);
        }

        if (entry.IsDirectory)
        {
            return "";
        }

        return Formatting.Kilobytes(entry.Size);
    }

    ListViewItem CreateItem(FileEntry entry)
    {
        if (entry.IsDrive)
        {
            var (driveCells, driveIcon) = DescribeDrive(entry);
            return new(ShownCells(driveCells), ShellListView.PaddedIcon(driveIcon)) { Tag = entry };
        }

        string[] cells =
        [
            Cell(entry, nameColumn),
            Cell(entry, dateColumn),
            Cell(entry, typeColumn),
            Cell(entry, sizeColumn)
        ];
        var icon = ShellListView.PaddedIcon(ShellIcons.GetIcon(entry.FullPath, entry.IsDirectory, entry.Modified));
        var item = new ListViewItem(ShownCells(cells), icon) { Tag = entry };
        if (entry.IsHidden)
        {
            item.ForeColor = SystemColors.GrayText;
        }

        return item;
    }

    IEnumerable<FileEntry> ApplyFilter()
    {
        if (filter.Length == 0)
        {
            return entries;
        }

        if (filter.Contains('*') || filter.Contains('?'))
        {
            return entries.Where(_ => FileSystemName.MatchesSimpleExpression(filter, _.Name));
        }

        return entries.Where(_ => _.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    // Static, with the column and direction handed in, so a folder can be sorted off the UI
    // thread as it is read.
    static int Compare(FileEntry first, FileEntry second, int column, bool ascending)
    {
        // Folders stay grouped above files whichever way the column is sorted.
        if (first.IsDirectory != second.IsDirectory)
        {
            if (first.IsDirectory)
            {
                return -1;
            }

            return 1;
        }

        var result = column switch
        {
            dateColumn => first.IsDrive
                ? first.FreeSpace.CompareTo(second.FreeSpace)
                : first.Modified.CompareTo(second.Modified),
            typeColumn => string.Compare(
                ShellIcons.GetTypeName(first.FullPath, first.IsDirectory),
                ShellIcons.GetTypeName(second.FullPath, second.IsDirectory),
                StringComparison.CurrentCultureIgnoreCase),
            sizeColumn => first.Size.CompareTo(second.Size),
            _ => 0
        };

        if (result == 0)
        {
            result = StrCmpLogicalW(first.Name, second.Name);
        }

        if (ascending)
        {
            return result;
        }

        return -result;
    }

    // By index into what is displayed: the list holds which rows are selected, not the rows.
    public List<FileEntry> SelectedEntries()
    {
        var indexes = list.SelectedIndices;
        var selection = new List<FileEntry>(indexes.Count);
        foreach (int index in indexes)
        {
            if (index < displayed.Count)
            {
                selection.Add(displayed[index]);
            }
        }

        return selection;
    }

    string[] SelectedPaths() =>
        SelectedEntries()
            .Where(_ => !_.IsDrive)
            .Select(_ => _.FullPath)
            .ToArray();

    void OpenEntries(IReadOnlyList<FileEntry> selection)
    {
        var navigated = false;
        foreach (var entry in selection)
        {
            var folder = entry.IsDirectory ? entry.FullPath : FileOperations.ResolveFolderShortcut(entry.FullPath);
            if (folder == null)
            {
                FileOperations.Open(this, entry.FullPath);
            }
            else if (navigated)
            {
                NewTabRequested?.Invoke(folder);
            }
            else
            {
                navigated = true;
                Navigate(folder);
            }
        }
    }

    void OnColumnClick(object? sender, ColumnClickEventArgs eventArgs) =>
        SortBy(shownColumns[eventArgs.Column]);

    /// <summary>
    /// Sorts by a column, as clicking its header does: ascending first, then toggling.
    /// </summary>
    public void SortBy(int column)
    {
        if (column == sortColumn)
        {
            sortAscending = !sortAscending;
        }
        else
        {
            sortColumn = column;
            sortAscending = true;
        }

        Populate(true);
    }

    void OnListKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyData)
        {
            case Keys.Back:
                GoBack();
                break;
            case Keys.Delete:
                Delete(false);
                break;
            case Keys.Shift | Keys.Delete:
                Delete(true);
                break;
            case Keys.F2:
                BeginRename();
                break;
            case Keys.Control | Keys.A:
                list.SelectAll();
                break;
            case Keys.Control | Keys.C:
                Copy(false);
                break;
            case Keys.Control | Keys.X:
                Copy(true);
                break;
            case Keys.Control | Keys.V:
                Paste();
                break;
            case Keys.Control | Keys.Shift | Keys.N:
                CreateFolder();
                break;
            case Keys.Alt | Keys.Enter:
                ShowProperties();
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    public void Copy(bool cut) =>
        FileOperations.CopyToClipboard(SelectedPaths(), cut);

    public void Paste()
    {
        if (IsThisPC)
        {
            return;
        }

        var clipboard = FileOperations.ReadClipboard();
        if (clipboard == null)
        {
            return;
        }

        FileOperations.Transfer(Owner, clipboard.Paths, CurrentPath, clipboard.Move);
        if (clipboard.Move)
        {
            Clipboard.Clear();
        }
    }

    public void Delete(bool permanent) =>
        FileOperations.Delete(Owner, SelectedPaths(), permanent);

    public void BeginRename()
    {
        if (list.FocusedItem is { Selected: true } item)
        {
            item.BeginEdit();
        }
    }

    public void CreateFolder() =>
        Create(FileOperations.CreateFolder);

    public void CreateFile(ShellNew.Entry entry) =>
        Create(_ => FileOperations.CreateFile(_, entry));

    // One shortcut beside each item, the last of them selected once the list shows it.
    void CreateShortcuts(List<FileEntry> selection)
    {
        try
        {
            foreach (var entry in selection)
            {
                pendingSelection = FileOperations.CreateShortcut(entry.FullPath);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Loading = LoadAsync(true);
    }

    // Makes the item, then shows it with its name ready to edit.
    void Create(Func<string, string> create)
    {
        if (IsThisPC)
        {
            return;
        }

        try
        {
            pendingRename = create(CurrentPath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Loading = LoadAsync(true);
    }

    public void ShowProperties()
    {
        var selection = SelectedEntries();
        if (selection.Count > 0)
        {
            ShellContextMenu.InvokeVerb(Owner, selection.Select(_ => _.FullPath).ToArray(), "properties");
        }
        else if (!IsThisPC)
        {
            ShellContextMenu.InvokeVerb(Owner, [CurrentPath], "properties");
        }
    }

    void OnBeforeLabelEdit(object? sender, LabelEditEventArgs eventArgs)
    {
        if (eventArgs.Item >= displayed.Count ||
            displayed[eventArgs.Item].IsDrive)
        {
            eventArgs.CancelEdit = true;
            return;
        }

        var entry = displayed[eventArgs.Item];

        editing = true;
        if (!entry.IsDirectory)
        {
            // Preselect the name but not the extension, so typing keeps the file type.
            list.SelectEditText(Path.GetFileNameWithoutExtension(DisplayName(entry).AsSpan()).Length);
        }
    }

    // Shortcut extensions are hidden, matching Explorer's NeverShowExt behaviour.
    static readonly string[] hiddenExtensions = [".lnk", ".url"];

    static string? HiddenExtension(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return null;
        }

        // A loop rather than a query: this runs for every row, and a query would allocate for each.
        foreach (var extension in hiddenExtensions)
        {
            if (entry.Name.Length > extension.Length &&
                entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return null;
    }

    internal static string DisplayName(FileEntry entry)
    {
        var extension = HiddenExtension(entry);
        if (extension == null)
        {
            return entry.Name;
        }

        return entry.Name[..^extension.Length];
    }

    internal void OnAfterLabelEdit(object? sender, LabelEditEventArgs eventArgs)
    {
        editing = false;
        var index = eventArgs.Item;

        // The folder was read again under the edit, and the row is no longer there.
        if (index >= displayed.Count)
        {
            eventArgs.CancelEdit = true;
            return;
        }

        var entry = displayed[index];
        var newName = eventArgs.Label?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == DisplayName(entry))
        {
            eventArgs.CancelEdit = true;
            return;
        }

        var hiddenExtension = HiddenExtension(entry);
        if (hiddenExtension != null)
        {
            newName += entry.Name[^hiddenExtension.Length..];
        }

        if (newName == entry.Name)
        {
            eventArgs.CancelEdit = true;
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            eventArgs.CancelEdit = true;
            MessageBox.Show(
                this,
                "A file name can't contain any of these characters:  \\ / : * ? \" < > |",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!FileOperations.Rename(Owner, entry.FullPath, newName))
        {
            eventArgs.CancelEdit = true;
            return;
        }

        // The list asks for the row again now the edit is over, so what is displayed takes the
        // new name, and the row made for the old one is forgotten, until the folder is read again.
        var renamed = entry with
        {
            Name = newName,
            FullPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName)
        };
        displayed[index] = renamed;
        var position = entries.IndexOf(entry);
        if (position >= 0)
        {
            entries[position] = renamed;
        }

        rows.Remove(index);
        changes.Raise();
    }

    void OnContextMenuRequested(Point location, bool fromKeyboard)
    {
        var selection = SelectedEntries();
        if (fromKeyboard)
        {
            var anchor = list.FocusedItem is { Selected: true } focused
                ? new Point(focused.Bounds.Left + 24, focused.Bounds.Bottom)
                : new Point(8, 8);
            location = list.PointToScreen(anchor);
        }

        if (selection.Count > 0)
        {
            ShowQuickItemMenu(selection, location);
        }
        else if (!IsThisPC)
        {
            ShowQuickBackgroundMenu(location);
        }
    }

    void ShowQuickItemMenu(List<FileEntry> selection, Point location) =>
        QuickMenu.Show(
            list,
            location,
            LodeActions.ForMenu(ItemCommands(selection), settings),
            () =>
            {
                using var menu = new ShellContextMenu();
                ShowItemMenu(menu, selection, location);
            },
            settings.ShowsInMenu(LodeActions.More));

    /// <summary>
    /// The fixed right-click menu for selected items. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> ItemCommands(List<FileEntry> selection)
    {
        // Worked out in one pass: the actions bar asks for these each time the selection changes.
        var anyDrive = false;
        var allFolders = true;
        var allFiles = true;
        foreach (var entry in selection)
        {
            anyDrive |= entry.IsDrive;
            if (entry.IsDirectory)
            {
                allFiles = false;
            }
            else
            {
                allFolders = false;
            }
        }

        var commands = new List<QuickMenu.Command?>
        {
            LodeActions.Command("open", () => OpenEntries(selection))
        };
        if (allFolders)
        {
            commands.Add(LodeActions.Command("openInNewTab", () => OpenInNewTabs(selection)));
        }

        if (selection is [{ IsDirectory: true } terminalFolder])
        {
            commands.Add(LodeActions.Command("terminal", () => FileOperations.OpenTerminal(terminalFolder.FullPath)));
            commands.Add(LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(terminalFolder.FullPath, admin: true)));
        }

        if (allFolders)
        {
            commands.Add(LodeActions.Pin(selection.Select(_ => _.FullPath).ToList(), places, SetPinned));
        }

        if (allFiles &&
            selection.All(_ => TextEditors.IsText(_.FullPath)))
        {
            commands.Add(LodeActions.Command("edit", () => TextEditors.Open(this, settings, selection.Select(_ => _.FullPath).ToList())));
        }

        if (allFiles)
        {
            var paths = selection.Select(_ => _.FullPath).ToList();
            if (OpenWith.Extension(paths) is { } extension)
            {
                commands.Add(LodeActions.Submenu("openWith", QuickMenu.Deferred(() => OpenWithCommands(extension, paths))));
            }
        }

        commands.AddRange(
        [
            null,
            LodeActions.Command("cut", () => Copy(true), !anyDrive),
            LodeActions.Command("copy", () => Copy(false), !anyDrive),
            LodeActions.Command("rename", BeginRename, selection.Count == 1 && !anyDrive),
            LodeActions.Command("delete", () => Delete(false), !anyDrive),
            null,
            LodeActions.Command("createShortcut", () => CreateShortcuts(selection), !anyDrive),
            LodeActions.Command("copyAsPath", () => CopyAsPath(selection.Select(_ => _.FullPath))),
            LodeActions.Command("properties", ShowProperties)
        ]);
        if (selection is [{ IsDirectory: true } folder])
        {
            commands.AddRange(CodeMenu.Commands(this, settings, folder.FullPath));
        }

        return commands;
    }

    // The apps Windows recommends for the files, each with its icon, then its dialog for
    // choosing another, which takes one file.
    List<QuickMenu.Command?> OpenWithCommands(string extension, List<string> paths)
    {
        var commands = new List<QuickMenu.Command?>();
        foreach (var app in FindApps(extension))
        {
            commands.Add(
                new(app.Name, () => OpenWith.Open(this, app, paths))
                {
                    ShellIcon = app.Icon
                });
        }

        if (paths is [var path])
        {
            commands.Add(new("Choose another app", () => OpenWith.Choose(Owner, path)));
        }

        return commands;
    }

    void ShowQuickBackgroundMenu(Point location) =>
        QuickMenu.Show(
            list,
            location,
            LodeActions.ForMenu(BackgroundCommands(FileOperations.ReadClipboard() != null), settings),
            () =>
            {
                using var menu = new ShellContextMenu();
                ShowBackgroundMenu(menu, location);
            },
            settings.ShowsInMenu(LodeActions.More));

    /// <summary>
    /// The full Windows menu, as Show more options opens it, for the selection, or for the
    /// folder when nothing is selected, at a point on the screen.
    /// </summary>
    public void ShowShellMenu(Point location)
    {
        var selection = SelectedEntries();
        using var menu = new ShellContextMenu();
        if (selection.Count > 0)
        {
            ShowItemMenu(menu, selection, location);
            return;
        }

        if (!IsThisPC)
        {
            ShowBackgroundMenu(menu, location);
        }
    }

    /// <summary>
    /// Whether there is anything for <see cref="ShowShellMenu"/> to show a menu for.
    /// </summary>
    public bool CanShowShellMenu =>
        list.SelectedIndices.Count > 0 || !IsThisPC;

    /// <summary>
    /// The fixed right-click menu for the empty part of the list. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> BackgroundCommands(bool canPaste, IReadOnlyList<ShellNew.Entry>? newTypes = null) =>
    [
        LodeActions.Submenu("new", NewCommands(newTypes ?? ShellNew.Current)),
        LodeActions.Command("paste", Paste, canPaste),
        .. UndoCommands(),
        null,
        LodeActions.Command("terminal", () => FileOperations.OpenTerminal(CurrentPath)),
        LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(CurrentPath, admin: true)),
        LodeActions.Command("copyAsPath", () => CopyAsPath([CurrentPath])),
        LodeActions.Command("properties", ShowProperties),
        .. CodeMenu.Commands(this, settings, CurrentPath)
    ];

    /// <summary>
    /// Undo and Redo, worded for what they would do ("Undo rename"), for the history every
    /// window shares.
    /// </summary>
    internal List<QuickMenu.Command?> UndoCommands()
    {
        var history = UndoHistory.Instance;
        return
        [
            LodeActions.Command("undo", Undo, history.CanUndo, history.UndoText),
            LodeActions.Command("redo", Redo, history.CanRedo, history.RedoText)
        ];
    }

    public void Undo() =>
        RunUndo(UndoHistory.Instance.Undo);

    public void Redo() =>
        RunUndo(UndoHistory.Instance.Redo);

    async void RunUndo(Func<IntPtr, Task<string?>> step)
    {
        var failure = await step(Owner);
        if (failure != null &&
            !IsDisposed)
        {
            MessageBox.Show(this, failure, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Folder, then each type Windows offers under New, by label.
    List<QuickMenu.Command?> NewCommands(IReadOnlyList<ShellNew.Entry> types) =>
    [
        new("Folder", CreateFolder)
        {
            Glyph = Icons.Folder
        },
        null,
        .. NewItems(types)
            .Select(_ => new QuickMenu.Command(_.Label, () => CreateFile(_.Type))
            {
                ShellIcon = _.Icon
            })
    ];

    /// <summary>
    /// A type under New: its label, and the type's own icon, as Explorer's New menu shows.
    /// </summary>
    sealed record NewItem(ShellNew.Entry Type, string Label, int Icon);

    sealed record NewMenu(IReadOnlyList<ShellNew.Entry> Types, List<NewItem> Items);

    // The last types asked for, as the New menu lists them. The machine's own are read once, and
    // the actions bar asks for the menu each time the selection changes, so their labels, icons
    // and order are worked out once too.
    static NewMenu? newMenu;

    static List<NewItem> NewItems(IReadOnlyList<ShellNew.Entry> types)
    {
        if (newMenu is { } known &&
            ReferenceEquals(known.Types, types))
        {
            return known.Items;
        }

        var items = types
            .Select(_ => new NewItem(_, NewLabel(_.TypeName), ShellIcons.GetIcon($"x{_.Extension}", false)))
            .OrderBy(_ => _.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        newMenu = new(types, items);
        return items;
    }

    // "Microsoft Word Document" reads as "Word Document". Only the label: the file is still
    // named as Explorer names it.
    internal static string NewLabel(string typeName)
    {
        const string prefix = "Microsoft ";
        if (typeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            typeName.Length > prefix.Length)
        {
            return typeName[prefix.Length..];
        }

        return typeName;
    }

    void OpenInNewTabs(List<FileEntry> selection)
    {
        foreach (var entry in selection)
        {
            NewTabRequested?.Invoke(entry.FullPath);
        }
    }

    internal static void CopyAsPath(IEnumerable<string> paths) =>
        Clipboard.SetText(PathsText(paths));

    // As Explorer's Copy as path puts them: quoted, one per line.
    internal static string PathsText(IEnumerable<string> paths) =>
        string.Join(Environment.NewLine, paths.Select(_ => $"\"{_}\""));

    void ShowItemMenu(ShellContextMenu menu, List<FileEntry> selection, Point location)
    {
        var customCommands = new List<ShellContextMenu.CustomCommand>();
        if (selection.All(_ => _.IsDirectory))
        {
            customCommands.Add(
                new(
                    "Open in new tab",
                    () =>
                    {
                        foreach (var entry in selection)
                        {
                            NewTabRequested?.Invoke(entry.FullPath);
                        }
                    }));
        }

        var paths = selection.Select(_ => _.FullPath).ToArray();
        menu.ShowForItems(Owner, paths, location, customCommands, _ => HandleItemVerb(_, selection));
    }

    // The shell would run these in Explorer; keep them inside Lode instead.
    bool HandleItemVerb(string verb, List<FileEntry> selection)
    {
        if (verb.Equals("open", StringComparison.OrdinalIgnoreCase) && selection.All(_ => _.IsDirectory))
        {
            OpenEntries(selection);
            return true;
        }

        if (verb.Equals("rename", StringComparison.OrdinalIgnoreCase))
        {
            BeginRename();
            return true;
        }

        return false;
    }

    void ShowBackgroundMenu(ShellContextMenu menu, Point location)
    {
        ShellContextMenu.CustomCommand[] customCommands =
        [
            new("Refresh", Reload),
            new("New folder", CreateFolder)
        ];

        var shown = menu.ShowForFolder(Owner, CurrentPath, location, customCommands, HandleBackgroundVerb);
        if (shown)
        {
            return;
        }

        // The shell has no background menu for this folder; offer the basics ourselves.
        var fallback = new ContextMenuStrip();
        fallback.Items.Add("Refresh", null, (_, _) => Reload());
        fallback.Items.Add("New folder", null, (_, _) => CreateFolder());
        fallback.Items.Add("Paste", null, (_, _) => Paste());
        fallback.Items.Add("Properties", null, (_, _) => ShowProperties());
        fallback.Closed += (_, _) => BeginInvoke(fallback.Dispose);
        fallback.Show(location);
    }

    bool HandleBackgroundVerb(string verb)
    {
        if (verb.Equals("paste", StringComparison.OrdinalIgnoreCase))
        {
            Paste();
            return true;
        }

        return false;
    }

    void OnItemDrag(object? sender, ItemDragEventArgs eventArgs)
    {
        var paths = SelectedPaths();
        if (paths.Length == 0)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, paths);
        list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    string? DropDestination(DragEventArgs eventArgs)
    {
        var point = list.PointToClient(new(eventArgs.X, eventArgs.Y));
        if (list.GetItemAt(point.X, point.Y)?.Tag is FileEntry { IsDirectory: true } target)
        {
            return target.FullPath;
        }

        return IsThisPC ? null : CurrentPath;
    }

    void OnDragOver(object? sender, DragEventArgs eventArgs) =>
        eventArgs.Effect = FileOperations.GetDropEffect(eventArgs, DropDestination(eventArgs));

    void OnDragDrop(object? sender, DragEventArgs eventArgs) =>
        FileOperations.Drop(Owner, eventArgs, DropDestination(eventArgs));

    void Watch()
    {
        Unwatch();
        changes.Reset();
        if (IsThisPC)
        {
            return;
        }

        try
        {
            watcher = new(
                CurrentPath,
                NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                changes.Raise);
        }
        catch (Exception)
        {
            // Some locations (certain network shares) can't be watched; F5 still works.
            watcher = null;
        }
    }

    void Unwatch()
    {
        watcher?.Dispose();
        watcher = null;
    }

    // The folder changed. False while reading it again has to wait: for the read under way, or
    // for the name being edited.
    bool OnChanged()
    {
        if (loading || editing)
        {
            return false;
        }

        if (!showing)
        {
            // Read when the view shows. Nothing more to learn by watching until then.
            stale = true;
            Unwatch();
            return true;
        }

        // Too soon after the last read, for how long that took. Looked at again next interval.
        if (Environment.TickCount64 < readAgainAt)
        {
            return false;
        }

        Reload();
        return true;
    }

    void UpdateFreeSpace() =>
        FreeSpaceText = IsThisPC ? "" : places.FreeSpace(CurrentPath);

    // Selecting a range raises one event per item; coalesce them into a single update.
    void QueueStatusChanged()
    {
        if (statusPending)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            StatusChanged?.Invoke();
            return;
        }

        statusPending = true;
        BeginInvoke(() =>
        {
            statusPending = false;
            StatusChanged?.Invoke();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            changes.Dispose();
            watcher?.Dispose();
            marquee.Dispose();
        }

        base.Dispose(disposing);
    }
}
