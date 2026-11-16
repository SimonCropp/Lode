/// <summary>
/// One tab's worth of file browsing: the file list, its navigation history, and the file
/// operations that act on the selection.
/// </summary>
sealed class FolderView : UserControl
{
    const int nameColumn = 0;
    const int folderColumn = 1;
    const int dateColumn = 2;
    const int typeColumn = 3;
    const int sizeColumn = 4;

    // The list's columns, in order, by the index sorting and the cells use. Name is always
    // shown; the others can be hidden from the header's right-click menu, in every view at once.
    // On This PC, Date modified and Size show each drive's free space and total size. Folder,
    // where each item is, only a search has, whose items come from all through the folder.
    sealed record ColumnDefinition(string Key, string Title, string ThisPCTitle, int Width, HorizontalAlignment Alignment);

    static readonly ColumnDefinition[] columnDefinitions =
    [
        new("Name", "Name", "Name", 380, HorizontalAlignment.Left),
        new("Folder", "Folder", "Folder", 220, HorizontalAlignment.Left),
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

    // How long the filter is left as it is before the subfolders are searched for it, so that
    // typing a name searches once, for all of it, rather than once for each letter.
    const int searchDelay = 300;

    // How often what the search has found so far is added to the list.
    static readonly TimeSpan searchBatch = TimeSpan.FromMilliseconds(250);

    // What the search of the subfolders has found: everything beneath them whose name the filter
    // matches, ordered as the entries are. The folder's own matches are taken from its entries,
    // which are kept up to date as it changes; these are as they were when found.
    List<FileEntry> found = [];
    (int Column, bool Ascending) foundSort;
    CancelSource? search;
    bool searching;

    // The search is to start again once the view shows: it was let go, or asked for again, while
    // nobody could see the view.
    bool searchStale;

    // Found while a name was being edited, which showing it would have ended.
    bool foundPending;

    /// <summary>
    /// The search of the subfolders under way. Completes once the list shows all it found.
    /// </summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    // Whether the filter searches the subfolders too, as it does everywhere but This PC and a
    // machine on the network.
    bool IsSearch => filter.Length > 0 && !ListsPlaces;

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

    // The list's horizontal scrollbar, run across the margin too.
    readonly GutterScrollBar scrollBar;

    /// <summary>
    /// The horizontal scrollbar. For tests.
    /// </summary>
    internal ScrollBar ScrollBar => scrollBar;

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
    readonly ListViewItem blankRow = new(["", "", "", "", ""]);
    bool restoring;

    // The rows are to be made again even if the folder turns out not to have changed.
    bool rowsStale;

    /// <summary>
    /// How many rows are made at the moment. For tests.
    /// </summary>
    internal int RowsMade => rows.Count;

    /// <summary>
    /// What the list says while it has no rows. For tests.
    /// </summary>
    internal string? EmptyText => list.EmptyText;

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

    /// <summary>
    /// This PC, listing drives, or a machine on the network, listing its shares: places to go,
    /// not a folder that holds anything, so nothing can be made, pasted or dropped there.
    /// </summary>
    public bool ListsPlaces => IsThisPC || Locations.IsServer(CurrentPath);
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

        // Over the list's own scrollbar, so in front of it.
        scrollBar = new(list);
        Controls.Add(scrollBar);
        scrollBar.BringToFront();

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
            if (!value || IsDisposed)
            {
                return;
            }

            if (stale)
            {
                // The rows are still the folder's, a read behind, unless it was never read.
                var preserveState = entries.Count > 0;
                if (IsRemote(CurrentPath))
                {
                    Loading = FindThenRead(preserveState);
                }
                else
                {
                    ReadFolder(preserveState);
                }
            }
            else if (searchStale)
            {
                SearchAgain();
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

            // What the folder itself has that matches shows at once; what is beneath its
            // subfolders, once they are searched.
            StopSearch();
            Populate(true);
            SearchAgain();
        }
    }

    public string StatusText => StatusFor(SelectedEntries());

    /// <summary>
    /// <see cref="StatusText"/> for a selection the caller has already read from the list, so it
    /// is read once for everything that follows a change of selection.
    /// </summary>
    public string StatusFor(IReadOnlyList<FileEntry> selection)
    {
        if (looking != null)
        {
            return $"Looking for {looking}...";
        }

        if (error != null)
        {
            return error;
        }

        var text = displayed.Count == 1 ? "1 item" : $"{displayed.Count:N0} items";
        if (IsSearch)
        {
            text += searching ? " found so far, searching subfolders" : " found";
        }
        else if (filter.Length > 0)
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

    /// <summary>
    /// Whether somewhere is on the network, where asking if it is there can take a long time.
    /// Replaced in tests, to send a folder on this machine the way one on the network goes.
    /// </summary>
    internal Func<string, bool> IsRemote = Locations.IsRemote;

    // Somewhere on the network being asked after, off the UI thread, and which asking that is:
    // one overtaken, by going somewhere else meanwhile, is not acted on.
    string? looking;
    int finding;

    // The view was started at what may be a file, taken on trust. Found to be one, its folder is
    // shown with it selected.
    bool revealIfFile;

    enum Found
    {
        Folder,
        File,
        Nothing
    }

    static Found Find(string path)
    {
        if (Locations.IsServer(path))
        {
            if (Shares.Answers(path))
            {
                return Found.Folder;
            }

            return Found.Nothing;
        }

        if (Directory.Exists(path))
        {
            return Found.Folder;
        }

        if (File.Exists(path))
        {
            return Found.File;
        }

        return Found.Nothing;
    }

    public void Navigate(string target, bool record = true) =>
        Go(target, record, revealFile: false);

    /// <summary>
    /// Where a tab starts: a folder, or a file, which means "show me this file", so its folder
    /// with the file selected. Somewhere on the network is taken on trust until the view shows,
    /// and asked after then, off the UI thread.
    /// </summary>
    public void OpenAt(string path)
    {
        if (IsRemote(path))
        {
            Go(path, record: true, revealFile: true);
            return;
        }

        if (File.Exists(path))
        {
            Reveal(path);
            return;
        }

        Navigate(path);
    }

    void Go(string target, bool record, bool revealFile)
    {
        var normalized = Locations.Normalize(target);
        if (normalized == null)
        {
            return;
        }

        if (Locations.IsThisPC(normalized))
        {
            Open(normalized, record);
            return;
        }

        // Whether somewhere on the network is there can take a long time to hear when it is
        // not, and the window would wait all that time. So a view showing asks off the UI
        // thread, and goes there once it has heard. One nobody can see takes it on trust, and
        // asks when it shows.
        if (IsRemote(normalized))
        {
            if (showing)
            {
                Loading = FindThenOpen(target, normalized, record, revealFile);
                return;
            }

            Open(normalized, record);
            revealIfFile = revealFile;
            if (revealFile)
            {
                _ = FindWhetherFile(normalized);
            }

            return;
        }

        if (Directory.Exists(normalized))
        {
            Open(normalized, record);
            return;
        }

        if (File.Exists(normalized))
        {
            FileOperations.Open(this, normalized);
            return;
        }

        SayNotFound(target);
    }

    void SayNotFound(string target) =>
        MessageBox.Show(
            this,
            $"Lode can't find '{target}'. Check the spelling and try again.",
            "Lode",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

    // Asks after somewhere on the network, off the UI thread, then does what Go does at once
    // for somewhere on this machine.
    async Task FindThenOpen(string target, string normalized, bool record, bool revealFile)
    {
        var asking = ++finding;
        looking = normalized;
        QueueStatusChanged();
        var found = await Task.Run(() => Find(normalized));

        // Gone somewhere else meanwhile.
        if (asking != finding ||
            IsDisposed)
        {
            return;
        }

        looking = null;
        if (found == Found.Folder)
        {
            Open(normalized, record);
            return;
        }

        QueueStatusChanged();
        if (found == Found.Nothing)
        {
            SayNotFound(target);
            return;
        }

        if (revealFile)
        {
            Reveal(normalized);
            return;
        }

        FileOperations.Open(this, normalized);
    }

    // A tab started somewhere on the network, and not yet shown, finds out behind the window
    // whether that is a file, so that it is named for the file's folder, and goes there with the
    // file to select, before it shows rather than once it does. Showing first, or going somewhere
    // else, overtakes it, as both ask again.
    async Task FindWhetherFile(string path)
    {
        var asking = finding;
        var found = await Task.Run(() => Find(path));
        if (asking != finding ||
            IsDisposed ||
            showing ||
            !revealIfFile ||
            found != Found.File)
        {
            return;
        }

        revealIfFile = false;
        history.Clear();
        historyIndex = -1;
        Reveal(path);
    }

    // A view that was taken somewhere on the network while nobody could see it, or left there,
    // has just shown. It is asked after first, off the UI thread: watching the folder and
    // reading its free space ask too, and would keep the window waiting if it has gone.
    async Task FindThenRead(bool preserveState)
    {
        var asking = ++finding;
        var path = CurrentPath;
        looking = path;
        QueueStatusChanged();
        var found = await Task.Run(() => Find(path));
        if (asking != finding ||
            IsDisposed)
        {
            return;
        }

        looking = null;
        QueueStatusChanged();

        // Out of sight again meanwhile. Still to be read, the next time it shows.
        if (!showing)
        {
            return;
        }

        if (found == Found.Folder)
        {
            ReadFolder(preserveState);
            return;
        }

        if (found == Found.File &&
            revealIfFile)
        {
            // Where the tab started is the file, which is nowhere to go back to.
            history.Clear();
            historyIndex = -1;
            Reveal(path);
            return;
        }

        // Not there. The tab stays where it was, and says so.
        stale = false;
        Unwatch();
        StopSearch();
        error = $"Lode can't find '{path}'.";
        Clear();
    }

    // Takes the view to a folder known to be there, or taken on trust.
    void Open(string normalized, bool record)
    {
        // Anywhere being asked after has been overtaken.
        finding++;
        looking = null;
        revealIfFile = false;
        if (record)
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(normalized);
            historyIndex = history.Count - 1;
        }

        var sameDrive = Locations.SameRoot(CurrentPath, normalized);
        CurrentPath = normalized;
        filter = "";
        StopSearch();
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
        if (!sameDrive)
        {
            // The drive left's, until the drive's own has been read with the folder.
            FreeSpaceText = "";
        }

        PathChanged?.Invoke();
        Loading = LoadAsync(false);
    }

    // Watches the folder and reads it, for a view that has just shown.
    void ReadFolder(bool preserveState)
    {
        stale = false;
        Watch();
        Loading = LoadAsync(preserveState);
        if (searchStale)
        {
            SearchAgain();
        }
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

        // What a search found goes too, and is searched for again.
        if (IsSearch)
        {
            StopSearch();
            searchStale = true;
        }
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
        UpdateEmptyText();
    }

    // What the list says while it has no rows: that the filter matches nothing, or that the
    // subfolders are still being searched for it.
    void UpdateEmptyText()
    {
        if (filter.Length == 0 ||
            error != null)
        {
            list.EmptyText = null;
            return;
        }

        list.EmptyText = searching ? "Searching subfolders..." : "No items match your search.";
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
    /// Opens the folder containing <paramref name="path"/> with that item selected. Already in
    /// that folder, and not filtering it, the view reads it again to select the item, rather than
    /// going there a second time.
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
        if (filter.Length == 0 &&
            Locations.Same(parent, CurrentPath))
        {
            Reload();
            return;
        }

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

    /// <summary>
    /// Reads the folder again, and searches its subfolders again for the filter, if it has one.
    /// </summary>
    public void Reload()
    {
        ReadAgain();
        if (IsSearch)
        {
            SearchAgain();
        }
    }

    // Reads the folder again, leaving what a search found as it is: for a change seen in the
    // folder, which is all that is watched.
    void ReadAgain()
    {
        if (!showing)
        {
            stale = true;
            return;
        }

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
        var freeSpace = places.FreeSpace;
        loading = true;

        // Sorted there too, which leaves the UI thread only the rows to make. The drive's free
        // space is read there as well, as asking a drive mapped to a share waits on the network.
        var (loaded, free, failure) = await Task.Run(
            () =>
            {
                var space = FreeSpaceOf(path, freeSpace);
                try
                {
                    var found = Enumerate(path, showHidden, hiddenSystem);
                    found.Sort((first, second) => Compare(first, second, column, ascending));
                    return (found, space, null);
                }
                catch (Exception exception)
                {
                    return (new List<FileEntry>(), space, (string?)exception.Message);
                }
            });

        if (version != loadVersion || IsDisposed)
        {
            return;
        }

        loading = false;
        FreeSpaceText = free;
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
    static TimeSpan longestWait = TimeSpan.FromSeconds(10);

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

        if (Locations.IsServer(path))
        {
            foreach (var (name, hidden) in Shares.List(path))
            {
                if (hidden && !showHidden)
                {
                    continue;
                }

                var attributes = FileAttributes.Directory;
                if (hidden)
                {
                    attributes |= FileAttributes.Hidden;
                }

                result.Add(new(name, Path.Join(path, name), true, 0, default, attributes, IsShare: true));
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

        foreach (var entry in new FileSystemEnumerable<FileEntry>(path, ToEntry, options))
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

    // Made straight from what the file system reports, without a FileInfo or DirectoryInfo for
    // each on the way.
    static FileEntry ToEntry(ref FileSystemEntry entry)
    {
        var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
        long size = 0;
        if (!isDirectory)
        {
            size = entry.Length;
        }

        return new(entry.FileName.ToString(), entry.ToFullPath(), isDirectory, size, entry.LastWriteTimeUtc.LocalDateTime, entry.Attributes);
    }

    // Lets go of the search of the subfolders, and of what it found.
    void StopSearch()
    {
        search?.Cancel();
        search = null;
        searching = false;
        searchStale = false;
        foundPending = false;
        found = [];
        Searching = Task.CompletedTask;
    }

    // Searches the subfolders for what the filter matches, from the start. A view nobody can
    // see searches once it shows.
    void SearchAgain()
    {
        StopSearch();
        if (IsSearch)
        {
            if (showing)
            {
                var cancel = new CancelSource();
                search = cancel;
                searching = true;
                Searching = SearchAsync(cancel.Token);
            }
            else
            {
                searchStale = true;
            }
        }

        UpdateEmptyText();
        QueueStatusChanged();
    }

    async Task SearchAsync(Cancel cancel)
    {
        var root = CurrentPath;
        var pattern = filter;
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        var sort = (sortColumn, sortAscending);

        // Reported on the UI thread, each in turn, and all of them before the search ends here.
        var progress = new Progress<FoundBatch>(_ => AddFound(_, cancel));
        try
        {
            await Task.Delay(searchDelay, cancel);
            await Task.Run(() => SearchSubfolders(root, pattern, showHidden, hiddenSystem, sort, progress, cancel), cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The folder went, or a subfolder could not be read part way through. What was
            // found before that stays.
        }

        if (cancel.IsCancellationRequested ||
            IsDisposed)
        {
            return;
        }

        searching = false;
        UpdateEmptyText();
        QueueStatusChanged();
    }

    /// <summary>
    /// Some of what the search found, and the order it was put in, which is the list's when the
    /// search started.
    /// </summary>
    sealed record FoundBatch(List<FileEntry> Entries, (int Column, bool Ascending) Sort);

    void AddFound(FoundBatch batch, Cancel cancel)
    {
        if (cancel.IsCancellationRequested ||
            IsDisposed)
        {
            return;
        }

        // The list may have been sorted another way since the search started.
        var sort = (sortColumn, sortAscending);
        var compare = Order(sortColumn, sortAscending);
        if (foundSort != sort)
        {
            found.Sort(compare);
            foundSort = sort;
        }

        var entries = batch.Entries;
        if (batch.Sort != sort)
        {
            entries.Sort(compare);
        }

        found = Merge(found, entries, compare);
        ShowFound();
    }

    // Shows what the search has found, unless a name is being edited, which a new set of rows
    // would end: then once the edit is over.
    void ShowFound()
    {
        if (editing)
        {
            foundPending = true;
            return;
        }

        foundPending = false;
        Populate(true);
    }

    // Everything beneath the folder's subfolders whose name the filter matches, reported a batch
    // at a time, each sorted. Not the folder's own items, which its entries have. Leaves out what
    // the folder's own listing would: hidden items, unless they are shown, and system ones.
    static void SearchSubfolders(
        string root,
        string pattern,
        bool showHidden,
        SystemItems? hiddenSystem,
        (int Column, bool Ascending) sort,
        IProgress<FoundBatch> progress,
        Cancel cancel)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? default : FileAttributes.Hidden
        };
        var recursive = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = options.AttributesToSkip,
            RecurseSubdirectories = true
        };
        var compare = Order(sort.Column, sort.Ascending);
        var batch = new List<FileEntry>();
        var sent = Stopwatch.GetTimestamp();

        void Send()
        {
            batch.Sort(compare);
            progress.Report(new(batch, sort));
            batch = [];
            sent = Stopwatch.GetTimestamp();
        }

        var subfolders = new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory && CanSearch(ref entry, hiddenSystem)
        };
        foreach (var subfolder in subfolders)
        {
            var matches = new FileSystemEnumerable<FileEntry>(subfolder, ToEntry, recursive)
            {
                // Asked of every item, matching or not, so it is where the search notices it has
                // been let go of, and sends what it has found once it has been holding it a while.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    if (batch.Count > 0 &&
                        Stopwatch.GetElapsedTime(sent) >= searchBatch)
                    {
                        Send();
                    }

                    return Matches(pattern, entry.FileName) &&
                           !IsSystem(ref entry, hiddenSystem);
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) => CanSearch(ref entry, hiddenSystem)
            };
            try
            {
                foreach (var match in matches)
                {
                    batch.Add(match);
                }
            }
            catch (IOException)
            {
                // The subfolder went while it was being searched, or its drive did.
            }
        }

        if (batch.Count > 0)
        {
            Send();
        }
    }

    // Whether Show system files would leave an item out. That takes its full path, so is only
    // asked of folders, and of what is marked system, which are all it could leave out.
    static bool IsSystem(ref FileSystemEntry entry, SystemItems? hiddenSystem) =>
        hiddenSystem != null &&
        (entry.IsDirectory || (entry.Attributes & FileAttributes.System) != 0) &&
        hiddenSystem.IsSystem(entry.ToFullPath(), entry.Attributes);

    // Whether the search goes into a folder: not one Show system files leaves out, nor a link to
    // another folder, a junction or symbolic link, which can lead round in a circle, or to
    // folders searched already. Other folders the file system marks the same way, as cloud
    // storage can, are not links, and are gone into.
    static bool CanSearch(ref FileSystemEntry entry, SystemItems? hiddenSystem)
    {
        if (IsSystem(ref entry, hiddenSystem))
        {
            return false;
        }

        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return true;
        }

        try
        {
            return new DirectoryInfo(entry.ToFullPath()).LinkTarget == null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Whether the filter matches a name: by its wildcards, where it has any, and otherwise by
    // being anywhere in the name.
    static bool Matches(string filter, CharSpan name)
    {
        if (filter.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return FileSystemName.MatchesSimpleExpression(filter, name);
        }

        return name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // Two lists in the same order, as one, still in it.
    static List<FileEntry> Merge(List<FileEntry> first, List<FileEntry> second, Comparison<FileEntry> compare)
    {
        var merged = new List<FileEntry>(first.Count + second.Count);
        var firstIndex = 0;
        var secondIndex = 0;
        while (firstIndex < first.Count &&
               secondIndex < second.Count)
        {
            if (compare(second[secondIndex], first[firstIndex]) < 0)
            {
                merged.Add(second[secondIndex++]);
            }
            else
            {
                merged.Add(first[firstIndex++]);
            }
        }

        merged.AddRange(first.Skip(firstIndex));
        merged.AddRange(second.Skip(secondIndex));
        return merged;
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

        if (foundSort != (sortColumn, sortAscending))
        {
            found.Sort(Order(sortColumn, sortAscending));
            foundSort = (sortColumn, sortAscending);
        }

        // In order already, as what it is taken from is.
        var visible = ApplyFilter();

        if (preserveState && pendingRename == null && pendingSelection == null && !columnsChanged && visible.SequenceEqual(displayed))
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

        using (list.SuspendPainting())
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

    bool IsColumnShown(int column)
    {
        if (column == nameColumn)
        {
            return true;
        }

        if (column == folderColumn &&
            !IsSearch)
        {
            return false;
        }

        return !settings.HiddenColumns.Contains(columnDefinitions[column].Key, StringComparer.OrdinalIgnoreCase);
    }

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
        var menu = new ExplorerMenu();
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
            // Hidden or shown only while there is a search to show it for.
            if (column == folderColumn &&
                !IsSearch)
            {
                continue;
            }

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

    // A drive's cells, one for every column, and its icon, as the shell describes it. A drive
    // mapped to a share is described off the UI thread, and its row made again once it has been.
    (string[] Cells, int Icon) DescribeDrive(FileEntry entry)
    {
        var (displayName, typeName, icon) = ShellIcons.DescribeDrive(entry.FullPath, DriveDescribed);
        string[] cells =
        [
            displayName,
            "",
            entry.Size > 0 ? Formatting.Size(entry.FreeSpace) : "",
            typeName,
            entry.Size > 0 ? Formatting.Size(entry.Size) : ""
        ];
        return (cells, icon);
    }

    void DriveDescribed()
    {
        if (IsDisposed ||
            !IsThisPC)
        {
            return;
        }

        rows.Clear();
        list.Invalidate();
    }

    // What a file or folder shows in a column.
    string Cell(FileEntry entry, int column)
    {
        switch (column)
        {
            case nameColumn:
                return DisplayName(entry);
            case folderColumn:
                return FolderCell(entry);
            case dateColumn:
                // A share has no date of its own.
                if (entry.Modified == default)
                {
                    return "";
                }

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

    // Where a search found an item: its folder, from the one open. Nothing for an item in the
    // folder open itself.
    string FolderCell(FileEntry entry)
    {
        var folder = Path.GetDirectoryName(entry.FullPath);
        if (folder == null ||
            Locations.Same(folder, CurrentPath))
        {
            return "";
        }

        return Path.GetRelativePath(CurrentPath, folder);
    }

    ListViewItem CreateItem(FileEntry entry)
    {
        if (entry.IsDrive)
        {
            var (driveCells, driveIcon) = DescribeDrive(entry);
            var drive = new ListViewItem(ShownCells(driveCells), ShellListView.PaddedIcon(driveIcon)) { Tag = entry };
            ColorDetails(drive, ExplorerStyle.SecondaryText);
            return drive;
        }

        string[] cells =
        [
            Cell(entry, nameColumn),
            Cell(entry, folderColumn),
            Cell(entry, dateColumn),
            Cell(entry, typeColumn),
            Cell(entry, sizeColumn)
        ];
        var icon = ShellListView.PaddedIcon(ShellIcons.GetIcon(entry.FullPath, entry.IsDirectory, entry.Modified));
        var item = new ListViewItem(ShownCells(cells), icon) { Tag = entry };
        if (entry.IsHidden)
        {
            item.ForeColor = SystemColors.GrayText;
            ColorDetails(item, SystemColors.GrayText);
            return item;
        }

        ColorDetails(item, ExplorerStyle.SecondaryText);
        return item;
    }

    // Every column but the name, which is always the first, in gray, as Explorer has them: read
    // after the name, if at all.
    static void ColorDetails(ListViewItem item, Color color)
    {
        item.UseItemStyleForSubItems = false;
        for (var index = 1; index < item.SubItems.Count; index++)
        {
            item.SubItems[index].ForeColor = color;
        }
    }

    // What the list shows: the entries, or those the filter matches, with what the search of the
    // subfolders has found among them. A new list, in order.
    List<FileEntry> ApplyFilter()
    {
        if (filter.Length == 0)
        {
            return [.. entries];
        }

        var matches = entries.Where(_ => Matches(filter, _.Name)).ToList();
        if (found.Count == 0)
        {
            return matches;
        }

        return Merge(matches, found, Order(sortColumn, sortAscending));
    }

    static Comparison<FileEntry> Order(int column, bool ascending) =>
        (first, second) => Compare(first, second, column, ascending);

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
            // Every item is beneath the folder open, so ordering by where they are from there is
            // ordering by their whole paths.
            folderColumn => StrCmpLogicalW(
                Path.GetDirectoryName(first.FullPath) ?? "",
                Path.GetDirectoryName(second.FullPath) ?? ""),
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
            .Where(_ => !_.IsPlace)
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
        if (ListsPlaces)
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

    public void Delete(bool permanent)
    {
        var paths = SelectedPaths();
        var deleting = FileOperations.Delete(Owner, paths, permanent);
        if (found.Count > 0)
        {
            _ = DropDeletedFound(deleting, paths);
        }
    }

    // What a search found is not watched, as the folder's own items are. So what is deleted from
    // the list leaves it once the deletion is done, with all that was found inside it.
    async Task DropDeletedFound(Task deleting, string[] paths)
    {
        await deleting;
        if (IsDisposed)
        {
            return;
        }

        var gone = paths
            .Where(_ => !Path.Exists(_))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count > 0 &&
            found.RemoveAll(_ => IsWithin(_.FullPath, gone)) > 0)
        {
            ShowFound();
        }
    }

    // Whether a path is one of these, or inside one.
    static bool IsWithin(string path, HashSet<string> paths)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (paths.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

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
        if (ListsPlaces)
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
        else if (!ListsPlaces)
        {
            ShellContextMenu.InvokeVerb(Owner, [CurrentPath], "properties");
        }
    }

    void OnBeforeLabelEdit(object? sender, LabelEditEventArgs eventArgs)
    {
        if (eventArgs.Item >= displayed.Count ||
            displayed[eventArgs.Item].IsPlace)
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
        if (foundPending)
        {
            // Once the list is done with the edit, and with the name changed below, if it is.
            BeginInvoke(ShowFound);
        }

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

        var foundPosition = found.IndexOf(entry);
        if (foundPosition >= 0)
        {
            // Sorted again when next shown: by its new name it may belong elsewhere.
            found[foundPosition] = renamed;
            foundSort = (-1, true);
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
        else if (!ListsPlaces)
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
        var anyPlace = false;
        var allFolders = true;
        var allFiles = true;
        foreach (var entry in selection)
        {
            anyPlace |= entry.IsPlace;
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

        // What applies to a folder applies to each of several: a terminal tab for each, git
        // across the repositories of them all.
        List<string> folders = [];
        if (allFolders)
        {
            folders = selection.Select(_ => _.FullPath).ToList();
            commands.Add(LodeActions.Command("openInNewTab", () => OpenInNewTabs(selection)));
            commands.Add(LodeActions.Command("terminal", () => FileOperations.OpenTerminal(folders)));
            commands.Add(LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(folders, admin: true)));
            commands.AddRange(ClaudeDesktop.Commands(this, folders));
            commands.Add(LodeActions.Pin(folders, places, SetPinned));
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
            LodeActions.Command("cut", () => Copy(true), !anyPlace),
            LodeActions.Command("copy", () => Copy(false), !anyPlace),
            LodeActions.Command("rename", BeginRename, selection.Count == 1 && !anyPlace),
            LodeActions.Command("delete", () => Delete(false), !anyPlace),
            null,
            LodeActions.Command("createShortcut", () => CreateShortcuts(selection), !anyPlace),
            LodeActions.Command("copyAsPath", () => CopyAsPath(selection.Select(_ => _.FullPath))),
            LodeActions.Command("properties", ShowProperties)
        ]);
        commands.AddRange(CodeMenu.Commands(this, settings, folders));
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

        if (!ListsPlaces)
        {
            ShowBackgroundMenu(menu, location);
        }
    }

    /// <summary>
    /// Whether there is anything for <see cref="ShowShellMenu"/> to show a menu for.
    /// </summary>
    public bool CanShowShellMenu =>
        list.SelectedIndices.Count > 0 || !ListsPlaces;

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
        .. ClaudeDesktop.Commands(this, [CurrentPath]),
        LodeActions.Command("copyAsPath", () => CopyAsPath([CurrentPath])),
        LodeActions.Command("properties", ShowProperties),
        .. CodeMenu.Commands(this, settings, [CurrentPath])
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
        var fallback = new ExplorerMenu();
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

        if (ListsPlaces)
        {
            return null;
        }

        return CurrentPath;
    }

    void OnDragOver(object? sender, DragEventArgs eventArgs) =>
        eventArgs.Effect = FileOperations.GetDropEffect(eventArgs, DropDestination(eventArgs));

    void OnDragDrop(object? sender, DragEventArgs eventArgs) =>
        FileOperations.Drop(Owner, eventArgs, DropDestination(eventArgs));

    void Watch()
    {
        Unwatch();
        changes.Reset();
        if (ListsPlaces)
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

        ReadAgain();
        return true;
    }

    static string FreeSpaceOf(string path, Func<string, string> freeSpace)
    {
        if (Locations.IsThisPC(path))
        {
            return "";
        }

        return freeSpace(path);
    }

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
            search?.Cancel();
            changes.Dispose();
            watcher?.Dispose();
            marquee.Dispose();
        }

        base.Dispose(disposing);
    }
}
