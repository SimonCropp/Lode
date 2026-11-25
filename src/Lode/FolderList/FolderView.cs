/// <summary>
/// One tab's worth of file browsing: the file list, its navigation history, and the file
/// operations that act on the selection.
/// </summary>
sealed class FolderView : UserControl
{
    internal const int nameColumn = 0;
    internal const int gitColumn = 1;
    internal const int tagColumn = 2;
    internal const int folderColumn = 3;
    const int dateColumn = 4;
    const int typeColumn = 5;
    const int sizeColumn = 6;

    // The list's columns, in order, by the index sorting and the cells use. Name is always
    // shown; the others can be hidden from the header's right-click menu, in every view at once.
    // On This PC, Date modified and Size show each drive's free space and total size. Folder,
    // where each item is, only a search has, whose items come from all through the folder. Git
    // only a folder in the code folder has, in a repository or listing some: beside the name, as
    // it says what has become of the file, or the repository, it names. Tag, each repository's
    // most recent tag, only a folder listing repositories has, as a file has no tag of its own.
    sealed record ColumnDefinition(string Key, string Title, string ThisPCTitle, int Width, HorizontalAlignment Alignment);

    static ColumnDefinition[] columnDefinitions =
    [
        new("Name", "Name", "Name", 380, HorizontalAlignment.Left),
        new("Git", "Git", "Git", 220, HorizontalAlignment.Left),
        new("Tag", "Tag", "Tag", 120, HorizontalAlignment.Left),
        new("Folder", "Folder", "Folder", 220, HorizontalAlignment.Left),
        new("Date", "Date modified", "Free space", 150, HorizontalAlignment.Left),
        new("Type", "Type", "Type", 180, HorizontalAlignment.Left),
        new("Size", "Size", "Total size", 110, HorizontalAlignment.Right)
    ];

    // The columns the list has now, in the order shown. How wide the user sized each is in the
    // settings, shared by every view, so one shown again is as wide as it was.
    List<int> shownColumns = [];

    /// <summary>
    /// A column was hidden or shown. Every view shares the setting, so each should refresh.
    /// </summary>
    public event Action? ColumnsChanged;

    /// <summary>
    /// The user sized a column in this view. Every view shares the widths, so each of the others
    /// should take them up (<see cref="RefreshColumnWidths"/>).
    /// </summary>
    public event Action<FolderView>? ColumnWidthsChanged;

    Settings settings;
    Places places;
    ShellListView list = new();

    // Watcher events arrive on thread-pool threads and can come in bursts. This turns them into
    // at most one reload per interval, on the UI thread.
    ChangeSignal changes;
    List<string> history = [];
    int historyIndex = -1;

    FolderWatcher? watcher;

    // Watches the folder's parent for the folder itself: deleted, sent to the Recycle Bin,
    // renamed or moved. The folder's own watch only sees what is in it, and goes on watching one
    // moved, as one sent to the Recycle Bin is, where it now is.
    FolderWatcher? parentWatcher;
    ChangeSignal parentChanges;

    // Watches all through a repository's root that has files promoted to it, for changes to
    // those, which the folder's own watch, of only what is in it, misses. See WatchPromoted.
    FolderWatcher? promotedWatcher;
    string? promotedRoot;

    // The patterns the watch goes by, read on its thread: the latest read's, so the watch needn't
    // start again when they change.
    volatile PromotedFiles? promotedPatterns;

    // What the watch on the parent saw renamed, from where to where, still to be followed.
    ConcurrentQueue<(string From, string To)> renames = new();

    // Reaches the UI thread from the thread of a move or rename Lode did, to follow it.
    SynchronizationContext context = new WindowsFormsSynchronizationContext();
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
    static TimeSpan searchBatch = TimeSpan.FromMilliseconds(250);

    // What the search of the subfolders has found: everything beneath them whose name the filter
    // matches, ordered as the entries are. The folder's own matches are taken from its entries,
    // which are kept up to date as it changes; these are as they were when found. Never changed
    // where it is, only replaced, as a merge off the UI thread may be reading it.
    List<FileEntry> found = [];
    (int Column, bool Ascending) foundSort;
    CancelSource? search;
    bool searching;

    // What the search has sent and is yet to be merged into what it found, and the merge under
    // way, off the UI thread: one at a time, each of what the last made. The generation lets a
    // merge for a search let go of know it is no longer wanted.
    List<FoundBatch> unmerged = [];
    Task? merging;
    int mergeGeneration;

    // The search stopped at MostFound, rather than for having looked everywhere.
    bool foundCapped;

    /// <summary>
    /// The most a search of the subfolders finds before it stops: each item is a few hundred
    /// bytes, and a search for a letter on a drive would find millions. Replaced in tests.
    /// </summary>
    internal int MostFound = 100_000;

    // What the filter last made of the entries and what was found, and of what, so the list can
    // be given its rows again, the git states having arrived say, without merging them again.
    (List<FileEntry> Entries, List<FileEntry> Found, string Filter, int Column, bool Ascending, GitCells Cells, int Edits, List<FileEntry> Visible)? filtered;

    // Counts the changes made to the entries where they are, a rename say, which the lists'
    // being the same doesn't show.
    int entryEdits;

    // The search is to start again once the view shows: it was let go, or asked for again, while
    // nobody could see the view.
    bool searchStale;

    // Found while a name was being edited, which showing it would have ended.
    bool foundPending;

    /// <summary>
    /// Reads a repository's state, for the status bar and the Git column: with LibGit2Sharp, or
    /// git for a repository of many files, whichever is faster (GitStateBenchmarks).
    /// </summary>
    internal static GitState.Reader ReadGit = LibGitState.FromFaster;

    // The folders listed that are repositories, found as the folder is read, and the repository
    // the folder is in, for the status bar. Only in the code folder, where the git actions are
    // too: a repository above it, a home folder kept in git say, would have everything in the
    // code folder changed.
    HashSet<string> repos = new(StringComparer.OrdinalIgnoreCase);
    string? containingRepo;

    // What each of those repositories was last read to be, by its path, and what the Git column
    // says of each item from that.
    Dictionary<string, GitState> gitStates = new(StringComparer.OrdinalIgnoreCase);

    // The sites the repositories' origins are on, read with their states.
    Dictionary<string, RemoteSite> gitSites = new(StringComparer.OrdinalIgnoreCase);

    GitCells gitCells = GitCells.None;

    // The reading of the states under way, one at a time: asked for again meanwhile, it reads
    // again once it is done. The version stops what is read for a folder left landing in the
    // folder gone to.
    CancelSource? gitCancel;
    bool gitPending;
    int gitVersion;
    bool gitStatesArriving;

    // When the states were last read, as a Stopwatch timestamp. Zero before they ever were.
    long gitReadAt;

    // How long after the states were read the window coming back to the front reads them again.
    // Going between windows a moment apart reads them once: a code folder is dozens of reads.
    static TimeSpan gitFresh = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The reading of the repositories' states under way. Completes once they are all shown.
    /// </summary>
    internal Task GitReading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The repository the folder is in, up to the code folder, as of the folder last read: the
    /// Changes and History panes are about it. Unlike the repository the Git column reads, kept
    /// while the next folder is read, so going between folders of one repository leaves it be.
    /// </summary>
    internal string? Repo { get; private set; }

    /// <summary>
    /// The repository the status bar is about, whose branch it shows and switches, and whose
    /// changes, commits ahead and behind it acts on: one selected on its own, in a folder that
    /// lists repositories, the code folder say, otherwise the one the folder is in. Only one whose
    /// state has been read: nothing outside the code folder, or outside a repository.
    /// </summary>
    internal string? GitRepo
    {
        get
        {
            // The selection read as the list holds it, by index into what is displayed, not made
            // into a list: this is asked of at every change of selection, a thousand rows say.
            if (list.SelectedIndices is [var index] &&
                index < displayed.Count &&
                gitStates.ContainsKey(displayed[index].FullPath))
            {
                return displayed[index].FullPath;
            }

            if (containingRepo != null &&
                gitStates.ContainsKey(containingRepo))
            {
                return containingRepo;
            }

            return null;
        }
    }

    /// <summary>
    /// What <see cref="GitRepo"/> was last read to be: its branch, what is uncommitted, and the
    /// commits ahead and behind.
    /// </summary>
    internal GitState? GitRepoState
    {
        get
        {
            if (GitRepo is { } repo)
            {
                return gitStates[repo];
            }

            return null;
        }
    }

    /// <summary>
    /// The state of <see cref="GitRepo"/>, as the status bar says it. Nothing where there is none.
    /// </summary>
    public string GitText
    {
        get
        {
            if (GitRepoState is { } state)
            {
                return state.Describe();
            }

            return "";
        }
    }

    /// <summary>
    /// The site the origin of <see cref="GitRepo"/> is on, GitHub say, for the status bar to link
    /// to. Null where it shows no state, or for an origin on no site Lode knows.
    /// </summary>
    public RemoteSite? GitSite
    {
        get
        {
            if (GitRepo is { } repo)
            {
                return gitSites.GetValueOrDefault(repo);
            }

            return null;
        }
    }

    /// <summary>
    /// The search of the subfolders under way. Completes once the list shows all it found.
    /// </summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    // Whether the filter searches the subfolders too, as it does everywhere but This PC, a
    // machine on the network and a zip.
    bool IsSearch => filter.Length > 0 &&
                     !IsReadOnly;

    // What to select once the list shows the folder, the first focused.
    IReadOnlyList<string>? pendingSelection;
    GutterMarquee marquee;

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
    GutterScrollBar scrollBar;

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
    Dictionary<int, ListViewItem> rows = [];
    const int maximumRows = 512;

    // Stands in for a row that is asked for but not needed: one that has gone, or one of those
    // being selected again, which the list asks for only to say that it changed. A cell for every
    // column there could be, so whichever it is asked for is there.
    ListViewItem blankRow = new(["", "", "", "", "", ""]);
    bool restoring;

    // The rows are to be made again even if the folder turns out not to have changed.
    bool rowsStale;

    /// <summary>
    /// How many rows are made at the moment. For tests.
    /// </summary>
    internal int RowsMade => rows.Count;

    /// <summary>
    /// What the list says while it has no rows, with the link to search inside the files, in
    /// brackets, when that is shown. For tests.
    /// </summary>
    internal string? EmptyText
    {
        get
        {
            if (nothingFoundShown)
            {
                return $"{nothingMatches} [Search inside the files]";
            }

            return list.EmptyText;
        }
    }

    /// <summary>
    /// The list the rows are in. For tests.
    /// </summary>
    internal ShellListView List => list;

    string? pendingRename;
    bool editing;

    // The item whose name is being edited. The list says only which row the edit was on, and
    // the rows can be shown again under it, a read of the folder landing say: the row is then
    // another item's, which is not the one to rename.
    FileEntry? edited;
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

    // Where in a zip the view is, worked out once for each place it goes. See Zip.Split.
    (string Path, Zip.Location? Location) archive = ("", null);

    /// <summary>
    /// The zip the view is in, and where in it, or null when it shows anywhere else.
    /// </summary>
    internal Zip.Location? Archive
    {
        get
        {
            if (!string.Equals(archive.Path, CurrentPath, StringComparison.Ordinal))
            {
                archive = (CurrentPath, Zip.Split(CurrentPath));
            }

            return archive.Location;
        }
    }

    /// <summary>
    /// Inside a zip, which Lode shows but doesn't change.
    /// </summary>
    public bool InArchive => Archive != null;

    /// <summary>
    /// Somewhere nothing can be made, pasted or dropped: where <see cref="ListsPlaces"/>, or
    /// inside a zip.
    /// </summary>
    public bool IsReadOnly => ListsPlaces || InArchive;

    IntPtr Owner => FindForm()?.Handle ?? Handle;

    public FolderView(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        SetPinned = (path, pin) => ShellContextMenu.SetPinned(Owner, path, pin);

        // Explorer's margin left of the list, which a selection rectangle can be dragged from.
        Padding = new(LogicalToDeviceUnits(12), 0, 0, 0);
        BackColor = ExplorerStyle.Window;
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
        list.RetrieveVirtualItem += (_, args) => args.Item = Row(args.ItemIndex);
        list.IsCut = index =>
            index < displayed.Count &&
            CutItems.Contains(displayed[index].FullPath);
        CutItems.Changed += OnCutChanged;
        // After Control's constructor, which puts Windows Forms' in place on a new thread.
        madeOn = SynchronizationContext.Current;
        GitCache.Stale += OnGitStale;
        list.SearchForVirtualItem += OnSearchForVirtualItem;
        list.ColumnFitRequested += SizeColumnToFit;
        list.ColumnResized += KeepWidth;
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
        list.DragLeave += (_, _) => list.DropTarget = -1;
        Controls.Add(list);

        // Over the list's own scrollbar, so in front of it.
        scrollBar = new(list);
        Controls.Add(scrollBar);
        scrollBar.BringToFront();

        // In the list, over where its rows would be.
        italic = new(Font, FontStyle.Italic);
        nothingFound = BuildNothingFound();
        list.Controls.Add(nothingFound);
        list.Resize += (_, _) => PlaceNothingFound();

        changes = new(400, OnChanged);
        parentChanges = new(100, OnParentChanged);
        ShellFileOperation.Moved += OnMoved;
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
            else
            {
                if (searchStale)
                {
                    SearchAgain();
                }

                // The folder is as it was, but its repositories may not be: git changes only .git.
                ReadGitStateUnlessFresh();
            }
        }
    }

    /// <summary>
    /// When the view last stopped or started <see cref="Showing"/>, or was made, for closing tabs
    /// nobody looks at. Kept by the window it is in, see <see cref="MainForm.CloseUnusedTabs"/>,
    /// and carried with the tab to another.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DateTimeOffset LastShown { get; set; }

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
            if (searching)
            {
                text += " found so far, searching subfolders";
            }
            else if (foundCapped)
            {
                text += $" found, only the first {MostFound:N0} in subfolders";
            }
            else
            {
                text += " found";
            }
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

        // The zip itself is a file, to be revealed as one. Inside it, what it holds.
        if (Zip.Split(path) is {Inner.Length: > 0} inArchive)
        {
            var kind = Zip.Find(inArchive);
            if (kind == Zip.Kind.Folder)
            {
                return Found.Folder;
            }

            if (kind == Zip.Kind.File)
            {
                return Found.File;
            }

            return Found.Nothing;
        }

        if (File.Exists(path))
        {
            return Found.File;
        }

        return Found.Nothing;
    }

    public void Navigate(string target) =>
        Go(target, null, revealFile: false);

    /// <summary>
    /// What is typed in the address bar, as Explorer takes it: a shell name, "shell:downloads"
    /// say, goes to that folder; a path goes there, one that isn't full taken from the folder
    /// open; and anything else that isn't a path to something here, `cmd`, `powershell`, `wt` or
    /// `code .`, is run, in the folder open.
    /// </summary>
    public void GoToTyped(string typed)
    {
        var text = typed.Trim();
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            if (ShellFileOperation.FileSystemPathOf(text) is { } path)
            {
                Navigate(path);
                return;
            }

            // One with no place on disk, the Recycle Bin say, which Lode can't show.
            Run(text, "", text);
            return;
        }

        var normalized = Locations.NormalizeTyped(text, CurrentPath);
        if (TypedCommand.Parse(text) is { } command &&
            (GivenOnlyDots(command) ||
             !IsSomewhere(normalized)))
        {
            Run(command.Program, command.Arguments, text);
            return;
        }

        Navigate(normalized ?? text);
    }

    // `code .` is code given the folder open, though as a path it is the folder named code,
    // where the folder open has one: Windows drops the dots and spaces a name ends with.
    static bool GivenOnlyDots(TypedCommand.Command command)
    {
        var arguments = command.Arguments;
        return arguments.Length > 0 &&
               arguments.AsSpan().Trim(". \t").IsEmpty;
    }

    // Whether what was typed is somewhere to go: This PC, or a folder or file here, which a
    // command never shadows.
    static bool IsSomewhere(string? normalized) =>
        normalized != null &&
        (Locations.IsThisPC(normalized) ||
         Directory.Exists(normalized) ||
         File.Exists(normalized));

    /// <summary>
    /// Starts what is typed in the address bar, as Run would, in the folder open. Tests swap it.
    /// </summary>
    internal static Action<ProcessStartInfo> StartTyped = startInfo => Process.Start(startInfo)?.Dispose();

    void Run(string program, string arguments, string typed)
    {
        // Somewhere a program can't start in, This PC or a zip, starts it in the home folder.
        var directory = CurrentPath;
        if (!Directory.Exists(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            StartTyped(
                new(program, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = directory
                });
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode is fileNotFound or pathNotFound)
        {
            SayNotFound(typed);
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == cancelledByUser)
        {
            // The user dismissed an elevation prompt.
        }
        catch (Exception exception)
            when (
                exception is
                    Win32Exception or
                    InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    const int fileNotFound = 2;
    const int pathNotFound = 3;
    const int cancelledByUser = 1223;

    /// <summary>
    /// Where a tab starts: a folder, or a file, which means "show me this file", so its folder
    /// with the file selected. A zip is such a file, unless <paramref name="intoZip"/>, for a tab
    /// that was inside it, restored say, which opens inside it again. Somewhere on the network is
    /// taken on trust until the view shows, and asked after then, off the UI thread.
    /// </summary>
    public void OpenAt(string path, bool intoZip = false)
    {
        var reveal = !intoZip || !Zip.IsZip(path);
        if (IsRemote(path))
        {
            Go(path, null, revealFile: reveal);
            return;
        }

        if (reveal &&
            File.Exists(path))
        {
            Reveal(path);
            return;
        }

        Navigate(path);
    }

    /// <param name="position">Where in the history the target is, when going back or forward
    /// to it. Null for somewhere new, which is added to the history.</param>
    /// <param name="again">Open it even where the view is already, which clears the filter.
    /// Otherwise going there, by the breadcrumb or the address bar say, reads it again, as
    /// Explorer does, keeping what is forward in the history, the selection, how far down it
    /// is scrolled and the filter.</param>
    /// <param name="select">What to select once the folder is open, for a Reveal. Kept only
    /// if it is opened, so a Reveal that fails or is overtaken leaves nothing behind.</param>
    void Go(string target, int? position, bool revealFile, bool again = false, IReadOnlyList<string>? select = null)
    {
        var normalized = Locations.Normalize(target);
        if (normalized == null)
        {
            return;
        }

        if (position == null &&
            !again &&
            historyIndex >= 0 &&
            Locations.Same(normalized, CurrentPath))
        {
            // Overtakes anywhere being asked after, as going there would.
            finding++;
            if (looking != null)
            {
                looking = null;
                QueueStatusChanged();
            }

            Reload();
            return;
        }

        if (Locations.IsThisPC(normalized))
        {
            Open(normalized, position, select: select);
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
                Loading = FindThenOpen(target, normalized, position, revealFile, select);
                return;
            }

            Open(normalized, position, select: select);
            revealIfFile = revealFile;
            if (revealFile)
            {
                _ = FindWhetherFile(normalized);
            }

            return;
        }

        if (Directory.Exists(normalized))
        {
            Open(normalized, position, select: select);
            return;
        }

        // A zip opens as a folder, as in Explorer. A file inside one is shown in its folder.
        if (Zip.Split(normalized) is { } inArchive)
        {
            var kind = Zip.Find(inArchive);
            if (kind == Zip.Kind.Folder)
            {
                Open(normalized, position, select: select);
                return;
            }

            if (kind == Zip.Kind.File)
            {
                Reveal(normalized);
                return;
            }

            SayNotFound(target);
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
    async Task FindThenOpen(string target, string normalized, int? position, bool revealFile, IReadOnlyList<string>? select)
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
        if (found == Found.Folder ||
            (found == Found.File &&
             !revealFile && Zip.IsZip(normalized)))
        {
            Open(normalized, position, select: select);
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

        // A zip, the view inside it.
        if (found == Found.Folder ||
            (found == Found.File &&
             !revealIfFile && Zip.IsZip(path)))
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

    // Takes the view to a folder known to be there, or taken on trust. Only here does where it
    // is in the history change: going back to somewhere that has gone, or that the view is taken
    // away from while it is still being asked after, leaves the history as it was. For a folder
    // that moved, what to select once it has been read where it went is kept.
    void Open(string normalized, int? position, Kept? keep = null, IReadOnlyList<string>? select = null)
    {
        // Anywhere being asked after has been overtaken.
        finding++;
        looking = null;
        revealIfFile = false;
        if (position is { } index)
        {
            historyIndex = index;
        }
        else
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(normalized);
            historyIndex = history.Count - 1;
        }

        // Gone to a folder above, by Up, the breadcrumb, the tree or Back, it lands with the
        // folder that leads back selected, as Explorer does. What was to be selected where the
        // view was is no more.
        pendingSelection = select;
        if (keep == null &&
            pendingSelection == null &&
            Locations.ChildToward(CurrentPath, normalized) is { } leadingBack)
        {
            pendingSelection = [leadingBack];
        }

        var sameDrive = Locations.SameRoot(CurrentPath, normalized);
        CurrentPath = normalized;
        filter = "";
        StopSearch();
        StopGitState();
        error = null;

        // Each folder is sorted as it was last, as in Explorer. Following one moved or renamed,
        // the view keeps how it was sorted, and the folder is noted as sorted so where it is now.
        if (keep == null)
        {
            (sortColumn, sortAscending) = SortOfFolder();
        }
        else if (sortColumn != folderColumn)
        {
            settings.SetSort(normalized, columnDefinitions[sortColumn].Key, sortAscending);
        }

        remembered = keep;
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

    const string nothingMatches = "No items match your search.";

    // In place of rows when the search box matched nothing where Find in Files can search, not
    // This PC nor a zip: that, and a link to search inside the files instead. Drawn here rather
    // than by the list, whose own links in that text look like the rest of it, and can't be
    // reached from the keyboard.
    TableLayoutPanel nothingFound;
    Font italic;
    bool nothingFoundShown;

    TableLayoutPanel BuildNothingFound()
    {
        var text = new Label
        {
            Text = nothingMatches,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = italic,
            ForeColor = ExplorerStyle.SecondaryText,
            Margin = new(0, 0, 0, LogicalToDeviceUnits(6))
        };
        var link = new LinkLabel
        {
            Name = "searchInside",
            Text = "Search inside the files",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            // In the colour Windows gives links, as the status bar's are.
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = Padding.Empty
        };
        link.LinkClicked += (_, _) => SearchInsideTheFiles();
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = ExplorerStyle.Window,
            Visible = false
        };
        panel.Controls.Add(text);
        panel.Controls.Add(link);
        return panel;
    }

    // In the middle of the list.
    void PlaceNothingFound()
    {
        if (!nothingFoundShown)
        {
            return;
        }

        var size = list.ClientSize;
        nothingFound.Location = new(
            Math.Max(0, (size.Width - nothingFound.Width) / 2),
            Math.Max(0, (size.Height - nothingFound.Height) / 2));
    }

    // What the list says while it has no rows: that the filter matches nothing, with a link to
    // search for it inside the files where Find in Files can, or that the subfolders are still
    // being searched for it.
    void UpdateEmptyText()
    {
        nothingFoundShown = filter.Length > 0 &&
                            error == null &&
                            !searching &&
                            IsSearch &&
                            displayed.Count == 0;
        nothingFound.Visible = nothingFoundShown;
        if (nothingFoundShown)
        {
            list.EmptyText = null;
            nothingFound.PerformLayout();
            PlaceNothingFound();
            return;
        }

        if (filter.Length == 0 ||
            error != null)
        {
            list.EmptyText = null;
            return;
        }

        if (searching)
        {
            list.EmptyText = "Searching subfolders...";
            return;
        }

        list.EmptyText = nothingMatches;
    }

    /// <summary>
    /// The search box's text is to be searched for inside the files, with what to search for
    /// and whether that is a regular expression.
    /// </summary>
    public event Action<string, bool>? FindInFilesRequested;

    /// <summary>
    /// Whether what the search box has can be searched for inside the files instead, with Find
    /// in Files: there is something in it, and it searches here, not This PC nor a zip.
    /// </summary>
    public bool CanSearchInsideTheFiles => IsSearch;

    /// <summary>
    /// Searches for what the search box has inside the files instead: from the link under "No
    /// items match your search.", the status bar's, or Ctrl+Enter in the box.
    /// </summary>
    public void SearchInsideTheFiles()
    {
        if (!CanSearchInsideTheFiles)
        {
            return;
        }

        var (find, regex) = TextFor(filter);
        FindInFilesRequested?.Invoke(find, regex);
    }

    /// <summary>
    /// What the filter, matched against names, searches for inside files: the same text, or for
    /// a wildcard, the text between the stars at either end, and where it has more, a regular
    /// expression, * any run of characters and ? any one.
    /// </summary>
    internal static (string Find, bool Regex) TextFor(string filter)
    {
        if (filter.AsSpan().IndexOfAny('*', '?') < 0)
        {
            return (filter, false);
        }

        var trimmed = filter.Trim('*');
        if (trimmed.AsSpan().IndexOfAny('*', '?') < 0)
        {
            return (trimmed, false);
        }

        var pattern = new StringBuilder();
        foreach (var character in trimmed)
        {
            switch (character)
            {
                case '*':
                    pattern.Append(".*");
                    break;
                case '?':
                    pattern.Append('.');
                    break;
                default:
                    pattern.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return (pattern.ToString(), true);
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
    void OnSearchForVirtualItem(object? sender, SearchForVirtualItemEventArgs args)
    {
        if (args.IsTextSearch)
        {
            args.Index = FindRow(args.Text ?? "", args.StartIndex);
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

    // Selects the rows for these paths again, and gives one the focus. Given the entries they
    // were, the rows are found by the order they are in, rather than by going through all of
    // them, which is what a search that has found a hundred thousand would do for every batch.
    void Restore(HashSet<string> selected, string? focused, IReadOnlyList<FileEntry>? probes = null)
    {
        if (selected.Count == 0 &&
            focused == null)
        {
            return;
        }

        // The list keeps which rows are selected, which it can only do once it has a window.
        _ = list.Handle;
        var indexes = new List<int>();
        if (probes == null ||
            !TryFindRows(probes, selected, focused, indexes, out var focus))
        {
            indexes.Clear();
            focus = -1;
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

    // The rows the entries are now, found by a binary search of the rows, which are in the
    // list's order, then among those that order puts level with each, which is by name: the same
    // name in two folders, say. False when any is not found that way: gone, or the rows not in
    // order, a rename having put a new name where the old one was. They are then gone through.
    bool TryFindRows(IReadOnlyList<FileEntry> probes, HashSet<string> selected, string? focused, List<int> indexes, out int focus)
    {
        focus = -1;

        // With much of the list selected, everything say, a search for each costs more than
        // going through the rows once: each of its steps is a comparison, which is a call into
        // Windows, where going through is a lookup a row.
        if ((long) probes.Count * BitOperations.Log2((uint) Math.Max(displayed.Count, 2)) > displayed.Count)
        {
            return false;
        }

        var compare = Order(sortColumn, sortAscending, gitCells);
        foreach (var probe in probes)
        {
            var low = 0;
            var high = displayed.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (compare(displayed[middle], probe) < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            var index = -1;
            for (var at = low; at < displayed.Count && compare(displayed[at], probe) == 0; at++)
            {
                if (string.Equals(displayed[at].FullPath, probe.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    index = at;
                    break;
                }
            }

            if (index < 0)
            {
                return false;
            }

            if (selected.Contains(probe.FullPath))
            {
                indexes.Add(index);
            }

            if (string.Equals(probe.FullPath, focused, StringComparison.OrdinalIgnoreCase))
            {
                focus = index;
            }
        }

        return true;
    }

    public void GoBack() =>
        GoThrough(-1);

    public void GoForward() =>
        GoThrough(1);

    // Back, or forward, to the nearest place that way that is still there. One that has gone,
    // a folder deleted since, is taken out of the history: gone to, it only said it couldn't be
    // found, every time, and nothing before it could be got back to.
    void GoThrough(int step)
    {
        var position = historyIndex + step;
        var forgot = false;
        while (position >= 0 &&
               position < history.Count &&
               HasGone(history[position]))
        {
            history.RemoveAt(position);
            forgot = true;
            if (step < 0)
            {
                // What is open is one further down now, and so is the one before it.
                historyIndex--;
                position--;
            }
        }

        if (position >= 0 &&
            position < history.Count)
        {
            GoTo(position);
            return;
        }

        if (forgot)
        {
            // Nowhere left that way, which the buttons are to show.
            QueueStatusChanged();
        }
    }

    // Whether a place in the history is known to be no longer there. Only a folder on this
    // machine: one on the network is not asked after here, where the window would wait on it,
    // and what is inside a zip is for going there to find out.
    bool HasGone(string path) =>
        !Locations.IsThisPC(path) &&
        !IsRemote(path) &&
        Zip.Split(path) == null &&
        !Directory.Exists(path);

    /// <summary>
    /// Where Back would go, the nearest first, each with where it is in the history, for
    /// <see cref="GoToHistory"/>: what the Back button lists.
    /// </summary>
    public IReadOnlyList<(string Path, int Position)> BackHistory =>
        Enumerable
            .Range(0, Math.Max(historyIndex, 0))
            .Reverse()
            .Select(_ => (history[_], _))
            .ToList();

    /// <summary>
    /// Where Forward would go, the nearest first.
    /// </summary>
    public IReadOnlyList<(string Path, int Position)> ForwardHistory =>
        Enumerable
            .Range(historyIndex + 1, Math.Max(history.Count - historyIndex - 1, 0))
            .Select(_ => (history[_], _))
            .ToList();

    /// <summary>
    /// Goes straight to somewhere in the history, however far back or forward, keeping the
    /// rest of it either side.
    /// </summary>
    public void GoToHistory(int position)
    {
        if (position < 0 ||
            position >= history.Count ||
            position == historyIndex)
        {
            return;
        }

        if (HasGone(history[position]))
        {
            // Out of the history, with what would be said of it had it been gone to.
            var gone = history[position];
            history.RemoveAt(position);
            if (position < historyIndex)
            {
                historyIndex--;
            }

            QueueStatusChanged();
            SayNotFound(gone);
            return;
        }

        GoTo(position);
    }

    // Back or forward, to somewhere in the history.
    void GoTo(int position) =>
        Go(history[position], position, revealFile: false);

    public void GoUp()
    {
        // Open selects the folder left.
        var parent = Locations.Parent(CurrentPath);
        if (parent != null)
        {
            Navigate(parent);
        }
    }

    /// <summary>
    /// Opens the folder containing <paramref name="path"/> with that item selected. Already in
    /// that folder, and not filtering it, the view reads it again to select the item, rather than
    /// going there a second time.
    /// </summary>
    public void Reveal(string path) =>
        Reveal([path]);

    /// <summary>
    /// As <see cref="Reveal(string)"/>, for items in one folder: all of them selected, and the
    /// first focused.
    /// </summary>
    public void Reveal(IReadOnlyList<string> paths)
    {
        var path = paths[0];
        var parent = Path.GetDirectoryName(path);
        if (parent == null)
        {
            Navigate(path);
            return;
        }

        if (filter.Length == 0 &&
            Locations.Same(parent, CurrentPath))
        {
            pendingSelection = paths;
            Reload();
            return;
        }

        // Again where it is already, to clear the filter, which may be hiding the file.
        Go(parent, null, revealFile: false, again: true, select: paths);
    }

    /// <summary>
    /// As <see cref="Reload"/>, and the rows showing are made again even when the folder has not
    /// changed, for Refresh: what a row shows beyond the folder's own listing, a shortcut's icon
    /// say, may have.
    /// </summary>
    public void ReloadRows()
    {
        rowsStale = true;

        // Refresh reads the repositories again too, whatever the watch has heard.
        var shown = repos.ToList();
        if (containingRepo != null)
        {
            shown.Add(containingRepo);
        }

        GitCache.MarkStale(shown);
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
        var watching = watcher;
        var watchingParent = parentWatcher;
        // A zip holds no repositories to show the state of.
        string? codeFolder = null;
        if (settings.IsCode(path) &&
            !InArchive)
        {
            codeFolder = settings.CodeDirectory;
        }

        // Not changed once made, so sorting by it there is safe.
        var cells = gitCells;

        // Neither a zip, nor This PC or a machine's shares, has a repository's root to promote
        // files to.
        PromotedFiles? promoted = null;
        if (!IsReadOnly &&
            settings.PromotedFiles.Count > 0 &&
            PromotedFiles.Parse(settings.PromotedFiles) is {IsEmpty: false} patterns)
        {
            promoted = patterns;
        }

        loading = true;

        // Sorted there too, which leaves the UI thread only the rows to make. The drive's free
        // space is read there as well, as asking a drive mapped to a share waits on the network.
        // So are which folders are repositories, and the files promoted to a repository's root.
        var (loaded, late, free, failure, nearest, foundRepos, containing, promotes) = await Task.Run(() =>
        {
            var space = FreeSpaceOf(path, freeSpace);
            // A watch that failed, the folder deleted and made again say, watches again
            // before the read, so nothing after the read is missed. F5 mends one too.
            watching?.Revive();
            watchingParent?.Revive();
            try
            {
                var found = Enumerate(path, showHidden, hiddenSystem, out var late);
                PromotedFiles? promotes = null;
                if (promoted != null &&
                    CodeCommands.IsRepo(path))
                {
                    promotes = promoted;
                    found.AddRange(promoted.Find(found, showHidden, hiddenSystem));
                }

                found.Sort((first, second) => Compare(first, second, column, ascending, cells));
                var (foundRepos, containing) = FindRepos(found, path, codeFolder);
                return (found, late, space, null, null, foundRepos, containing, promotes);
            }
            catch (Exception exception)
            {
                // Not there to read. Gone, the view goes up to the nearest folder that is.
                var nearest = MeansGone(exception) ? NearestFolder(path) : null;
                return (new List<FileEntry>(), new List<Task<FileEntry>>(), space, (string?) exception.Message, nearest, new HashSet<string>(), (string?) null, (PromotedFiles?) null);
            }
        });

        if (version != loadVersion || IsDisposed)
        {
            return;
        }

        loading = false;
        if (nearest != null)
        {
            Open(nearest, null);
            return;
        }

        WatchPromoted(path, promotes);

        FreeSpaceText = free;
        entries = loaded;
        entriesSort = (column, ascending);
        error = failure;
        repos = foundRepos;
        containingRepo = containing;
        Repo = containing;

        // Sorted by Git or Tag when last open: the column shows only now the repositories are
        // known.
        if (!preserveState &&
            settings.SortOf(path) is {Column: "Git" or "Tag"} sort &&
            columnDefinitions[sortColumn].Key != sort.Column)
        {
            (sortColumn, sortAscending) = SortOfFolder();
        }

        Populate(preserveState);
        ReadTook(Stopwatch.GetElapsedTime(started));
        foreach (var drive in late)
        {
            _ = SizeLate(version, drive);
        }

        ReadGitState();
    }

    // The folders listed that are repositories, and the repository the folder is in, up to the
    // code folder. Without a code folder, none. A folder in a repository is not looked through
    // for more: one nested in another is rare, and the folder may hold thousands of folders.
    static (HashSet<string> Repos, string? Containing) FindRepos(List<FileEntry> entries, string path, string? codeFolder)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (codeFolder == null)
        {
            return (found, null);
        }

        var containing = CodeCommands.RepoContaining(path, codeFolder);
        if (containing != null)
        {
            return (found, containing);
        }

        foreach (var entry in entries)
        {
            if (entry.IsDirectory &&
                CodeCommands.IsRepo(entry.FullPath))
            {
                found.Add(entry.FullPath);
            }
        }

        return (found, null);
    }

    // Lets go of the repositories' states, and of reading them: the folder is another.
    void StopGitState()
    {
        gitVersion++;
        gitCancel?.Cancel();
        gitCancel = null;
        gitPending = false;
        gitStates.Clear();
        gitSites.Clear();
        gitCells = GitCells.None;
        repos = new(StringComparer.OrdinalIgnoreCase);
        containingRepo = null;
        gitReadAt = 0;
        GitReading = Task.CompletedTask;
    }

    /// <summary>
    /// Reads the states again, as <see cref="ReadGitState"/> does, unless they were read only
    /// a moment ago: the window came back to the front.
    /// </summary>
    public void ReadGitStateUnlessFresh()
    {
        // Watched, only what has changed is read, which is nothing if nothing has.
        if (!GitCache.IsWatching(settings.CodeDirectory) &&
            gitReadAt != 0 &&
            Stopwatch.GetElapsedTime(gitReadAt) < gitFresh)
        {
            return;
        }

        ReadGitState();
    }

    // The thread the view was made on, its window's, for what is heard on others to go back to.
    SynchronizationContext? madeOn;

    // Repositories changed, somewhere, heard on a background thread: those this view shows are
    // read again, once it shows.
    void OnGitStale(IReadOnlyCollection<string> stale)
    {
        // Told of every repository changed, in every view: only one that shows one of them, and
        // can be seen, has its window's thread take it up. One that can't reads once it shows.
        if (!Shows(stale))
        {
            return;
        }

        if (madeOn != null &&
            SynchronizationContext.Current != madeOn)
        {
            madeOn.Post(_ => OnGitStale(stale), null);
            return;
        }

        ReadGitState();
    }

    // On any thread: the repositories are never changed once found, only others found in their
    // place, and whoever finds them or shows the view then reads what has gone stale.
    bool Shows(IReadOnlyCollection<string> stale)
    {
        if (!ShowsRepos())
        {
            return false;
        }

        var listed = repos;
        var containing = containingRepo;
        foreach (var repo in stale)
        {
            if (listed.Contains(repo) ||
                Locations.Same(repo, containing))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the view shows a repository now.
    bool ShowsRepos() =>
        showing &&
        !IsDisposed &&
        (containingRepo != null || repos.Count > 0);

    // Whether a column that says something of the repositories listed shows: Git, or Tag.
    bool ShowsRepoColumn =>
        IsColumnShown(gitColumn) ||
        IsColumnShown(tagColumn);

    /// <summary>
    /// Reads again the state of the repository the folder is in, and of those it lists while
    /// the Git or Tag column shows: the folder was read, or the window came back to the front, where
    /// a commit made in a terminal meanwhile changed it, or a git action finished. A view nobody
    /// can see reads once it shows.
    /// </summary>
    public void ReadGitState()
    {
        if (!showing ||
            IsDisposed)
        {
            return;
        }

        var wanted = new List<string>();
        if (containingRepo != null)
        {
            wanted.Add(containingRepo);
        }

        // In the order they are listed, so those at the top are read first.
        if (ShowsRepoColumn)
        {
            wanted.AddRange(
                displayed
                    .Where(_ => repos.Contains(_.FullPath))
                    .Select(_ => _.FullPath));
        }

        if (wanted.Count == 0)
        {
            return;
        }

        if (settings.CodeDirectory is { } code)
        {
            GitCache.Watch(code);
        }

        // What was last read of each shows at once, and only those that have changed since, or
        // were never read, are read, as TortoiseGit does.
        var toRead = ShowCached(wanted);
        if (toRead.Count == 0)
        {
            return;
        }

        if (gitCancel != null)
        {
            gitPending = true;
            return;
        }

        var cancel = new CancelSource();
        gitCancel = cancel;
        GitReading = ReadGitStateAsync(toRead, gitVersion, cancel);
    }

    // Shows what was last read of each repository wanted, in any view, giving those to read: the
    // ones never read, and those changed since.
    List<string> ShowCached(List<string> wanted)
    {
        var toRead = new List<string>();
        var any = false;
        foreach (var repo in wanted)
        {
            if (GitCache.TryGet(repo, out var state, out var site, out var fresh))
            {
                if (!gitStates.TryGetValue(repo, out var shown) ||
                    shown != state ||
                    gitSites.GetValueOrDefault(repo) != site)
                {
                    gitStates[repo] = state;
                    if (site == null)
                    {
                        gitSites.Remove(repo);
                    }
                    else
                    {
                        gitSites[repo] = site;
                    }

                    any = true;
                }

                if (fresh)
                {
                    continue;
                }
            }

            toRead.Add(repo);
        }

        if (any)
        {
            GitStatesChanged();
        }

        return toRead;
    }

    // Shown as each is read, then all of them once they are, so none is missed when it is done.
    async Task ReadGitStateAsync(List<string> wanted, int version, CancelSource cancel)
    {
        using (cancel)
        {
            IProgress<GitRead> progress = new Progress<GitRead>(_ => GitStateRead(version, _));
            var reader = ReadGit;
            var token = cancel.Token;
            List<GitRead> read;
            try
            {
                read = await Task.Run(
                    () => CodeCommands.InParallel(
                        wanted,
                        repo =>
                        {
                            // Read once between the views showing it, where there are several.
                            var found = GitCache.Read(repo, token, () => (reader(repo, token), RemoteSite.OfRepo(repo)));
                            var state = new GitRead(repo, found.State, found.Site, found.Changes);

                            // Let go of, the read may have stopped short, which is nothing to keep.
                            if (!token.IsCancellationRequested)
                            {
                                progress.Report(state);
                            }

                            return state;
                        },
                        cancel: token),
                    token);
            }
            catch (OperationCanceledException)
            {
                // Let go of before it started: the folder is another, or the view closed.
                return;
            }

            if (version != gitVersion ||
                IsDisposed)
            {
                return;
            }

            gitCancel = null;
            gitReadAt = Stopwatch.GetTimestamp();
            foreach (var state in read)
            {
                GitStateRead(version, state);
            }

            // Shown now, rather than once the list gets round to it, so that once this is done
            // the list shows them.
            if (gitStatesArriving)
            {
                gitStatesArriving = false;
                ShowGitStates();
            }

            if (gitPending)
            {
                gitPending = false;
                ReadGitState();
            }
        }
    }

    // Changes is how many changes had been seen in the repository as it was read.
    sealed record GitRead(string Repo, GitState? State, RemoteSite? Site, int Changes);

    void GitStateRead(int version, GitRead read)
    {
        GitCache.Store(read.Repo, read.State, read.Site, read.Changes);

        if (version != gitVersion ||
            IsDisposed)
        {
            return;
        }

        var siteChanged = gitSites.GetValueOrDefault(read.Repo) != read.Site;
        if (read.Site == null)
        {
            gitSites.Remove(read.Repo);
        }
        else
        {
            gitSites[read.Repo] = read.Site;
        }

        if (read.State == null)
        {
            if (!gitStates.Remove(read.Repo) &&
                !siteChanged)
            {
                return;
            }
        }
        else
        {
            if (gitStates.TryGetValue(read.Repo, out var known) &&
                known == read.State &&
                !siteChanged)
            {
                return;
            }

            gitStates[read.Repo] = read.State;
        }

        GitStatesChanged();
    }

    // The states arrive one by one. The rows are made again for them once, after as many as have
    // come, and the list sorted again if it is sorted by them, unless a name is being edited,
    // which sorting would end.
    void GitStatesChanged()
    {
        if (!IsHandleCreated)
        {
            ShowGitStates();
            return;
        }

        if (gitStatesArriving)
        {
            return;
        }

        gitStatesArriving = true;
        BeginInvoke(() =>
        {
            // Shown already, as the reading finished first.
            if (!gitStatesArriving ||
                IsDisposed)
            {
                return;
            }

            gitStatesArriving = false;
            ShowGitStates();
        });
    }

    void ShowGitStates()
    {
        GitState? containing = null;
        if (containingRepo != null)
        {
            gitStates.TryGetValue(containingRepo, out containing);
        }

        gitCells = new(gitStates, containingRepo, containing);
        if (sortColumn is gitColumn or tagColumn &&
            !editing)
        {
            entriesSort = (-1, false);
            foundSort = (-1, false);
        }

        rowsStale = true;
        Populate(true);
    }

    // A drive of This PC's that said how big it is only after it was listed without, put in the
    // list as it now is, unless the list has been read again, or left, meanwhile.
    async Task SizeLate(int version, Task<FileEntry> sizing)
    {
        var sized = await sizing;
        if (version != loadVersion ||
            IsDisposed)
        {
            return;
        }

        var index = entries.FindIndex(_ => Locations.Same(_.FullPath, sized.FullPath));
        if (index < 0)
        {
            return;
        }

        entries[index] = sized;
        entryEdits++;

        // Sorted again, as its size may move it.
        entriesSort = (-1, false);
        Populate(true);
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
        readAgainAt = Environment.TickCount64 + (long) WaitAfter(read).TotalMilliseconds;

    static FileEntry DriveEntry(DriveInfo drive) =>
        DriveEntry(
            drive.Name,
            () =>
            {
                if (!drive.IsReady)
                {
                    return (0, 0);
                }

                return (drive.TotalSize, drive.AvailableFreeSpace);
            });

    /// <summary>
    /// A drive's row in This PC. One that can't say how big it is, gone between listing and
    /// asking, or refusing access, is listed without its size, rather than failing the whole
    /// listing.
    /// </summary>
    internal static FileEntry DriveEntry(string name, Func<(long Total, long Free)> size)
    {
        long total = 0;
        long free = 0;
        try
        {
            (total, free) = size();
        }
        catch (Exception)
        {
            // Gone meanwhile, IOException, or refusing access, UnauthorizedAccessException.
        }

        return new(name, name, true, total, default, FileAttributes.Directory, IsDrive: true, FreeSpace: free);
    }

    // How long This PC's read waits for its drives' sizes. A drive that answers later, one mapped
    // to a share that is offline or a disk that has to spin up, is listed without its size, which
    // follows once it has answered.
    static TimeSpan drivesWait = TimeSpan.FromMilliseconds(250);

    /// <param name="late">This PC's drives that had not said how big they are in time, each as
    /// it will be listed once it has.</param>
    static List<FileEntry> Enumerate(string path, bool showHidden, SystemItems? hiddenSystem, out List<Task<FileEntry>> late)
    {
        var result = new List<FileEntry>();
        late = [];
        if (Locations.IsThisPC(path))
        {
            // Asked all at once, each on its own, so a slow drive holds up only itself.
            var drives = DriveInfo.GetDrives();
            var sizing = drives
                .Select(_ => Task.Run(() => DriveEntry(_)))
                .ToArray();
            Task.WaitAll(sizing, drivesWait);
            foreach (var (drive, sized) in drives.Zip(sizing))
            {
                if (sized.IsCompletedSuccessfully)
                {
                    result.Add(sized.Result);
                    continue;
                }

                result.Add(new(drive.Name, drive.Name, true, 0, default, FileAttributes.Directory, IsDrive: true));
                late.Add(sized);
            }

            return result;
        }

        if (Locations.IsServer(path))
        {
            foreach (var (name, hidden) in Shares.List(path))
            {
                if (hidden &&
                    !showHidden)
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

        if (Zip.Split(path) is { } inArchive)
        {
            return Zip.List(inArchive, path);
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
        foundCapped = false;
        found = [];
        unmerged = [];
        merging = null;
        mergeGeneration++;
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
        var includeIgnored = settings.SearchIgnored;
        var hiddenSystem = settings.HiddenSystemItems();
        var sort = (sortColumn, sortAscending);

        // Reported on the UI thread, each in turn, and all of them before the search ends here.
        // Each is merged into what was found off the UI thread, which then only gives the list
        // its rows. How long that took sets how long the search holds the next.
        var pace = new SearchPace();
        var progress = new Progress<FoundBatch>(batch => AddFound(batch, pace, cancel));
        var cells = gitCells;
        var most = MostFound;
        var capped = false;
        try
        {
            await Task.Delay(searchDelay, cancel);
            capped = await Task.Run(() => SearchSubfolders(root, pattern, showHidden, includeIgnored, hiddenSystem, sort, cells, most, progress, pace, cancel), cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException)
        {
            // The folder went, or a subfolder could not be read part way through. What was
            // found before that stays.
        }
        catch (Exception exception)
        {
            // Not the folder: Lode's own. Reported here, as nothing awaits the search, and what
            // was found before it stays.
            IssueLauncher.LaunchForException("Search failed", exception);
        }

        if (cancel.IsCancellationRequested ||
            IsDisposed)
        {
            return;
        }

        // Everything the search sent is shown before it is said to be done.
        while (merging is { } pending)
        {
            await pending;
            if (cancel.IsCancellationRequested ||
                IsDisposed)
            {
                return;
            }
        }

        searching = false;
        foundCapped = capped;
        UpdateEmptyText();
        QueueStatusChanged();
    }

    /// <summary>
    /// Some of what the search found, and the order it was put in, which is the list's when the
    /// search started.
    /// </summary>
    sealed record FoundBatch(List<FileEntry> Entries, (int Column, bool Ascending) Sort);

    /// <summary>
    /// How long a search holds what it finds before handing it over: <see cref="searchBatch"/>,
    /// or, once showing what was found has come to take long, as a folder's reads go
    /// (<see cref="WaitAfter"/>), so the window spends no more than a tenth of its time on it.
    /// Set on the UI thread, read on the search's.
    /// </summary>
    internal sealed class SearchPace
    {
        long holdTicks = searchBatch.Ticks;

        public TimeSpan Hold =>
            TimeSpan.FromTicks(Interlocked.Read(ref holdTicks));

        public void Shown(TimeSpan took)
        {
            var hold = WaitAfter(took);
            if (hold < searchBatch)
            {
                hold = searchBatch;
            }

            Interlocked.Exchange(ref holdTicks, hold.Ticks);
        }
    }

    // On the UI thread. Merged with the others sent meanwhile, once the merge under way is done.
    void AddFound(FoundBatch batch, SearchPace pace, Cancel cancel)
    {
        if (cancel.IsCancellationRequested ||
            IsDisposed)
        {
            return;
        }

        unmerged.Add(batch);
        if (merging != null)
        {
            return;
        }

        // A merge of little may be done by the time it returns, and has then said so itself,
        // before this could keep it. Kept after that, it would never be let go of: nothing sent
        // later would be merged, and the search, waiting for a merge already over, never end.
        var merge = MergeFound(pace, mergeGeneration, cancel);
        if (!merge.IsCompleted)
        {
            merging = merge;
        }
    }

    /// <summary>
    /// What a merge off the UI thread made: everything found, and the list's rows, the folder's
    /// own matches among it.
    /// </summary>
    sealed record MergedFound(List<FileEntry> Found, List<FileEntry> Visible);

    // Merges what the search sent into what it found, and that with the folder's own matches,
    // off the UI thread, which then only gives the list its rows. What it merges is never changed
    // where it is, only replaced: replaced meanwhile, by a rename, a delete or a sort, the merge
    // is of what was, and is made again of what is.
    async Task MergeFound(SearchPace pace, int generation, Cancel cancel)
    {
        try
        {
            while (unmerged.Count > 0)
            {
                var batches = unmerged;
                unmerged = [];
                var column = sortColumn;
                var ascending = sortAscending;
                var cells = gitCells;
                var before = found;
                var beforeSort = foundSort;
                var beforeEntries = entries;
                var beforeEdits = entryEdits;
                var beforeFilter = filter;
                var own = OwnMatches(beforeFilter);
                var ownSorted = entriesSort == (column, ascending);
                var merged = await Task.Run(
                    () => MergeBatches(before, beforeSort == (column, ascending), batches, own, ownSorted, column, ascending, cells),
                    cancel);
                if (cancel.IsCancellationRequested ||
                    IsDisposed ||
                    generation != mergeGeneration)
                {
                    return;
                }

                if (!ReferenceEquals(found, before) ||
                    foundSort != beforeSort ||
                    !ReferenceEquals(entries, beforeEntries) ||
                    entryEdits != beforeEdits ||
                    !ReferenceEquals(gitCells, cells) ||
                    filter != beforeFilter ||
                    (sortColumn, sortAscending) != (column, ascending))
                {
                    unmerged.InsertRange(0, batches);
                    continue;
                }

                found = merged.Found;
                foundSort = (column, ascending);
                var started = Stopwatch.GetTimestamp();
                ShowFound(merged.Visible);
                pace.Shown(Stopwatch.GetElapsedTime(started));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (generation == mergeGeneration)
            {
                merging = null;
            }
        }
    }

    // Off the UI thread: none of what it is given is changed, only read.
    static MergedFound MergeBatches(
        List<FileEntry> found,
        bool foundSorted,
        List<FoundBatch> batches,
        List<FileEntry> own,
        bool ownSorted,
        int column,
        bool ascending,
        GitCells cells)
    {
        var compare = Order(column, ascending, cells);
        var all = found;
        if (!foundSorted)
        {
            all = [.. all];
            all.Sort(compare);
        }

        var batch = batches[0].Entries;
        if (batches.Count > 1 ||
            batches[0].Sort != (column, ascending))
        {
            batch = [.. batches.SelectMany(_ => _.Entries)];
            batch.Sort(compare);
        }

        all = Merge(all, batch, compare);
        if (!ownSorted)
        {
            own.Sort(compare);
        }

        return new(all, Merge(own, all, compare));
    }

    // Shows what the search has found, unless a name is being edited, which a new set of rows
    // would end: then once the edit is over. Given the rows when they are merged already.
    void ShowFound(List<FileEntry>? visible = null)
    {
        if (editing)
        {
            foundPending = true;
            return;
        }

        foundPending = false;
        Populate(true, visible);
    }

    // Everything beneath the folder's subfolders whose name the filter matches, reported a batch
    // at a time, each sorted. Not the folder's own items, which its entries have. Leaves out what
    // the folder's own listing would: hidden items, unless they are shown, and system ones. In a
    // repository, what git ignores too, unless told not to, as Find in Files does: an ignored
    // folder, bin or node_modules say, isn't gone into at all. Never .git, nor what other version
    // control keeps for itself. Stops once it has found the most it may, and says whether it did.
    static bool SearchSubfolders(
        string root,
        string pattern,
        bool showHidden,
        bool includeIgnored,
        SystemItems? hiddenSystem,
        (int Column, bool Ascending) sort,
        GitCells cells,
        int most,
        IProgress<FoundBatch> progress,
        SearchPace pace,
        Cancel cancel)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? default : FileAttributes.Hidden
        };
        var compare = Order(sort.Column, sort.Ascending, cells);
        var batch = new List<FileEntry>();
        var sent = Stopwatch.GetTimestamp();
        var count = 0;

        void Send()
        {
            batch.Sort(compare);
            progress.Report(new(batch, sort));
            batch = [];
            sent = Stopwatch.GetTimestamp();
        }

        // What git doesn't ignore in a repository, as Find in Files lists it, kept until the
        // repository changes. Null where git couldn't list it, which is searched whole.
        GitScope? ScopeOf(string top) =>
            GitCache.Searchable(top, () => GitScope.List(top, cancel));

        // A repository is listed from when the walk comes to it, several at once, not when it
        // is gone into: listing starts git, and one after another, a folder of repositories
        // was a wait on each in turn.
        var listers = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, 8));

        async Task<GitScope?> ListSoon(string top)
        {
            await listers.WaitAsync(cancel);
            try
            {
                return await Task.Run(() => ScopeOf(top), cancel);
            }
            finally
            {
                listers.Release();
            }
        }

        // A folder still to go through, and what git doesn't ignore where it is: null for all.
        // A repository of its own is being listed.
        var stack = new Stack<(string Folder, GitScope? Scope, Task<GitScope?>? Listing)>();
        GitScope? rootScope = null;
        if (!includeIgnored &&
            SearchWalk.RepoAbove(root) is { } top)
        {
            rootScope = ScopeOf(top);
        }

        // The folder's own items are its entries: only its subfolders are gone into. Each
        // folder's subfolders go on the stack last first, so they are gone into in the order
        // they are listed.
        PushAll(Read(root, onlyFolders: true).Select(_ => _.Subfolder).OfType<string>().ToList(), rootScope);
        while (stack.Count > 0)
        {
            var (folder, scope, listing) = stack.Pop();
            if (listing != null)
            {
                scope = listing.GetAwaiter().GetResult();
            }

            var subfolders = new List<string>();
            foreach (var (subfolder, match) in Read(folder, onlyFolders: false))
            {
                if (match != null &&
                    (scope == null || IsNotIgnored(scope, match)))
                {
                    batch.Add(match);
                    count++;
                    if (count >= most)
                    {
                        Send();
                        return true;
                    }
                }

                if (subfolder != null)
                {
                    subfolders.Add(subfolder);
                }
            }

            PushAll(subfolders, scope);
        }

        if (batch.Count > 0)
        {
            Send();
        }

        return false;

        void PushAll(List<string> folders, GitScope? scope)
        {
            for (var index = folders.Count - 1; index >= 0; index--)
            {
                Push(folders[index], scope);
            }
        }

        // Gone into, unless git ignores it. A repository of its own is gone into by what it
        // doesn't ignore.
        void Push(string folder, GitScope? scope)
        {
            if (scope != null &&
                !scope.HasFolder(folder))
            {
                return;
            }

            Task<GitScope?>? listing = null;
            if (!includeIgnored &&
                SearchWalk.IsRepo(folder))
            {
                listing = ListSoon(folder);
            }

            stack.Push((folder, scope, listing));
        }

        // What a folder holds that is searched: its subfolders to go into, and what matches.
        IEnumerable<(string? Subfolder, FileEntry? Match)> Read(string folder, bool onlyFolders)
        {
            var items = new FileSystemEnumerable<(string? Subfolder, FileEntry? Match)>(
                folder,
                (ref entry) =>
                {
                    string? subfolder = null;
                    if (entry.IsDirectory &&
                        SearchWalk.CanSearch(ref entry, hiddenSystem))
                    {
                        subfolder = entry.ToFullPath();
                    }

                    FileEntry? match = null;
                    if (!onlyFolders &&
                        Matches(pattern, entry.FileName) &&
                        !SearchWalk.IsSystem(ref entry, hiddenSystem))
                    {
                        match = ToEntry(ref entry);
                    }

                    return (subfolder, match);
                },
                options)
            {
                // Asked of every item, matching or not, so it is where the search notices it has
                // been let go of, and sends what it has found once it has been holding it a while.
                ShouldIncludePredicate = (ref entry) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    if (batch.Count > 0 &&
                        Stopwatch.GetElapsedTime(sent) >= pace.Hold)
                    {
                        Send();
                    }

                    if (entry.IsDirectory)
                    {
                        return !SearchWalk.IsVersionControl(entry.FileName);
                    }

                    return !onlyFolders &&
                           Matches(pattern, entry.FileName);
                }
            };
            try
            {
                return items.ToList();
            }
            catch (IOException)
            {
                // The folder went while it was being searched, or its drive did.
                return [];
            }
        }
    }

    // Whether git doesn't ignore what matched.
    static bool IsNotIgnored(GitScope scope, FileEntry match)
    {
        if (match.IsDirectory)
        {
            return scope.HasFolder(match.FullPath);
        }

        return scope.HasFile(match.FullPath);
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
    internal static List<FileEntry> Merge(List<FileEntry> first, List<FileEntry> second, Comparison<FileEntry> compare)
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

    // Sorted the other way by the same column is the same order backwards, the folders still
    // above the files: turned round, rather than sorted again, which for a folder of a hundred
    // thousand is over a million comparisons, each a call into Windows.
    static void TurnRound(List<FileEntry> sorted)
    {
        var files = sorted.FindIndex(_ => !_.IsDirectory);
        if (files < 0)
        {
            files = sorted.Count;
        }

        sorted.Reverse(0, files);
        sorted.Reverse(files, sorted.Count - files);
    }

    // Gives the list its rows: the entries the filter matches, with what the search found, unless
    // a merge off the UI thread has made them already.
    void Populate(bool preserveState, List<FileEntry>? merged = null)
    {
        // Before sorting: hiding the column the list is sorted by sorts it by name.
        var columnsChanged = UpdateColumns();
        if (entriesSort == (sortColumn, !sortAscending))
        {
            TurnRound(entries);
            entriesSort = (sortColumn, sortAscending);
        }
        else if (entriesSort != (sortColumn, sortAscending))
        {
            var column = sortColumn;
            var ascending = sortAscending;
            entries.Sort((first, second) => Compare(first, second, column, ascending, gitCells));
            entriesSort = (column, ascending);
        }

        if (foundSort == (sortColumn, !sortAscending))
        {
            // A new list, not the one a merge may be reading.
            found = [.. found];
            TurnRound(found);
            foundSort = (sortColumn, sortAscending);
        }
        else if (foundSort != (sortColumn, sortAscending))
        {
            found = [.. found];
            found.Sort(Order(sortColumn, sortAscending, gitCells));
            foundSort = (sortColumn, sortAscending);
        }

        // In order already, as what it is taken from is.
        List<FileEntry> visible;
        if (merged != null &&
            !columnsChanged)
        {
            visible = merged;
            filtered = (entries, found, filter, sortColumn, sortAscending, gitCells, entryEdits, merged);
        }
        else
        {
            visible = ApplyFilter();
        }

        if (preserveState &&
            pendingRename == null &&
            pendingSelection == null &&
            !columnsChanged &&
            (ReferenceEquals(visible, displayed) ||
             visible.SequenceEqual(displayed)))
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

        // The entries selected and focused, to find in the new rows by their order.
        List<FileEntry>? probes = null;
        if (preserveState)
        {
            probes = SelectedEntries();
            foreach (var entry in probes)
            {
                selected.Add(entry.FullPath);
            }

            if (list.FocusedItem?.Tag is FileEntry focusedEntry)
            {
                focused = focusedEntry.FullPath;
                if (!selected.Contains(focused))
                {
                    probes.Add(focusedEntry);
                }
            }

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

        var reveal = pendingSelection ?? (pendingRename == null ? null : [pendingRename]);
        if (reveal != null)
        {
            selected.Clear();
            selected.UnionWith(reveal);
            focused = reveal[0];
            topIndex = -1;
            probes = null;
        }

        pendingSelection = null;

        using (list.SuspendPainting())
        {
            SetColumnText(dateColumn, IsThisPC ? "Free space" : "Date modified");
            SetColumnText(sizeColumn, IsThisPC ? "Total size" : "Size");
            ShowRows(visible);
            Restore(selected, focused, probes);

            if (topIndex > 0 &&
                visible.Count > 0)
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

    /// <summary>
    /// The column the list is sorted by, and which way. For tests.
    /// </summary>
    internal (int Column, bool Ascending) Sort => (sortColumn, sortAscending);

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

        if (column == gitColumn &&
            !HasGitColumn)
        {
            return false;
        }

        if (column == tagColumn &&
            !HasTagColumn)
        {
            return false;
        }

        return !settings.HiddenColumns.Contains(columnDefinitions[column].Key, StringComparer.OrdinalIgnoreCase);
    }

    // Git is for a folder in a repository, its items' changes, a search's from all through it
    // included, and for one listing repositories, their states. A search of that one finds items
    // from inside the repositories, which have no state of their own.
    bool HasGitColumn => containingRepo != null ||
                         (repos.Count > 0 && !IsSearch);

    // Tag is for a folder listing repositories alone: in a repository, the files and folders
    // have no tag of their own, and nor do the items a search finds inside the repositories.
    bool HasTagColumn => repos.Count > 0 &&
                         !IsSearch;

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

        // The list can ask for a row at any time, and a row must have a cell for every column the
        // list has. So the columns go first, then the rows made for them are forgotten and made
        // from here on for the columns wanted, and only then are those added.
        list.Columns.Clear();
        shownColumns = wanted;
        rows.Clear();
        foreach (var column in wanted)
        {
            list.Columns.Add(ColumnTitle(column), WidthOf(column), columnDefinitions[column].Alignment);
        }

        // Sorted by a column taken away, Folder as a search ends say, the list goes back to how
        // its folder is sorted, or by Name if that is the column gone.
        if (!shownColumns.Contains(sortColumn))
        {
            (sortColumn, sortAscending) = SortOfFolder();
        }

        return true;
    }

    // How wide a column is, in pixels at the list's scale: as the user last sized it, in any
    // view, or as it is built.
    int WidthOf(int column)
    {
        var definition = columnDefinitions[column];
        if (!settings.ColumnWidths.TryGetValue(definition.Key, out var width))
        {
            width = definition.Width;
        }

        return LogicalToDeviceUnits(width);
    }

    /// <summary>
    /// Brings the columns' widths in line with the settings, as another view has sized one.
    /// </summary>
    public void RefreshColumnWidths()
    {
        for (var index = 0; index < shownColumns.Count; index++)
        {
            var width = WidthOf(shownColumns[index]);
            if (list.Columns[index].Width != width)
            {
                list.Columns[index].Width = width;
            }
        }
    }

    // On a monitor at another scale, the same widths, at that scale.
    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        RefreshColumnWidths();
    }

    // A column the user sized, by where it is among those shown: as wide in every view from now
    // on, and the next time Lode starts. Kept at 100% scale, so it is as wide on any monitor.
    void KeepWidth(int shown, int width)
    {
        if (shown < 0 ||
            shown >= shownColumns.Count)
        {
            return;
        }

        var key = columnDefinitions[shownColumns[shown]].Key;
        settings.ColumnWidths[key] = (int) Math.Round(width * 96.0 / DeviceDpi);
        ColumnWidthsChanged?.Invoke(this);
    }

    // How the folder open was last sorted, by a column shown: by Name, A to Z, unless it was
    // sorted otherwise.
    (int Column, bool Ascending) SortOfFolder()
    {
        if (settings.SortOf(CurrentPath) is { } sort)
        {
            var column = Array.FindIndex(columnDefinitions, _ => _.Key == sort.Column);
            if (column >= 0 &&
                IsColumnShown(column))
            {
                return (column, sort.Ascending);
            }
        }

        return (nameColumn, true);
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

            return RowCells(0);
        }
    }

    /// <summary>
    /// A row's cells, by where it is in what is displayed, one per column shown. For tests.
    /// </summary>
    internal List<string> RowCells(int index) =>
        Row(index).SubItems
            .Cast<ListViewItem.ListViewSubItem>()
            .Select(_ => _.Text)
            .ToList();

    /// <summary>
    /// The colour of a row's cell, by where the row is in what is displayed and where the column
    /// is among those shown. For tests.
    /// </summary>
    internal Color CellColor(int index, int shown) =>
        Row(index).SubItems[shown].ForeColor;

    /// <summary>
    /// The image a row shows, by where it is in what is displayed, as an index into the lists'
    /// shared images. For tests.
    /// </summary>
    internal int RowIcon(int index) =>
        Row(index).ImageIndex;

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
        if (stale &&
            entries.Count == 0)
        {
            return;
        }

        Populate(true);

        // Git or Tag shown again: the repositories' states weren't read while both were hidden.
        if (ShowsRepoColumn &&
            repos.Any(_ => !gitStates.ContainsKey(_)))
        {
            ReadGitState();
        }
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
            // Hidden or shown only while there is a search, or are repositories, to show it for.
            if ((column == folderColumn && !IsSearch) ||
                (column == gitColumn && !HasGitColumn) ||
                (column == tagColumn && !HasTagColumn))
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
    /// measuring, every row it asks for has that in the column. Every view then has it that wide.
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

        KeepWidth(shown, list.Columns[shown].Width);
    }

    // The text of the widest cell a column has, in any row.
    string WidestCell(int column)
    {
        var widest = "";
        var widestWidth = 0;

        // Dates, types and sizes repeat. Each is measured once.
        var measured = new HashSet<string>();
        var measure = new ShellListView.TextMeasure(list);
        foreach (var entry in displayed)
        {
            var text = entry.IsDrive ? DriveCell(entry, column) : Cell(entry, column);
            if (!measured.Add(text))
            {
                continue;
            }

            var width = measure.Width(text);
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

    // A drive's cells, one for every column, and its icon, as the shell describes it. A drive is
    // described off the UI thread, and its row made again once it has been.
    (string[] Cells, int Icon) DescribeDrive(FileEntry entry)
    {
        var (displayName, typeName, icon) = ShellIcons.DescribeDrive(entry.FullPath, DriveDescribed);
        string[] cells =
        [
            displayName,
            "",
            "",
            "",
            DriveSize(entry, entry.FreeSpace),
            typeName,
            DriveSize(entry, entry.Size)
        ];
        return (cells, icon);
    }

    // What a drive shows in a column, asking the shell only for the columns it names: sizing
    // the size columns to fit asks nothing of it.
    string DriveCell(FileEntry entry, int column)
    {
        switch (column)
        {
            case folderColumn:
            case gitColumn:
            case tagColumn:
                return "";
            case dateColumn:
                return DriveSize(entry, entry.FreeSpace);
            case sizeColumn:
                return DriveSize(entry, entry.Size);
        }

        return DescribeDrive(entry).Cells[column];
    }

    // Nothing for a drive that has not said how big it is.
    static string DriveSize(FileEntry entry, long size)
    {
        if (entry.Size > 0)
        {
            return Formatting.Size(size);
        }

        return "";
    }

    // An item's icon in the system image list, and the overlay drawn over it. A shortcut's icon
    // is what it points at, with the shell's arrow over it, as Explorer has it. A file promoted
    // to a repository's root is as good as a shortcut to it, so has the arrow too. In a zip there
    // is no file to read for one, so each has its type's. One whose own icon isn't read yet has
    // its type's until it is, when known is called.
    static (int Icon, int Overlay) IconOf(FileEntry entry, Action known)
    {
        if (entry.InArchive)
        {
            return (ShellIcons.GetTypeIcon(entry.Name, entry.IsDirectory), 0);
        }

        var overlay = ShellIcons.ShortcutOverlay;
        if (!entry.Promoted)
        {
            overlay = ShellIcons.GetOverlay(entry.FullPath, entry.IsDirectory);
        }

        return (ShellIcons.GetIcon(entry.FullPath, entry.IsDirectory, entry.Modified, known), overlay);
    }

    // Programs' and shortcuts' own icons arrive one by one, read off the UI thread as their
    // rows are first made. The rows are made again for them once, after as many as have come.
    bool fileIconsArriving;

    void FileIconKnown()
    {
        if (IsDisposed ||
            fileIconsArriving)
        {
            return;
        }

        // Without a window yet, nothing has been drawn: the rows are made with it as they are.
        if (!IsHandleCreated)
        {
            rows.Clear();
            return;
        }

        fileIconsArriving = true;
        BeginInvoke(() =>
        {
            fileIconsArriving = false;
            rows.Clear();
            list.Invalidate();
        });
    }

    // Something cut, or pasted, here or in Explorer: the rows are drawn again, as dimmed or not.
    void OnCutChanged()
    {
        if (IsDisposed ||
            !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(OnCutChanged);
            return;
        }

        list.Invalidate();
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

                return Formatting.Modified(entry.Modified);
            case typeColumn:
                return ShellIcons.GetTypeName(entry.FullPath, entry.IsDirectory);
            case gitColumn:
                return gitCells.Text(entry);
            case tagColumn:
                return gitCells.Tag(entry);
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
        var folder = entry.Folder;
        if (folder.Length == 0 ||
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
            var drive = new ListViewItem(ShownCells(driveCells), ShellListView.PaddedIcon(driveIcon)) {Tag = entry};
            ColorDetails(drive, ExplorerStyle.SecondaryText);
            return drive;
        }

        string[] cells =
        [
            Cell(entry, nameColumn),
            Cell(entry, gitColumn),
            Cell(entry, tagColumn),
            Cell(entry, folderColumn),
            Cell(entry, dateColumn),
            Cell(entry, typeColumn),
            Cell(entry, sizeColumn)
        ];
        var (shellIcon, overlay) = IconOf(entry, FileIconKnown);
        var item = new ListViewItem(ShownCells(cells), ShellListView.PaddedIcon(shellIcon, overlay)) {Tag = entry};
        if (entry.IsHidden)
        {
            item.ForeColor = SystemColors.GrayText;
            ColorDetails(item, SystemColors.GrayText);
        }
        else
        {
            ColorDetails(item, ExplorerStyle.SecondaryText);
        }

        ColorChange(item, entry);
        return item;
    }

    // What has become of a file in a repository, in the colour for it, hidden or not: amber,
    // green or red stands out from the gray beside it, as the change is what to see first.
    void ColorChange(ListViewItem item, FileEntry entry)
    {
        var shown = shownColumns.IndexOf(gitColumn);
        if (shown >= 0 &&
            gitCells.Change(entry) is { } change)
        {
            item.SubItems[shown].ForeColor = ExplorerStyle.ChangeColor(change);
        }
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
    // Made again only when any of what it is made of has changed since it last was.
    List<FileEntry> ApplyFilter()
    {
        if (filtered is { } last &&
            ReferenceEquals(last.Entries, entries) &&
            ReferenceEquals(last.Found, found) &&
            last.Filter == filter &&
            last.Column == sortColumn &&
            last.Ascending == sortAscending &&
            ReferenceEquals(last.Cells, gitCells) &&
            last.Edits == entryEdits)
        {
            return last.Visible;
        }

        var visible = Filtered();
        filtered = (entries, found, filter, sortColumn, sortAscending, gitCells, entryEdits, visible);
        return visible;
    }

    List<FileEntry> Filtered()
    {
        if (filter.Length == 0)
        {
            return [.. entries];
        }

        var matches = OwnMatches(filter);
        if (found.Count == 0)
        {
            return matches;
        }

        return Merge(matches, found, Order(sortColumn, sortAscending, gitCells));
    }

    // The folder's own items the filter matches. Not those promoted to a repository's root: the
    // search of the subfolders finds them where they are, and shows them with their folder, so
    // they would otherwise show twice.
    List<FileEntry> OwnMatches(string filter) =>
        entries
            .Where(_ => !_.Promoted &&
                        Matches(filter, _.Name))
            .ToList();

    internal static Comparison<FileEntry> Order(int column, bool ascending, GitCells? cells = null) =>
        (first, second) => Compare(first, second, column, ascending, cells);

    // Static, with the column and direction handed in, so a folder can be sorted off the UI
    // thread as it is read. By Git or Tag, by what the column says, which is handed in too.
    static int Compare(FileEntry first, FileEntry second, int column, bool ascending, GitCells? cells = null)
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
            folderColumn => StrCmpLogicalW(first.Folder, second.Folder),
            dateColumn => first.IsDrive
                ? first.FreeSpace.CompareTo(second.FreeSpace)
                : first.Modified.CompareTo(second.Modified),
            typeColumn => string.Compare(
                ShellIcons.GetTypeName(first.FullPath, first.IsDirectory),
                ShellIcons.GetTypeName(second.FullPath, second.IsDirectory),
                StringComparison.CurrentCultureIgnoreCase),
            sizeColumn => first.Size.CompareTo(second.Size),
            // By what the column says: repositories by branch, so those on a branch other than
            // the one most are on stand together; files by what has become of them.
            gitColumn => StrCmpLogicalW((cells ?? GitCells.None).Text(first), (cells ?? GitCells.None).Text(second)),
            // Numbers as numbers, so v1.10 comes after v1.9.
            tagColumn => StrCmpLogicalW((cells ?? GitCells.None).Tag(first), (cells ?? GitCells.None).Tag(second)),
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

    // What is selected that can be cut, copied, deleted or dragged as it is: not a drive or a
    // share, nor what is in a zip, which is copied or dragged by extracting it first.
    string[] SelectedPaths() =>
        SelectedEntries()
            .Where(_ => _ is {IsPlace: false, InArchive: false})
            .Select(_ => _.FullPath)
            .ToArray();

    void OpenEntries(IReadOnlyList<FileEntry> selection)
    {
        var navigated = false;
        foreach (var entry in selection)
        {
            // In a zip, a file is opened from a copy extracted to the temp folder, and a zip in
            // one is gone into there.
            if (entry is {InArchive: true, IsDirectory: false})
            {
                _ = OpenFromArchive(entry, !navigated);
                navigated |= Zip.IsZip(entry.Name);
                continue;
            }

            var folder = FolderToOpen(entry);
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

    // Where opening an item goes: a folder, a zip, as Explorer goes into one, or where a shortcut
    // to a folder leads. Null for a file to open.
    static string? FolderToOpen(FileEntry entry)
    {
        if (entry.IsDirectory ||
            Zip.IsZip(entry.FullPath))
        {
            return entry.FullPath;
        }

        return FileOperations.ResolveFolderShortcut(entry.FullPath);
    }

    /// <summary>
    /// Extracts a file in a zip to the temp folder, off the UI thread, and opens it from there:
    /// a zip in the zip by going into it, here or, unless <paramref name="here"/>, in a new tab;
    /// anything else with its app. What it has open is a copy, so changes to it stay out of the zip.
    /// </summary>
    internal async Task OpenFromArchive(FileEntry entry, bool here)
    {
        string extracted;
        try
        {
            extracted = (await Task.Run(() => Zip.ExtractToTemp([entry.FullPath])))[0];
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        if (IsDisposed)
        {
            return;
        }

        if (!Zip.IsZip(extracted))
        {
            FileOperations.Open(this, extracted);
            return;
        }

        if (here)
        {
            Navigate(extracted);
            return;
        }

        NewTabRequested?.Invoke(extracted);
    }

    void OnColumnClick(object? sender, ColumnClickEventArgs args) =>
        SortBy(shownColumns[args.Column]);

    /// <summary>
    /// Sorts by a column, as clicking its header does: first the way the column starts, then
    /// toggling (see <see cref="StartsAscending"/>). The folder is sorted so from then on, in
    /// any tab, and the next time Lode starts, as Explorer keeps each folder's. Folder, which
    /// only a search has, is for the search alone.
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
            sortAscending = StartsAscending(column);
        }

        if (column != folderColumn)
        {
            settings.SetSort(CurrentPath, columnDefinitions[column].Key, sortAscending);
        }

        if (entries.Count + found.Count >= SortOffThreadFrom &&
            entriesSort != (sortColumn, !sortAscending))
        {
            _ = SortThenPopulate();
            return;
        }

        Populate(true);
        ScrollToSelection();
    }

    // Sorted, the list keeps its place by row, which then holds other items: the one focused, or
    // else the first selected, is scrolled to where it can be seen, as in Explorer. With nothing
    // selected the list stays where it is.
    void ScrollToSelection()
    {
        if (list.FocusedItem is {Selected: true} focused)
        {
            focused.EnsureVisible();
            return;
        }

        if (list.SelectedIndices.Count > 0)
        {
            list.EnsureVisible(list.SelectedIndices[0]);
        }
    }

    // Which way a column is first sorted: Date modified newest first, as Explorer sorts it, the
    // others ascending. On This PC that column is Free space, which starts ascending as the
    // others do.
    bool StartsAscending(int column) =>
        column != dateColumn ||
        IsThisPC;

    // From this many items a sort by another column is done off the UI thread: of a hundred
    // thousand it is over a million comparisons, each a call into Windows, and by Type two
    // lookups besides, with the window not answering for all of it. Fewer are sorted before
    // anyone could tell, and the other way by the same column is only turned round.
    internal static int SortOffThreadFrom { get; set; } = 20_000;

    // Sorts copies of the entries and of what was found, by what the list is now to be sorted
    // by, and gives the list its rows once they are. Should anything of them have changed
    // meanwhile, the folder read again, another column clicked, the rows are given as they
    // then are, sorted here as before.
    async Task SortThenPopulate()
    {
        var column = sortColumn;
        var ascending = sortAscending;
        var cells = gitCells;
        var beforeEntries = entries;
        var beforeFound = found;
        var beforeEdits = entryEdits;
        List<FileEntry> sortedEntries = [.. entries];
        List<FileEntry> sortedFound = [.. found];
        await Task.Run(() =>
        {
            var compare = Order(column, ascending, cells);
            sortedEntries.Sort(compare);
            sortedFound.Sort(compare);
        });
        if (IsDisposed)
        {
            return;
        }

        if (ReferenceEquals(entries, beforeEntries) &&
            ReferenceEquals(found, beforeFound) &&
            ReferenceEquals(gitCells, cells) &&
            entryEdits == beforeEdits &&
            (sortColumn, sortAscending) == (column, ascending))
        {
            entries = sortedEntries;
            entriesSort = (column, ascending);
            found = sortedFound;
            foundSort = (column, ascending);
        }

        Populate(true);
        ScrollToSelection();
    }

    void OnListKeyDown(object? sender, KeyEventArgs args)
    {
        if (!HandleKey(args.KeyData))
        {
            return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
    }

    /// <summary>
    /// Does what a key pressed in the list does, giving whether it does anything. Tests press
    /// keys through it, as a message can't hold Ctrl or Shift down.
    /// </summary>
    internal bool HandleKey(Keys keyData)
    {
        switch (keyData)
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
            case Keys.Control | Keys.Shift | Keys.C:
                // As in Windows 11's Explorer.
                var selected = SelectedEntries()
                    .Where(_ => !_.IsPlace)
                    .Select(_ => _.FullPath)
                    .ToList();
                if (selected.Count > 0)
                {
                    CopyAsPath(selected);
                }

                break;
            case Keys.Control | Keys.Shift | Keys.N:
                CreateFolder();
                break;
            case Keys.Alt | Keys.Enter:
                ShowProperties();
                break;
            default:
                return false;
        }

        return true;
    }

    public void Copy(bool cut)
    {
        var selection = SelectedEntries();
        if (selection.Any(_ => _.InArchive))
        {
            // Nothing can be taken out of a zip, only copied.
            if (!cut)
            {
                _ = CopyOutOfArchive(selection.Select(_ => _.FullPath).ToList());
            }

            return;
        }

        // Nor is a file promoted to a repository's root cut from there, only copied.
        if (cut &&
            selection.Any(_ => _.Promoted))
        {
            return;
        }

        FileOperations.CopyToClipboard(SelectedPaths(), cut);
    }

    /// <summary>
    /// What is in a zip, extracted to the temp folder for <see cref="Copy"/> and drags. Replaced
    /// in tests.
    /// </summary>
    internal Func<IReadOnlyList<string>, IReadOnlyList<string>> ExtractToTemp = Zip.ExtractToTemp;

    // Items in a zip are on the clipboard as copies extracted to the temp folder, off the UI
    // thread, so they paste anywhere as files do.
    async Task CopyOutOfArchive(List<string> paths)
    {
        IReadOnlyList<string> extracted;
        try
        {
            extracted = await Task.Run(() => ExtractToTemp(paths));
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        FileOperations.CopyToClipboard(extracted, false);
    }

    public void Paste()
    {
        if (IsReadOnly)
        {
            return;
        }

        var clipboard = FileOperations.ReadClipboard();
        if (clipboard == null)
        {
            return;
        }

        Paste(clipboard);
    }

    /// <summary>
    /// Pastes what the clipboard holds, <paramref name="clipboard"/>, selecting it once it is
    /// there. Tests call it, leaving the clipboard alone.
    /// </summary>
    internal void Paste(FileOperations.ClipboardFiles clipboard)
    {
        if (IsReadOnly)
        {
            return;
        }

        Loading = PasteAndSelect(clipboard);
    }

    // What was pasted is selected once it is there, as Explorer has it: the folder is read
    // again for it, unless the view has gone somewhere else meanwhile.
    async Task PasteAndSelect(FileOperations.ClipboardFiles clipboard)
    {
        var folder = CurrentPath;
        var placed = await FileOperations.Paste(Owner, clipboard, folder);
        if (IsDisposed ||
            placed.Count == 0 ||
            !Locations.Same(folder, CurrentPath))
        {
            return;
        }

        pendingSelection = placed;
        Reload();
        await Loading;
    }

    /// <summary>
    /// Deletes items: to the Recycle Bin, or outright when permanent. Replaced in tests, which
    /// must not have the shell delete anything.
    /// </summary>
    internal Func<IntPtr, IReadOnlyCollection<string>, bool, Task<bool>> DeleteItems = FileOperations.Delete;

    public void Delete(bool permanent)
    {
        // A file promoted to a repository's root is a shortcut to it there, which is not how to
        // delete it, as the right-click menu says by not offering to.
        if (SelectedEntries().Any(_ => _.Promoted))
        {
            return;
        }

        var paths = SelectedPaths();
        if (paths.Length == 0)
        {
            return;
        }

        var deleting = DeleteItems(Owner, paths, permanent);
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
        if (gone.Count == 0)
        {
            return;
        }

        // A new list, not the one a merge may be reading.
        var kept = found.Where(_ => !IsWithin(_.FullPath, gone)).ToList();
        if (kept.Count < found.Count)
        {
            found = kept;
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
        if (list.FocusedItem is {Selected: true} item)
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
                pendingSelection = [FileOperations.CreateShortcut(entry.FullPath)];
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Loading = LoadAsync(true);
    }

    /// <summary>
    /// A zip of the selection beside it, named for the first item, shown with its name ready to
    /// edit once it is made, as Explorer has it. In what a search found, where the items can be in
    /// different folders, it goes in the first item's.
    /// </summary>
    internal void Compress(List<FileEntry> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        Loading = CompressAndRename(selection.Select(_ => _.FullPath).ToList(), selection[0].Folder);
    }

    async Task CompressAndRename(List<string> paths, string folder)
    {
        var zip = await Zip.CompressInBackground(paths, folder);
        if (IsDisposed ||
            zip == null ||
            !Locations.Same(folder, CurrentPath))
        {
            return;
        }

        pendingRename = zip;
        Reload();
        await Loading;
    }

    /// <summary>
    /// Extracts each zip: to a new folder beside it named for it, or, <paramref name="here"/>, into
    /// the folder it is in. What it put there is selected once it is done.
    /// </summary>
    internal void Extract(List<string> zips, bool here) =>
        Loading = ExtractAndSelect(zips, here);

    async Task ExtractAndSelect(List<string> zips, bool here)
    {
        var folder = CurrentPath;
        var placed = await Zip.ExtractInBackground(zips, here);
        if (IsDisposed ||
            placed.Count == 0 ||
            !Locations.Same(folder, CurrentPath))
        {
            return;
        }

        pendingSelection = placed;
        Reload();
        await Loading;
    }

    // Makes the item, then shows it with its name ready to edit.
    void Create(Func<string, string> create)
    {
        if (IsReadOnly)
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
            if (!selection.Any(_ => _.InArchive))
            {
                ShellContextMenu.ShowProperties(Owner, selection.Select(_ => _.FullPath).ToArray());
            }
        }
        else if (!IsReadOnly)
        {
            ShellContextMenu.InvokeVerb(Owner, [CurrentPath], "properties");
        }
    }

    internal void OnBeforeLabelEdit(object? sender, LabelEditEventArgs args)
    {
        if (args.Item >= displayed.Count ||
            displayed[args.Item] is {IsPlace: true} or {InArchive: true} ||
            // Renaming one renames every item selected, which a file promoted to a repository's
            // root is not to be renamed with.
            SelectedEntries().Any(_ => _.Promoted))
        {
            args.CancelEdit = true;
            return;
        }

        var entry = displayed[args.Item];

        editing = true;
        edited = entry;
        if (!entry.IsDirectory)
        {
            // Preselect the name but not the extension, so typing keeps the file type.
            list.SelectEditText(Path.GetFileNameWithoutExtension(DisplayName(entry).AsSpan()).Length);
        }
    }

    // Shortcut extensions are hidden, matching Explorer's NeverShowExt behaviour.
    static string[] hiddenExtensions = [".lnk", ".url"];

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

    internal void OnAfterLabelEdit(object? sender, LabelEditEventArgs args)
    {
        editing = false;
        if (foundPending)
        {
            // Once the list is done with the edit, and with the name changed below, if it is.
            // Not the method itself: with a parameter left out it is no Action, and would be
            // called as a delegate given no arguments, which throws.
            BeginInvoke(() => ShowFound());
        }

        var index = args.Item;

        // The rows were shown again under the edit, and its row is now another item's, or
        // gone: the item is found where it is now.
        var begunOn = edited;
        edited = null;
        if (begunOn != null &&
            (index >= displayed.Count ||
             !Locations.Same(displayed[index].FullPath, begunOn.FullPath)))
        {
            index = RowOf(begunOn.FullPath);
        }

        // The folder was read again under the edit, and the item is no longer there.
        if (index < 0 ||
            index >= displayed.Count)
        {
            args.CancelEdit = true;
            return;
        }

        var entry = displayed[index];
        var newName = args.Label?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == DisplayName(entry))
        {
            args.CancelEdit = true;
            return;
        }

        var hiddenExtension = HiddenExtension(entry);
        if (hiddenExtension != null)
        {
            newName += entry.Name[^hiddenExtension.Length..];
        }

        if (newName == entry.Name)
        {
            args.CancelEdit = true;
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            args.CancelEdit = true;
            MessageBox.Show(
                this,
                "A file name can't contain any of these characters:  \\ / : * ? \" < > |",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // With others selected too, they are all renamed to it, numbered, as Explorer does:
        // the one whose name was typed first, then the rest as the list has them.
        var selection = SelectedEntries();
        if (selection.Count > 1 &&
            selection.Contains(entry))
        {
            args.CancelEdit = true;
            RenameSeveral(entry, selection, newName);
            return;
        }

        if (!FileOperations.Rename(Owner, entry.FullPath, newName))
        {
            args.CancelEdit = true;
            return;
        }

        // The list asks for the row again now the edit is over, so what is displayed takes the
        // new name, and the row made for the old one is forgotten, until the folder is read again.
        var renamed = entry with
        {
            Name = newName,
            FullPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName)
        };

        // Found again: the shell may have asked something first, a name being taken say, and
        // the folder been read again while it waited for the answer.
        index = RowOf(entry.FullPath);
        if (index >= 0)
        {
            displayed[index] = renamed;
            rows.Remove(index);
        }

        entryEdits++;
        var position = entries.IndexOf(entry);
        if (position >= 0)
        {
            entries[position] = renamed;
        }

        var foundPosition = found.IndexOf(entry);
        if (foundPosition >= 0)
        {
            // By its new name it may belong elsewhere. A new list, not the one a merge may be
            // reading.
            found = [.. found];
            if (foundSort == (sortColumn, sortAscending))
            {
                // Put where it belongs now, rather than everything found sorted again for the
                // one of them.
                found.RemoveAt(foundPosition);
                var at = found.BinarySearch(renamed, Comparer<FileEntry>.Create(Order(sortColumn, sortAscending, gitCells)));
                if (at < 0)
                {
                    at = ~at;
                }

                found.Insert(at, renamed);
            }
            else
            {
                // Sorted when next shown, as it was to be anyway.
                found[foundPosition] = renamed;
            }
        }

        changes.Raise();
    }

    // Read again once they are renamed, and selected under their new names: there are as many
    // rows to change as were selected, and where each belongs now is for the sort to say.
    void RenameSeveral(FileEntry typedOn, List<FileEntry> selection, string newName)
    {
        List<string> paths =
        [
            typedOn.FullPath,
            .. selection
                .Where(_ => _ != typedOn &&
                            _ is {IsPlace: false, InArchive: false})
                .Select(_ => _.FullPath)
        ];
        var renamed = FileOperations.RenameSeveral(Owner, paths, newName);
        if (renamed.Count == 0)
        {
            return;
        }

        pendingSelection = renamed;
        Reload();
    }

    // The row showing the item at a path, or -1 when none does.
    int RowOf(string path) =>
        displayed.FindIndex(_ => Locations.Same(_.FullPath, path));

    void OnContextMenuRequested(Point location, bool fromKeyboard)
    {
        var selection = SelectedEntries();
        if (fromKeyboard)
        {
            var anchor = list.FocusedItem is {Selected: true} focused
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
            settings.ShowsInMenu(LodeActions.More) &&
            !InArchive &&
            !selection.Any(_ => _.Promoted));

    /// <summary>
    /// The fixed right-click menu for selected items. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> ItemCommands(List<FileEntry> selection)
    {
        if (selection.Any(_ => _.InArchive))
        {
            return ArchiveItemCommands(selection);
        }

        // Worked out in one pass: the actions bar asks for these each time the selection changes.
        var anyPlace = false;
        var anyPromoted = false;
        var allFolders = true;
        var allFiles = true;
        foreach (var entry in selection)
        {
            anyPlace |= entry.IsPlace;
            anyPromoted |= entry.Promoted;
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
            commands.AddRange(CodingAgents.Commands(folders));
            commands.Add(LodeActions.Pin(folders, places, SetPinned));
        }

        if (allFiles)
        {
            var paths = selection.Select(_ => _.FullPath).ToList();
            if (PowerShellScripts.AreScripts(paths))
            {
                commands.Add(LodeActions.Command("run", () => PowerShellScripts.Run(paths)));
            }

            if (paths.All(TextEditors.IsText))
            {
                commands.Add(LodeActions.Command("edit", () => TextEditors.Open(this, settings, paths)));
            }

            if (OpenWith.Extension(paths) is { } extension)
            {
                commands.Add(LodeActions.Submenu("openWith", QuickMenu.Deferred(() => OpenWithCommands(extension, paths))));
            }

            if (paths.All(Zip.IsZip))
            {
                commands.Add(LodeActions.Command("extractAll", () => Extract(paths, false)));
                commands.Add(LodeActions.Command("extractHere", () => Extract(paths, true)));
            }
        }

        // A file promoted to a repository's root is a shortcut to it there, so is not cut,
        // renamed or deleted from there. Nor is a shortcut made to it, or a zip of it, which
        // would go beside the file, out of sight of the root.
        var changeable = !anyPlace && !anyPromoted;
        commands.AddRange(
        [
            null,
            LodeActions.Command("cut", () => Copy(true), changeable),
            LodeActions.Command("copy", () => Copy(false), !anyPlace),
            LodeActions.Command("rename", BeginRename, selection.Count > 0 && changeable),
            LodeActions.Command("delete", () => Delete(false), changeable),
            null,
            LodeActions.Command("createShortcut", () => CreateShortcuts(selection), changeable),
            LodeActions.Command("compress", () => Compress(selection), changeable),
            LodeActions.Command("copyAsPath", () => CopyAsPath(selection.Select(_ => _.FullPath))),
            LodeActions.Command("properties", ShowProperties)
        ]);
        commands.AddRange(CodeMenu.Commands(this, settings, folders, KnownGitState));
        return commands;
    }

    // In a zip, what leaves it as it is: opening, copying, which extracts, and copying as path.
    List<QuickMenu.Command?> ArchiveItemCommands(List<FileEntry> selection)
    {
        var commands = new List<QuickMenu.Command?>
        {
            LodeActions.Command("open", () => OpenEntries(selection))
        };
        if (selection.All(_ => _.IsDirectory))
        {
            commands.Add(LodeActions.Command("openInNewTab", () => OpenInNewTabs(selection)));
        }

        commands.AddRange(
        [
            null,
            LodeActions.Command("copy", () => Copy(false)),
            LodeActions.Command("copyAsPath", () => CopyAsPath(selection.Select(_ => _.FullPath)))
        ]);
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
            settings.ShowsInMenu(LodeActions.More) && !InArchive);

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

        if (!IsReadOnly)
        {
            ShowBackgroundMenu(menu, location);
        }
    }

    /// <summary>
    /// Whether there is anything for <see cref="ShowShellMenu"/> to show a menu for. Nothing in a
    /// zip: Windows' menu for it would change it.
    /// </summary>
    public bool CanShowShellMenu =>
        !InArchive &&
        (list.SelectedIndices.Count > 0 || !ListsPlaces);

    /// <summary>
    /// The fixed right-click menu for the empty part of the list. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> BackgroundCommands(bool canPaste, IReadOnlyList<ShellNew.Entry>? newTypes = null)
    {
        // In a zip nothing can be made or pasted, but all of it can be extracted.
        if (Archive is { } inArchive)
        {
            return
            [
                .. UndoCommands(),
                null,
                LodeActions.Command("extractAll", () => Extract([inArchive.Archive], false)),
                LodeActions.Command("copyAsPath", () => CopyAsPath([CurrentPath]))
            ];
        }

        return FolderCommands(canPaste, newTypes);
    }

    List<QuickMenu.Command?> FolderCommands(bool canPaste, IReadOnlyList<ShellNew.Entry>? newTypes) =>
    [
        LodeActions.Submenu("new", NewCommands(newTypes ?? ShellNew.Current, CreateFolder, CreateFile)),
        LodeActions.Command("paste", Paste, canPaste),
        .. UndoCommands(),
        null,
        LodeActions.Command("terminal", () => FileOperations.OpenTerminal(CurrentPath)),
        LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(CurrentPath, admin: true)),
        .. ClaudeDesktop.Commands(this, [CurrentPath]),
        .. CodingAgents.Commands([CurrentPath]),
        LodeActions.Command("copyAsPath", () => CopyAsPath([CurrentPath])),
        LodeActions.Command("properties", ShowProperties),
        .. CodeMenu.Commands(this, settings, [CurrentPath], KnownGitState)
    ];

    // A repository's state, as the Git column and the status bar show it, once it has been read.
    GitState? KnownGitState(string repo) =>
        gitStates.GetValueOrDefault(repo);

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

    /// <summary>
    /// New's entries: Folder, which runs <paramref name="createFolder"/>, then each type Windows
    /// offers under New, by label, which runs <paramref name="createFile"/> with the type. The
    /// list's own New, and the tree's for a folder in it.
    /// </summary>
    internal static List<QuickMenu.Command?> NewCommands(IReadOnlyList<ShellNew.Entry> types, Action createFolder, Action<ShellNew.Entry> createFile) =>
    [
        new("Folder", createFolder)
        {
            Glyph = Icons.Folder
        },
        null,
        .. NewItems(types)
            .Select(_ =>
                new QuickMenu.Command(_.Label, () => createFile(_.Type))
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
        SetClipboardText(PathsText(paths));

    /// <summary>
    /// Puts text on the clipboard. Tests swap it, leaving the clipboard alone.
    /// </summary>
    internal static Action<string> SetClipboardText = Clipboard.SetText;

    // As Explorer's Copy as path puts them: quoted, one per line.
    internal static string PathsText(IEnumerable<string> paths) =>
        string.Join(Environment.NewLine, paths.Select(_ => $"\"{_}\""));

    // Windows' own menu. Not for what is in a zip, which it would change: Shift+right-click
    // there opens nothing. Nor for a file promoted to a repository's root, which it would cut,
    // rename or delete as it would the file itself, and which isn't in the folder open.
    void ShowItemMenu(ShellContextMenu menu, List<FileEntry> selection, Point location)
    {
        if (selection.Any(_ => _.InArchive || _.Promoted))
        {
            return;
        }

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

    // The shell would run these in Explorer; keep them inside Lode instead. Delete and Create
    // shortcut are Lode's, so Ctrl+Z hears of them, as it does from Lode's own menu. Not for a
    // place, a drive say, which Lode's own menu doesn't delete or make a shortcut beside.
    internal bool HandleItemVerb(string verb, List<FileEntry> selection)
    {
        if (!selection.Any(_ => _.IsPlace))
        {
            if (verb.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                Delete(ModifierKeys.HasFlag(Keys.Shift));
                return true;
            }

            if (verb.Equals("link", StringComparison.OrdinalIgnoreCase))
            {
                CreateShortcuts(selection);
                return true;
            }
        }

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
        if (InArchive)
        {
            return;
        }

        ShellContextMenu.CustomCommand[] customCommands =
        [
            new("Refresh", Reload),
            new("New folder", CreateFolder)
        ];

        var shown = menu.ShowForFolder(Owner, CurrentPath, location, customCommands, _ => HandleBackgroundVerb(_));
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

    // New's entries come with verbs: NewFolder, and a file type's extension. Lode makes those,
    // as its own New does, so Ctrl+Z hears of them and the name is ready to edit. A type Lode
    // doesn't know, one made by a program of its own say, and Shortcut, which is a wizard, are
    // the shell's.
    internal bool HandleBackgroundVerb(string verb, IReadOnlyList<ShellNew.Entry>? newTypes = null)
    {
        if (verb.Equals("paste", StringComparison.OrdinalIgnoreCase))
        {
            Paste();
            return true;
        }

        if (verb.Equals("NewFolder", StringComparison.OrdinalIgnoreCase))
        {
            CreateFolder();
            return true;
        }

        if (verb.StartsWith('.') &&
            (newTypes ?? ShellNew.Current).FirstOrDefault(_ => _.Extension.Equals(verb, StringComparison.OrdinalIgnoreCase)) is { } type)
        {
            CreateFile(type);
            return true;
        }

        return false;
    }

    void OnItemDrag(object? sender, ItemDragEventArgs args)
    {
        var selection = SelectedEntries();
        if (selection.Any(_ => _.InArchive))
        {
            DragOutOfArchive(selection);
            return;
        }

        var paths = SelectedPaths();
        if (paths.Length == 0)
        {
            return;
        }

        // A file promoted to a repository's root is dragged as a copy, or a shortcut, never moved
        // from where it is by a drag from where it is only shown.
        var effects = DragDropEffects.Copy | DragDropEffects.Link;
        if (!selection.Any(_ => _.Promoted))
        {
            effects |= DragDropEffects.Move;
        }

        var data = new DataObject(DataFormats.FileDrop, paths);
        SetDragImage(data, selection);
        DragImage.Drag(list, data, effects);
    }

    // A picture of what is dragged, of the icons the list shows them with, as Explorer's drags
    // have. One not yet read, a program's own say, is its type's, so the drag doesn't wait on it.
    void SetDragImage(DataObject data, List<FileEntry> dragged)
    {
        var icons = dragged
            .Take(3)
            .Select(_ => IconOf(_, static () =>
            {
            }))
            .ToList();
        DragImage.Set(data, icons, dragged.Count, DeviceDpi);
    }

    // What is in a zip is dragged as copies extracted to the temp folder, there and then, as the
    // drag can't start until they are, and only copied: moving one would take it out of the temp
    // folder, not the zip.
    void DragOutOfArchive(List<FileEntry> selection)
    {
        IReadOnlyList<string> extracted;
        var cursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            extracted = ExtractToTemp(selection.Select(_ => _.FullPath).ToList());
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            Cursor.Current = cursor;
        }

        var data = new DataObject(DataFormats.FileDrop, extracted.ToArray());
        SetDragImage(data, selection);
        DragImage.Drag(list, data, DragDropEffects.Copy);
    }

    string? DropDestination(DragEventArgs args)
    {
        var point = list.PointToClient(new(args.X, args.Y));
        if (list.GetItemAt(point.X, point.Y)?.Tag is FileEntry {IsDirectory: true, InArchive: false} target)
        {
            return target.FullPath;
        }

        if (IsReadOnly)
        {
            return null;
        }

        return CurrentPath;
    }

    // Whether what is being dragged over the list is dragged with the right button, which is up
    // by the drop.
    bool rightDrag;

    /// <summary>
    /// What a drag over the list would do, with the folder it would go into highlighted, as
    /// Explorer highlights it, and said beside the drag's picture, in Explorer's words. Not the
    /// folder open, which is the list as a whole.
    /// </summary>
    internal void OnDragOver(object? sender, DragEventArgs args)
    {
        rightDrag = FileOperations.RightButton(args);
        args.Effect = FileOperations.GetDropEffect(args, DropDestination(args));
        var target = -1;
        string? into = null;
        if (args.Effect != DragDropEffects.None)
        {
            into = Locations.DisplayName(CurrentPath);
            var point = list.PointToClient(new(args.X, args.Y));
            if (list.GetItemAt(point.X, point.Y) is {Tag: FileEntry {IsDirectory: true, InArchive: false} folder} item)
            {
                target = item.Index;
                into = DisplayName(folder);
            }
        }

        list.DropTarget = target;
        DragImage.Describe(args, args.Effect, into);
    }

    void OnDragDrop(object? sender, DragEventArgs args)
    {
        list.DropTarget = -1;
        FileOperations.Drop(Owner, args, DropDestination(args), rightDrag);
    }

    void Watch()
    {
        Unwatch();
        changes.Reset();
        parentChanges.Reset();
        renames.Clear();
        if (ListsPlaces)
        {
            return;
        }

        if (Archive is { } inArchive)
        {
            WatchArchive(inArchive.Archive);
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

        // A drive or a share has no folder above it to watch. Nor is one on the network watched:
        // starting a watch there waits on the network, on the UI thread, and reading the folder
        // still finds that it has gone.
        var parent = Path.GetDirectoryName(CurrentPath);
        if (parent == null ||
            IsRemote(CurrentPath))
        {
            return;
        }

        try
        {
            parentWatcher = new(
                parent,
                NotifyFilters.DirectoryName,
                parentChanges.Raise,
                Path.GetFileName(CurrentPath),
                (from, to) => renames.Enqueue((from, to)));
        }
        catch (Exception)
        {
            // Not to be watched. Reading the folder still finds that it has gone.
            parentWatcher = null;
        }
    }

    // Inside a zip, at any depth, the zip itself is watched, in its folder. Written to, it is
    // read again. Renamed, the view follows it; gone, it leaves.
    void WatchArchive(string zip)
    {
        if (IsRemote(zip))
        {
            return;
        }

        try
        {
            parentWatcher = new(
                Path.GetDirectoryName(zip)!,
                NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                () =>
                {
                    changes.Raise();
                    parentChanges.Raise();
                },
                Path.GetFileName(zip),
                (from, to) => renames.Enqueue((from, to)));
        }
        catch (Exception)
        {
            // Not to be watched. F5 still reads it again.
            parentWatcher = null;
        }
    }

    void Unwatch()
    {
        watcher?.Dispose();
        watcher = null;
        parentWatcher?.Dispose();
        parentWatcher = null;
        StopWatchingPromoted();
    }

    // A promoted file's date and size, or one made or deleted, is a change the folder's own watch
    // doesn't see, as the file is a folder down. So a root with files promoted to it is watched
    // all through, once a read has found it is one, and a change to a file a pattern names there
    // has it read again. Not on the network, where starting a watch waits on it.
    void WatchPromoted(string root, PromotedFiles? patterns)
    {
        if (patterns == null ||
            IsRemote(root))
        {
            StopWatchingPromoted();
            return;
        }

        if (promotedWatcher == null ||
            !Locations.Same(promotedRoot, root))
        {
            StopWatchingPromoted();
            StartWatchingPromoted(root);
        }

        promotedPatterns = patterns;
    }

    void StartWatchingPromoted(string root)
    {
        try
        {
            promotedWatcher = new(
                root,
                NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                changes.Raise,
                subfolders: true,
                changedAt: (path, _) =>
                {
                    if (promotedPatterns is { } current &&
                        current.Promotes(root, path))
                    {
                        changes.Raise();
                    }
                });
            promotedRoot = root;
        }
        catch (Exception)
        {
            // Not to be watched. F5 still reads the promoted files again.
            promotedWatcher = null;
        }
    }

    void StopWatchingPromoted()
    {
        promotedWatcher?.Dispose();
        promotedWatcher = null;
        promotedRoot = null;
        promotedPatterns = null;
    }

    // The folder's name came or went in its parent. Renamed, by anything, the view follows it to
    // its new name, as Explorer does. Gone, the view leaves it.
    bool OnParentChanged()
    {
        var renamed = new List<(string From, string To)>();
        while (renames.TryDequeue(out var rename))
        {
            renamed.Add(rename);
        }

        Follow(renamed);
        _ = LeaveIfGone();
        return true;
    }

    // A move or rename Lode did, heard on its thread, before what it let go of is watched again.
    // Posted, so it is followed ahead of anything those watches then report.
    void OnMoved(IReadOnlyList<(string From, string To)> moved)
    {
        try
        {
            context.Post(_ => Follow(moved), null);
        }
        catch (InvalidOperationException)
        {
            // Lode is exiting, and there is no longer a UI thread to tell.
        }
    }

    /// <summary>
    /// Follows folders moved or renamed, from where to where, as Explorer does: showing one, or
    /// a folder inside it, the view goes to where that is now, with what was selected still
    /// selected. Somewhere in the history inside one is where it went too, so Back goes there.
    /// </summary>
    internal void Follow(IReadOnlyList<(string From, string To)> moved)
    {
        if (IsDisposed ||
            moved.Count == 0)
        {
            return;
        }

        for (var index = 0; index < history.Count; index++)
        {
            history[index] = MovedTo(history[index], moved);
        }

        // A change of case alone is followed too, for the tab's name.
        var target = MovedTo(CurrentPath, moved);
        if (ListsPlaces ||
            string.Equals(target, CurrentPath, StringComparison.Ordinal))
        {
            return;
        }

        var kept = KeepMoved(moved);
        if (historyIndex < 0)
        {
            Open(target, null, kept);
            return;
        }

        history[historyIndex] = target;
        Open(target, historyIndex, kept);
    }

    // Where a path is now, after each move in turn.
    static string MovedTo(string path, IReadOnlyList<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            path = Locations.Moved(path, from, to) ?? path;
        }

        return path;
    }

    // What is selected, and has the focus, by where each now is, to select again once the folder
    // has been read where it went.
    Kept KeepMoved(IReadOnlyList<(string From, string To)> moved)
    {
        HashSet<string> selected;
        string? focused;
        int topIndex;

        // Let go of while nobody was looking: what it held was remembered then.
        if (entries.Count == 0 &&
            remembered is { } kept)
        {
            selected = kept.Selected;
            focused = kept.Focused;
            topIndex = kept.TopIndex;
        }
        else
        {
            selected = SelectedEntries()
                .Select(_ => _.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            focused = (list.FocusedItem?.Tag as FileEntry)?.FullPath;
            topIndex = list.TopItem?.Index ?? -1;
        }

        var movedFocus = focused == null ? null : MovedTo(focused, moved);
        return new(
            selected
                .Select(_ => MovedTo(_, moved))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            movedFocus,
            topIndex);
    }

    /// <summary>
    /// Goes up to the nearest folder still there when the one the view shows has gone: deleted,
    /// sent to the Recycle Bin, renamed or moved, or on a drive that has been taken out. Explorer
    /// does the same. Asked off the UI thread. Nothing changes while the folder is there, or
    /// may be: one that can't be asked after, a share that doesn't answer say, hasn't gone.
    /// </summary>
    internal async Task LeaveIfGone()
    {
        if (ListsPlaces)
        {
            return;
        }

        var path = CurrentPath;
        var nearest = await Task.Run(() => NearestFolder(path));

        // Not gone, or gone somewhere else meanwhile.
        if (nearest == null ||
            IsDisposed ||
            !Locations.Same(CurrentPath, path))
        {
            return;
        }

        Open(nearest, null);
    }

    // For a folder that has gone, the nearest above it still there: a machine on the network,
    // or This PC, if nothing nearer. Null while the folder itself is there, or may be.
    /// <summary>
    /// The nearest folder above <paramref name="path"/> that is there, This PC at the last, or
    /// null when <paramref name="path"/> itself is not known to have gone.
    /// </summary>
    internal static string? NearestFolder(string path)
    {
        if (!IsGone(path))
        {
            return null;
        }

        var parent = Locations.Parent(path) ?? Locations.ThisPC;
        while (!Locations.IsThisPC(parent) &&
               !Locations.IsServer(parent) &&
               IsGone(parent))
        {
            parent = Locations.Parent(parent) ?? Locations.ThisPC;
        }

        return parent;
    }

    // Known not to be a folder there: nothing there, or a file in its place. Where it can't be
    // asked after, as when Lode is denied, or the network doesn't answer, it isn't known to
    // have gone.
    static bool IsGone(string path)
    {
        // In a zip that is there, a folder it no longer holds.
        if (Zip.Split(path) is { } inArchive)
        {
            return Zip.Find(inArchive) != Zip.Kind.Folder;
        }

        try
        {
            return !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (Exception exception) when (MeansGone(exception))
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool MeansGone(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException;

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
            ShellFileOperation.Moved -= OnMoved;
            CutItems.Changed -= OnCutChanged;
            GitCache.Stale -= OnGitStale;
            search?.Cancel();
            gitCancel?.Cancel();
            changes.Dispose();
            parentChanges.Dispose();
            watcher?.Dispose();
            parentWatcher?.Dispose();
            promotedWatcher?.Dispose();
            marquee.Dispose();
            italic.Dispose();
        }

        base.Dispose(disposing);
    }
}
