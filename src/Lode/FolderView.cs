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
    readonly Timer watchTimer = new() { Interval = 400 };
    readonly List<string> history = [];
    int historyIndex = -1;

    FileSystemWatcher? watcher;
    volatile bool changePending;
    bool loading;
    int loadVersion;

    List<FileEntry> entries = [];
    List<FileEntry> displayed = [];
    int sortColumn;
    bool sortAscending = true;
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
    /// The selection rectangle dragged from the margin left of the list. For tests.
    /// </summary>
    internal GutterMarquee Marquee => marquee;

    /// <summary>
    /// Where a row is, in the list's coordinates. For tests.
    /// </summary>
    internal Rectangle RowBounds(int index) =>
        list.Items[index].Bounds;
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
        UpdateColumns();

        list.ItemActivate += (_, _) => OpenEntries(SelectedEntries());
        list.ColumnClick += OnColumnClick;
        list.KeyDown += OnListKeyDown;
        list.SelectedIndexChanged += (_, _) => QueueStatusChanged();
        list.BeforeLabelEdit += OnBeforeLabelEdit;
        list.AfterLabelEdit += OnAfterLabelEdit;
        list.ContextMenuRequested += OnContextMenuRequested;
        list.HeaderContextMenuRequested += ShowHeaderMenu;
        list.ItemDrag += OnItemDrag;
        list.DragEnter += OnDragOver;
        list.DragOver += OnDragOver;
        list.DragDrop += OnDragDrop;
        Controls.Add(list);

        watchTimer.Tick += (_, _) => OnWatchTimerTick();
        watchTimer.Start();
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

    public string StatusText
    {
        get
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

            var selection = SelectedEntries();
            if (selection.Count > 0)
            {
                text += selection.Count == 1 ? "    1 item selected" : $"    {selection.Count:N0} items selected";
                if (selection.All(_ => !_.IsDirectory))
                {
                    text += $"  {Formatting.Size(selection.Sum(_ => _.Size))}";
                }
            }

            return text;
        }
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
        Watch();
        UpdateFreeSpace();
        PathChanged?.Invoke();
        Loading = LoadAsync(false);
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

    public void Reload()
    {
        UpdateFreeSpace();
        Loading = LoadAsync(true);
    }

    async Task LoadAsync(bool preserveState)
    {
        var version = ++loadVersion;
        var path = CurrentPath;
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        List<FileEntry> loaded;
        string? failure = null;
        loading = true;
        try
        {
            loaded = await Task.Run(() => Enumerate(path, showHidden, hiddenSystem));
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
        error = failure;
        Populate(preserveState);
    }

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

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (hiddenSystem != null &&
                hiddenSystem.IsSystem(info.FullName, info.Attributes))
            {
                continue;
            }

            var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
            var size = info is FileInfo file ? file.Length : 0;
            result.Add(new(info.Name, info.FullName, isDirectory, size, info.LastWriteTime, info.Attributes));
        }

        return result;
    }

    void Populate(bool preserveState)
    {
        // Before sorting: hiding the column the list is sorted by sorts it by name.
        var columnsChanged = UpdateColumns();
        var visible = ApplyFilter().ToList();
        visible.Sort(Compare);

        if (preserveState && pendingRename == null && !columnsChanged && visible.SequenceEqual(displayed))
        {
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
            list.Items.Clear();

            var items = new ListViewItem[visible.Count];
            for (var index = 0; index < items.Length; index++)
            {
                items[index] = CreateItem(visible[index]);
            }

            list.Items.AddRange(items);

            foreach (var item in items)
            {
                var path = ((FileEntry)item.Tag!).FullPath;
                if (selected.Contains(path))
                {
                    item.Selected = true;
                }

                if (string.Equals(path, focused, StringComparison.OrdinalIgnoreCase))
                {
                    item.Focused = true;
                }
            }

            if (topIndex > 0 && items.Length > 0)
            {
                list.TopItem = items[Math.Min(topIndex, items.Length - 1)];
            }
        }
        finally
        {
            list.EndUpdate();
        }

        displayed = visible;
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

        list.Columns.Clear();
        foreach (var column in wanted)
        {
            var definition = columnDefinitions[column];
            if (!columnWidths.TryGetValue(column, out var width))
            {
                width = LogicalToDeviceUnits(definition.Width);
            }

            list.Columns.Add(ColumnTitle(column), width, definition.Alignment);
        }

        shownColumns = wanted;
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
            if (list.Items.Count == 0)
            {
                return [];
            }

            return list.Items[0].SubItems
                .Cast<ListViewItem.ListViewSubItem>()
                .Select(_ => _.Text)
                .ToList();
        }
    }

    // A row's cells for the columns shown, from one for every column.
    string[] ShownCells(string[] cells) =>
        shownColumns.Select(_ => cells[_]).ToArray();

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
    public void RefreshColumns() =>
        Populate(true);

    /// <summary>
    /// The header's right-click menu, as Explorer's: size the column clicked, or all of them,
    /// to fit, then a tick for each column, to hide or show it. Name cannot be hidden.
    /// </summary>
    internal ContextMenuStrip HeaderMenu(int clickedColumn)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(
            new ToolStripMenuItem("Size column to fit", null, (_, _) => list.SizeColumnToFit(clickedColumn))
            {
                Enabled = clickedColumn >= 0
            });
        menu.Items.Add(
            "Size all columns to fit",
            null,
            (_, _) =>
            {
                for (var index = 0; index < list.Columns.Count; index++)
                {
                    list.SizeColumnToFit(index);
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

    ListViewItem CreateItem(FileEntry entry)
    {
        if (entry.IsDrive)
        {
            var (displayName, typeName, driveIcon) = ShellIcons.Describe(entry.FullPath);
            string[] driveCells =
            [
                displayName,
                entry.Size > 0 ? Formatting.Size(entry.FreeSpace) : "",
                typeName,
                entry.Size > 0 ? Formatting.Size(entry.Size) : ""
            ];
            return new(ShownCells(driveCells), list.PaddedIcon(driveIcon)) { Tag = entry };
        }

        string[] cells =
        [
            DisplayName(entry),
            entry.Modified.ToString("g"),
            ShellIcons.GetTypeName(entry.FullPath, entry.IsDirectory),
            entry.IsDirectory ? "" : Formatting.Kilobytes(entry.Size)
        ];
        var icon = list.PaddedIcon(ShellIcons.GetIcon(entry.FullPath, entry.IsDirectory));
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

    int Compare(FileEntry first, FileEntry second)
    {
        // Folders stay grouped above files whichever way the column is sorted.
        if (first.IsDirectory != second.IsDirectory)
        {
            return first.IsDirectory ? -1 : 1;
        }

        var result = sortColumn switch
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

        return sortAscending ? result : -result;
    }

    public List<FileEntry> SelectedEntries() =>
        list.SelectedItems
            .Cast<ListViewItem>()
            .Select(_ => (FileEntry)_.Tag!)
            .ToList();

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
        var entry = (FileEntry)list.Items[eventArgs.Item].Tag!;
        if (entry.IsDrive)
        {
            eventArgs.CancelEdit = true;
            return;
        }

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

        return hiddenExtensions.FirstOrDefault(_ => entry.Name.EndsWith(_, StringComparison.OrdinalIgnoreCase) && entry.Name.Length > _.Length);
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

    void OnAfterLabelEdit(object? sender, LabelEditEventArgs eventArgs)
    {
        editing = false;
        var item = list.Items[eventArgs.Item];
        var entry = (FileEntry)item.Tag!;
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

        item.Tag = entry with
        {
            Name = newName,
            FullPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName)
        };
        changePending = true;
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
        var anyDrive = selection.Any(_ => _.IsDrive);
        var commands = new List<QuickMenu.Command?>
        {
            LodeActions.Command("open", () => OpenEntries(selection))
        };
        if (selection.All(_ => _.IsDirectory))
        {
            commands.Add(LodeActions.Command("openInNewTab", () => OpenInNewTabs(selection)));
        }

        if (selection is [{ IsDirectory: true } terminalFolder])
        {
            commands.Add(LodeActions.Command("terminal", () => FileOperations.OpenTerminal(terminalFolder.FullPath)));
            commands.Add(LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(terminalFolder.FullPath, admin: true)));
        }

        if (selection.All(_ => _.IsDirectory))
        {
            commands.Add(LodeActions.Pin(selection.Select(_ => _.FullPath).ToList(), places, SetPinned));
        }

        if (selection.All(_ => !_.IsDirectory && TextEditors.IsText(_.FullPath)))
        {
            commands.Add(LodeActions.Command("edit", () => TextEditors.Open(this, settings, selection.Select(_ => _.FullPath).ToList())));
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
        null,
        LodeActions.Command("terminal", () => FileOperations.OpenTerminal(CurrentPath)),
        LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(CurrentPath, admin: true)),
        LodeActions.Command("copyAsPath", () => CopyAsPath([CurrentPath])),
        LodeActions.Command("properties", ShowProperties),
        .. CodeMenu.Commands(this, settings, CurrentPath)
    ];

    // Folder, then each type Windows offers under New, by label.
    List<QuickMenu.Command?> NewCommands(IReadOnlyList<ShellNew.Entry> types) =>
    [
        new("Folder", CreateFolder)
        {
            Glyph = Icons.Folder
        },
        null,
        .. types
            .Select(_ => new QuickMenu.Command(NewLabel(_.TypeName), () => CreateFile(_))
            {
                // The type's own icon, as Explorer's New menu shows.
                ShellIcon = ShellIcons.GetIcon($"x{_.Extension}", false)
            })
            .OrderBy(_ => _.Text, StringComparer.CurrentCultureIgnoreCase)
    ];

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
        watcher?.Dispose();
        watcher = null;
        changePending = false;
        if (IsThisPC)
        {
            return;
        }

        try
        {
            watcher = new(CurrentPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += (_, _) => changePending = true;
            watcher.Deleted += (_, _) => changePending = true;
            watcher.Changed += (_, _) => changePending = true;
            watcher.Renamed += (_, _) => changePending = true;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Some locations (certain network shares) can't be watched; F5 still works.
            watcher = null;
        }
    }

    // Watcher events arrive on thread-pool threads and can come in bursts. They only raise a
    // flag; this timer turns the flag into at most one reload per tick on the UI thread.
    void OnWatchTimerTick()
    {
        if (!changePending || loading || editing)
        {
            return;
        }

        changePending = false;
        Reload();
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
            watchTimer.Dispose();
            watcher?.Dispose();
            marquee.Dispose();
        }

        base.Dispose(disposing);
    }
}
