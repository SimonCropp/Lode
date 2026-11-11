/// <summary>
/// The navigation pane: a Pinned section, a Recent section, then This PC and its drives, with
/// subfolders loaded on demand.
/// </summary>
sealed class FolderTree : TreeView
{
    /// <summary>
    /// The tag of a section heading. Every other node's tag is the folder's path.
    /// </summary>
    public sealed record Section(string Name);
    const int unscaledRowHeight = 26;
    const int unscaledIconSize = 16;

    static readonly object placeholder = new();

    readonly Settings settings;
    readonly Places places;

    readonly TreeNode computerNode = new(Locations.ThisPC)
    {
        Tag = Locations.ThisPC,
        ImageIndex = ShellIcons.ComputerIcon,
        SelectedImageIndex = ShellIcons.ComputerIcon
    };

    // The folders whose subfolders are kept up to date: those loaded and still expanded. Each has
    // a watcher, or null where the location can't be watched. A folder drops out when it is
    // collapsed, and is brought up to date when it is expanded again.
    readonly Dictionary<TreeNode, FileSystemWatcher?> watched = [];

    // Folders a watcher has seen change. Watcher events arrive on thread-pool threads and can
    // come in bursts, so they only land here; the timer syncs each folder once per tick on the
    // UI thread.
    readonly ConcurrentDictionary<TreeNode, bool> changed = [];
    readonly Timer changeTimer = new() { Interval = 300 };

    public event Action<string>? PathSelected;
    public event Action<string>? NewTabRequested;

    public FolderTree(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        Pin = _ => ShellContextMenu.SetPinned(FindForm()?.Handle ?? Handle, _, true);
        HideSelection = false;
        ShowLines = false;
        FullRowSelect = true;
        HotTracking = true;
        AllowDrop = true;
        BorderStyle = BorderStyle.None;

        // Explorer's spacing between rows, scaled the same way as the icons.
        ItemHeight = unscaledRowHeight * ShellIcons.SmallIconSize.Height / unscaledIconSize;
        changeTimer.Tick += (_, _) => SyncChanged();
        changeTimer.Start();
        Rebuild();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        SetWindowTheme(Handle, "Explorer", null);
        SendMessage(Handle, TreeViewSetImageList, IntPtr.Zero, ShellIcons.SmallImageList);
    }

    public void Rebuild()
    {
        BeginUpdate();
        try
        {
            UnwatchAll();
            Nodes.Clear();
            computerNode.Nodes.Clear();
            AddSection("Pinned", ShellIcons.QuickAccessIcon, places.Pinned);
            if (settings.ShowRecent)
            {
                AddSection("Recent", ShellIcons.RecentIcon, places.Recent);
            }

            if (places.ShowComputer)
            {
                Nodes.Add(computerNode);
                LoadDrives();
                computerNode.Expand();
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    // A heading with the folders beneath it, open. Nothing at all when there are no folders.
    void AddSection(string name, int icon, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        var section = new TreeNode(name)
        {
            Tag = new Section(name),
            ImageIndex = icon,
            SelectedImageIndex = icon
        };
        foreach (var folder in folders)
        {
            // Asking the shell for a network folder's own icon can stall on a share that
            // is offline, so those get the plain folder icon.
            var folderIcon = Locations.IsNetwork(folder) ? ShellIcons.FolderIcon : ShellIcons.GetActualIcon(folder);
            // Shortcuts, not a second copy of the folder tree: they have no children, and
            // picking one selects the folder in the tree under This PC.
            section.Nodes.Add(CreateNode(folder, Locations.DisplayName(folder), folderIcon, expandable: false));
        }

        Nodes.Add(section);
        if (!settings.CollapsedSections.Contains(name))
        {
            section.Expand();
        }
    }

    protected override void OnAfterExpand(TreeViewEventArgs eventArgs)
    {
        base.OnAfterExpand(eventArgs);
        if (eventArgs.Node?.Tag is Section section)
        {
            settings.CollapsedSections.Remove(section.Name);
        }
    }

    /// <summary>
    /// Picks up drives that were plugged in or removed since the tree was built.
    /// </summary>
    public void RefreshDrives()
    {
        if (!places.ShowComputer)
        {
            return;
        }

        var current = computerNode.Nodes.Cast<TreeNode>().Select(_ => (string)_.Tag!);
        var actual = places.DrivePaths();
        if (!current.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            LoadDrives();
        }
    }

    void LoadDrives()
    {
        foreach (TreeNode drive in computerNode.Nodes)
        {
            UnwatchTree(drive);
        }

        computerNode.Nodes.Clear();
        foreach (var drive in places.DrivePaths())
        {
            var (displayName, _, icon) = ShellIcons.Describe(drive);
            computerNode.Nodes.Add(CreateNode(drive, displayName, icon));
        }
    }

    static TreeNode CreateNode(string path, string text, int icon, bool expandable = true)
    {
        var node = new TreeNode(text)
        {
            Tag = path,
            ImageIndex = icon,
            SelectedImageIndex = icon
        };

        // Every folder starts with a placeholder child so it shows an expander without the
        // cost of checking for subfolders. The real children load on first expand.
        if (expandable)
        {
            node.Nodes.Add(new TreeNode { Tag = placeholder });
        }

        return node;
    }

    // A Pinned or Recent entry.
    static bool IsShortcut(TreeNode node) =>
        node.Parent?.Tag is Section;

    static bool NeedsLoading(TreeNode node) =>
        node.Nodes.Count == 1 &&
        node.Nodes[0].Tag == placeholder;

    // A folder's subfolders in display order, or null when the folder can't be read.
    List<DirectoryInfo>? ReadSubfolders(TreeNode node)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = settings.ShowHidden ? default : FileAttributes.Hidden
            };
            var directories = new DirectoryInfo((string)node.Tag!)
                .EnumerateDirectories("*", options)
                .ToList();
            directories.Sort((first, second) => StrCmpLogicalW(first.Name, second.Name));
            return directories;
        }
        catch (Exception)
        {
            return null;
        }
    }

    void LoadChildren(TreeNode node)
    {
        node.Nodes.Clear();

        // An unreadable folder simply has no children to show.
        foreach (var directory in ReadSubfolders(node) ?? [])
        {
            node.Nodes.Add(CreateNode(directory.FullName, directory.Name, ShellIcons.FolderIcon));
        }

        Watch(node);
    }

    /// <summary>
    /// Brings a folder's loaded subfolders in line with the disk. The nodes of subfolders still
    /// there are kept, so whatever is expanded beneath them stays expanded.
    /// </summary>
    void SyncChildren(TreeNode node)
    {
        // Can't tell what is there; better to leave what is showing than to empty it. If the
        // folder itself has gone, its parent's sync removes it.
        var directories = ReadSubfolders(node);
        if (directories == null)
        {
            return;
        }

        var paths = directories
            .Select(_ => _.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BeginUpdate();
        try
        {
            foreach (var child in node.Nodes.Cast<TreeNode>().ToList())
            {
                if (child.Tag is string path && paths.Contains(path))
                {
                    continue;
                }

                // Settle the selection on the folder rather than leave the control to pick some
                // other node. Set from code, so it doesn't navigate.
                if (SelectedNode != null && IsSelfOrDescendant(SelectedNode, child))
                {
                    SelectedNode = node;
                }

                UnwatchTree(child);
                child.Remove();
            }

            var nodes = node.Nodes;
            // What is left is in display order already, so new folders slot in around it.
            for (var i = 0; i < directories.Count; i++)
            {
                var directory = directories[i];
                if (i < nodes.Count &&
                    nodes[i].Tag is string path &&
                    Locations.Same(path, directory.FullName))
                {
                    // Renamed to a different case.
                    if (nodes[i].Text != directory.Name)
                    {
                        nodes[i].Text = directory.Name;
                        nodes[i].Tag = directory.FullName;
                    }

                    continue;
                }

                nodes.Insert(i, CreateNode(directory.FullName, directory.Name, ShellIcons.FolderIcon));
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    static bool IsSelfOrDescendant(TreeNode node, TreeNode ancestor)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    // This PC and the section headings aren't folders on disk: their children are managed
    // elsewhere. Shortcuts have no children at all.
    static bool IsFolder(TreeNode node) =>
        node.Tag is string path &&
        !Locations.IsThisPC(path) &&
        !IsShortcut(node);

    void Watch(TreeNode node)
    {
        if (watched.ContainsKey(node))
        {
            return;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new((string)node.Tag!)
            {
                NotifyFilter = NotifyFilters.DirectoryName
            };
            watcher.Created += (_, _) => changed[node] = true;
            watcher.Deleted += (_, _) => changed[node] = true;
            watcher.Renamed += (_, _) => changed[node] = true;

            // Too many changes at once to keep track of, or the folder itself has gone.
            // Either way, read it again.
            watcher.Error += (_, _) => changed[node] = true;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Some locations (an offline share, an empty drive) can't be watched. Those are
            // read again whenever they are expanded.
            watcher?.Dispose();
            watcher = null;
        }

        watched[node] = watcher;
    }

    void Unwatch(TreeNode node)
    {
        if (watched.Remove(node, out var watcher))
        {
            watcher?.Dispose();
        }
    }

    // For a node leaving the tree: it, and everything beneath it.
    void UnwatchTree(TreeNode node)
    {
        Unwatch(node);
        foreach (TreeNode child in node.Nodes)
        {
            UnwatchTree(child);
        }
    }

    void UnwatchAll()
    {
        foreach (var watcher in watched.Values)
        {
            watcher?.Dispose();
        }

        watched.Clear();
        changed.Clear();
    }

    void SyncChanged()
    {
        if (changed.IsEmpty)
        {
            return;
        }

        foreach (var node in changed.Keys)
        {
            changed.TryRemove(node, out _);

            // Skips folders collapsed or removed since, including by an earlier sync this tick.
            if (watched.ContainsKey(node))
            {
                SyncChildren(node);
            }
        }
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs eventArgs)
    {
        if (eventArgs.Node is { } node && IsFolder(node))
        {
            if (NeedsLoading(node))
            {
                LoadChildren(node);
            }
            else if (!watched.ContainsKey(node))
            {
                // Loaded before, but not watched while collapsed.
                SyncChildren(node);
                Watch(node);
            }
        }

        base.OnBeforeExpand(eventArgs);
    }

    protected override void OnAfterCollapse(TreeViewEventArgs eventArgs)
    {
        base.OnAfterCollapse(eventArgs);
        if (eventArgs.Node?.Tag is Section section &&
            !settings.CollapsedSections.Contains(section.Name))
        {
            settings.CollapsedSections.Add(section.Name);
        }

        if (eventArgs.Node is { } node && IsFolder(node))
        {
            Unwatch(node);
        }
    }

    protected override void OnAfterSelect(TreeViewEventArgs eventArgs)
    {
        base.OnAfterSelect(eventArgs);

        // Unknown means the selection was set from code (SelectPath), not by the user.
        if (eventArgs.Action != TreeViewAction.Unknown && eventArgs.Node is { Tag: string path } node)
        {
            PathSelected?.Invoke(path);

            // Moved after the click has settled, as changing the selection inside its own
            // event is ignored.
            if (IsShortcut(node))
            {
                BeginInvoke(() => SelectPath(path));
            }
        }
    }

    /// <summary>
    /// Selects a node as a click would, for tests.
    /// </summary>
    internal void SelectByUser(TreeNode node)
    {
        SelectedNode = node;
        OnAfterSelect(new(node, TreeViewAction.ByMouse));
    }

    /// <summary>
    /// Moves the selection to the node for <paramref name="path"/>, expanding down to it.
    /// </summary>
    public void SelectPath(string path)
    {
        // A selected shortcut still moves to the folder in the tree below, when there is one.
        if (SelectedNode is { Tag: string selected } current &&
            Locations.Same(selected, path) &&
            !IsShortcut(current))
        {
            return;
        }

        var node = FindNode(path);
        SelectedNode = node;
        node?.EnsureVisible();
    }

    TreeNode? FindNode(string path)
    {
        if (Locations.IsThisPC(path))
        {
            return places.ShowComputer ? computerNode : null;
        }

        // Walk down from the drive. Only when no drive holds the path (a network share, say)
        // does a Pinned or Recent entry for the folder itself stand in; those have no
        // children, so it has to be the folder exactly.
        var current = computerNode.Nodes
            .Cast<TreeNode>()
            .Where(_ => Locations.IsWithin(path, (string)_.Tag!))
            .MaxBy(_ => ((string)_.Tag!).Length);
        if (current == null)
        {
            return Nodes
                .Cast<TreeNode>()
                .Where(_ => _.Tag is Section)
                .SelectMany(_ => _.Nodes.Cast<TreeNode>())
                .FirstOrDefault(_ => Locations.Same((string)_.Tag!, path));
        }

        var currentPath = (string)current.Tag!;
        var remainder = path.AsSpan(currentPath.Length);
        foreach (var range in remainder.Split(Path.DirectorySeparatorChar))
        {
            var segment = remainder[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (NeedsLoading(current))
            {
                LoadChildren(current);
            }

            currentPath = Path.Join(currentPath, segment);
            var child = current.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(_ => Locations.Same(_.Tag as string, currentPath));
            if (child == null)
            {
                // Hidden or since-deleted folder: settle for the deepest ancestor we have.
                break;
            }

            current.Expand();
            current = child;
        }

        return current;
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs eventArgs)
    {
        base.OnNodeMouseClick(eventArgs);
        if (eventArgs.Button != MouseButtons.Right ||
            eventArgs.Node?.Tag is not string path ||
            Locations.IsThisPC(path))
        {
            return;
        }

        var owner = FindForm()?.Handle ?? Handle;
        var location = PointToScreen(eventArgs.Location);
        var quickAccess = eventArgs.Node.Parent?.Tag is Section;
        QuickMenu.Show(
            location,
            NodeCommands(path),
            () => ShowShellMenu(owner, path, location, quickAccess));
    }

    /// <summary>
    /// The fixed right-click menu for a folder in the tree. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> NodeCommands(string path) =>
    [
        new("Open", () => PathSelected?.Invoke(path)),
        new("Open in new tab", () => NewTabRequested?.Invoke(path)),
        null,
        new("Copy", () => FileOperations.CopyToClipboard([path], false)),
        new("Copy as path", () => FolderView.CopyAsPath([path])),
        new("Properties", () => ShellContextMenu.InvokeVerb(FindForm()?.Handle ?? Handle, [path], "properties")),
        .. CodeMenu.Commands(this, settings, path)
    ];

    /// <summary>
    /// Pins a folder to Quick access. Replaced in tests, which must not change the real list.
    /// </summary>
    internal Action<string> Pin;

    void ShowShellMenu(IntPtr owner, string path, Point location, bool quickAccess)
    {
        ShellContextMenu.CustomCommand[] customCommands =
        [
            new("Open in new tab", () => NewTabRequested?.Invoke(path))
        ];

        using var menu = new ShellContextMenu();

        // Pinned and Recent folders are Quick access entries, whose menu offers Unpin.
        if (quickAccess)
        {
            menu.ShowForQuickAccessItem(owner, path, location, customCommands, HandleVerb);
            return;
        }

        menu.ShowForItems(owner, [path], location, customCommands, HandleVerb);

        bool HandleVerb(string verb)
        {
            if (!verb.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            PathSelected?.Invoke(path);
            return true;
        }
    }

    string? DropDestination(DragEventArgs eventArgs)
    {
        var point = PointToClient(new(eventArgs.X, eventArgs.Y));
        if (GetNodeAt(point)?.Tag is not string path || Locations.IsThisPC(path))
        {
            return null;
        }

        return path;
    }

    bool IsPinnedHeader(Point clientLocation) =>
        GetNodeAt(clientLocation)?.Tag is Section { Name: "Pinned" };

    /// <summary>
    /// Pins the folder when the screen point is over the Pinned header. True when it was over
    /// the header, so the caller treats the drop as handled.
    /// </summary>
    public bool PinIfOverPinned(Point screenLocation, string path)
    {
        if (!IsPinnedHeader(PointToClient(screenLocation)))
        {
            return false;
        }

        foreach (var folder in Pinnable([path]))
        {
            Pin(folder);
        }

        return true;
    }

    /// <summary>
    /// The paths that can be pinned: existing folders not pinned already.
    /// </summary>
    internal string[] Pinnable(IEnumerable<string> paths) =>
        paths
            .Where(_ => !Locations.IsThisPC(_) &&
                        Directory.Exists(_) &&
                        !places.Pinned.Any(pinned => Locations.Same(pinned, _)))
            .ToArray();

    // Folders dropped on the Pinned header get pinned to Quick access, as in Explorer.
    string[]? FoldersToPin(DragEventArgs eventArgs)
    {
        var point = PointToClient(new(eventArgs.X, eventArgs.Y));
        if (!IsPinnedHeader(point) ||
            eventArgs.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        var folders = Pinnable(paths);
        if (folders.Length == 0)
        {
            return null;
        }

        return folders;
    }

    DragDropEffects DropEffect(DragEventArgs eventArgs)
    {
        if (FoldersToPin(eventArgs) != null)
        {
            return DragDropEffects.Link;
        }

        return FileOperations.GetDropEffect(eventArgs, DropDestination(eventArgs));
    }

    protected override void OnDragEnter(DragEventArgs eventArgs)
    {
        base.OnDragEnter(eventArgs);
        eventArgs.Effect = DropEffect(eventArgs);
    }

    protected override void OnDragOver(DragEventArgs eventArgs)
    {
        base.OnDragOver(eventArgs);
        eventArgs.Effect = DropEffect(eventArgs);
    }

    protected override void OnDragDrop(DragEventArgs eventArgs)
    {
        base.OnDragDrop(eventArgs);
        var owner = FindForm()?.Handle ?? Handle;
        if (FoldersToPin(eventArgs) is { } folders)
        {
            foreach (var folder in folders)
            {
                Pin(folder);
            }

            return;
        }

        FileOperations.Drop(owner, eventArgs, DropDestination(eventArgs));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            changeTimer.Dispose();
            UnwatchAll();
        }

        base.Dispose(disposing);
    }
}
