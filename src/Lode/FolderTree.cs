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
    const int unscaledRowHeight = 32;
    const int unscaledIndent = 8;
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
    readonly Dictionary<TreeNode, FolderWatcher?> watched = [];

    // Folders a watcher has seen change. Watcher events arrive on thread-pool threads and can
    // come in bursts, so they only land here; the signal syncs each folder at most once per
    // interval on the UI thread.
    readonly ConcurrentDictionary<TreeNode, bool> changed = [];
    readonly ChangeSignal changes;

    // False while the window is hidden or minimized. Folders that change then wait, and are
    // synced when it shows again.
    bool showing = true;

    /// <summary>
    /// A subfolder as the tree shows it, and whether it has subfolders the tree would show
    /// beneath it, which is what gives it an expander.
    /// </summary>
    readonly record struct Subfolder(string Name, string FullPath, bool HasSubfolders);

    // The selected folder, when it is watched only for being selected: kept up to date while it
    // is, as an expanded folder is, so that one with no subfolders gets its expander as soon as
    // it has one (New folder in the list, say), and one whose last goes loses it.
    TreeNode? watchedForSelection;

    // Folders being read because their expander was clicked, so that clicking it again
    // meanwhile doesn't read them again.
    readonly HashSet<TreeNode> reading = [];

    // A round of bringing changed folders up to date is under way. The next waits for it, so an
    // older reading of a folder can't land after a newer one.
    bool syncing;

    // The folder being selected while the way down to it is read, and which attempt that is, so
    // one overtaken by another gives up.
    string? selecting;
    int selectVersion;

    /// <summary>
    /// The reading of folders under way, all of it off the UI thread. Completes once the tree
    /// shows what was read.
    /// </summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    void Track(Task work)
    {
        if (work.IsCompleted)
        {
            return;
        }

        if (Loading.IsCompleted)
        {
            Loading = work;
            return;
        }

        Loading = Task.WhenAll(Loading, work);
    }

    public event Action<string>? PathSelected;
    public event Action<string>? NewTabRequested;

    public FolderTree(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        SetPinned = (path, pin) => ShellContextMenu.SetPinned(FindForm()?.Handle ?? Handle, path, pin);
        Pin = _ => SetPinned(_, true);
        MovePinnedToEnd = _ => ShellContextMenu.MoveToEnd(FindForm()?.Handle ?? Handle, _);
        HideSelection = false;
        ShowLines = false;
        FullRowSelect = true;
        HotTracking = true;
        AllowDrop = true;
        BorderStyle = BorderStyle.None;
        // Painted through a buffer, as Explorer's tree is. Resizing it, as dragging the splitter
        // does, has every row drawn again, and drawn straight to the screen they flicker, wiped
        // to the background and drawn back each time the mouse moves.
        DoubleBuffered = true;
        // Only for Rename on the right-click menu; a click on the selected folder does not
        // start editing, as it would by default.
        LabelEdit = true;

        // Explorer's spacing between rows, scaled the same way as the icons.
        ItemHeight = unscaledRowHeight * ShellIcons.SmallIconSize.Height / unscaledIconSize;
        changes = new(300, SyncChanged);
        Rebuild();
    }

    /// <summary>
    /// Whether anybody can see the tree. In a window that is hidden or minimized it is told it is
    /// not showing, and leaves the folders that change until it shows again.
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
            SyncChanged();
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        BackColor = ExplorerStyle.Window;
        SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
        SendMessage(Handle, TreeViewSetImageList, IntPtr.Zero, ShellIcons.SmallImageList);
        // Explorer's step in from one level to the next, scaled the same way as the icons: far
        // less than the tree's own, which is a little wider than an icon.
        Indent = unscaledIndent * ShellIcons.SmallIconSize.Width / unscaledIconSize;
    }

    /// <summary>
    /// Builds the tree again from the settings and places. True when it selected a pinned folder
    /// that was just moved, which the caller then leaves selected.
    /// </summary>
    public bool Rebuild()
    {
        bool selectedMoved;
        if (pinnedMove is { IsCompleted: true })
        {
            pinnedMove = null;
        }

        // A folder on its way to being selected was being read for the tree that was, by the
        // settings that were. Whoever rebuilds selects again.
        selectVersion++;
        selecting = null;
        using (this.SuspendPainting())
        {
            UnwatchAll();
            Nodes.Clear();
            computerNode.Nodes.Clear();
            IReadOnlyList<string> recent = [];
            if (settings.ShowRecent)
            {
                recent = places.Recent;
            }

            // Told apart across both sections, which show side by side, as tabs are.
            var labels = Locations.Labels(places.Pinned.Concat(recent));
            AddSection("Pinned", ShellIcons.QuickAccessIcon, places.Pinned, labels);
            AddSection("Recent", ShellIcons.RecentIcon, recent, labels);

            if (places.ShowComputer)
            {
                Nodes.Add(computerNode);
                LoadDrives();
                computerNode.Expand();
            }

            selectedMoved = SelectMovedPinned();
            ShowShortcutsSelected(SelectedNode);
        }

        return selectedMoved;
    }

    // A pinned folder being moved, and the order it is being moved into. Its node is selected
    // in each rebuild until Quick access has that order.
    string? movingPinned;
    IReadOnlyList<string> movingOrder = [];

    bool SelectMovedPinned()
    {
        if (movingPinned == null ||
            PinnedSection is not { } section)
        {
            return false;
        }

        var node = section.Nodes.Cast<TreeNode>().FirstOrDefault(_ => Locations.Same(_.Tag as string, movingPinned));
        if (node == null)
        {
            return false;
        }

        SelectedNode = node;
        if (places.Pinned.SequenceEqual(movingOrder, StringComparer.OrdinalIgnoreCase))
        {
            movingPinned = null;
        }

        return true;
    }

    TreeNode? PinnedSection =>
        Nodes.Cast<TreeNode>().FirstOrDefault(_ => _.Tag is Section { Name: "Pinned" });

    static bool IsPinnedEntry(TreeNode? node) =>
        node is { Tag: string, Parent.Tag: Section { Name: "Pinned" } };

    /// <summary>
    /// Moves a pinned folder to <paramref name="index"/> in the Pinned list, as dragging it there
    /// does.
    /// </summary>
    internal void MovePinned(string folder, int index)
    {
        // Each move is worked out from Quick access as last read, so none starts until the
        // one before has finished and Quick access has been read again after it.
        if (pinnedMove != null)
        {
            return;
        }

        var desired = PinnedOrder.Move(places.Pinned, folder, index);
        var toMove = PinnedOrder.ToMoveToEnd(places.Pinned, desired);
        if (toMove.Count == 0)
        {
            return;
        }

        movingPinned = folder;
        movingOrder = desired;
        ShowPinnedMoved(folder, index);
        pinnedMove = MovePinnedToEnd(toMove);
    }

    // The shell moving pinned folders, until the tree is rebuilt from Quick access read again
    // after it.
    Task? pinnedMove;

    // The shell takes a moment to move a pinned folder, and Quick access is only read again
    // after that, so the folder is shown in its new place, and selected, before it starts.
    void ShowPinnedMoved(string folder, int index)
    {
        var node = PinnedSection?.Nodes.Cast<TreeNode>().FirstOrDefault(_ => Locations.Same(_.Tag as string, folder));
        if (node?.Parent is not { } section)
        {
            return;
        }

        using (this.SuspendPainting())
        {
            node.Remove();
            section.Nodes.Insert(Math.Clamp(index, 0, section.Nodes.Count), node);
            SelectedNode = node;
        }
    }

    // Alt+Shift+Up and Down move the selected pinned folder up or down the list.
    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyData is (Keys.Alt | Keys.Shift | Keys.Up) or (Keys.Alt | Keys.Shift | Keys.Down) &&
            IsPinnedEntry(SelectedNode))
        {
            var node = SelectedNode!;
            var offset = eventArgs.KeyCode == Keys.Up ? -1 : 1;
            MovePinned((string)node.Tag!, node.Index + offset);
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(eventArgs);
    }

    // A pinned folder can be dragged up or down the Pinned list to reorder it, as in Explorer.
    const string pinnedFormat = "Lode.PinnedFolder";

    protected override void OnItemDrag(ItemDragEventArgs eventArgs)
    {
        base.OnItemDrag(eventArgs);
        if (eventArgs is {Button: MouseButtons.Left, Item: TreeNode node} &&
            IsPinnedEntry(node))
        {
            var data = new DataObject();
            data.SetData(pinnedFormat, (string)node.Tag!);
            DoDragDrop(data, DragDropEffects.Move);
            ClearInsertMark();
        }
    }

    // Where a dragged pinned folder would land: before or after a pinned folder, or first when
    // over the Pinned heading. Null when not over the Pinned list.
    (string Folder, int Index, TreeNode Node, bool After)? PinnedDropTarget(DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetData(pinnedFormat) is not string folder)
        {
            return null;
        }

        var point = PointToClient(new(eventArgs.X, eventArgs.Y));
        var node = GetNodeAt(point);
        if (node?.Tag is Section { Name: "Pinned" })
        {
            return (folder, 0, node, false);
        }

        if (!IsPinnedEntry(node))
        {
            return null;
        }

        var after = point.Y > node!.Bounds.Top + node.Bounds.Height / 2;
        var others = places.Pinned.Where(_ => !Locations.Same(_, folder)).ToList();
        var targetIndex = others.FindIndex(_ => Locations.Same(_, node.Tag as string));
        if (targetIndex < 0)
        {
            // Over the folder being dragged: it stays where it is.
            targetIndex = node.Index;
            after = false;
        }
        else if (after)
        {
            targetIndex++;
        }

        return (folder, targetIndex, node, after);
    }

    void ShowInsertMark(TreeNode node, bool after) =>
        SendMessage(Handle, TreeViewSetInsertMark, after ? 1 : 0, node.Handle);

    void ClearInsertMark() =>
        SendMessage(Handle, TreeViewSetInsertMark, IntPtr.Zero, IntPtr.Zero);

    // A heading with the folders beneath it, open. Nothing at all when there are no folders.
    // Asking the shell for a network folder's own icon can stall on a share that is offline, so
    // a machine gets Network's and a folder on one the plain folder icon.
    static int PinnedIcon(string folder)
    {
        if (Locations.IsServer(folder) ||
            Locations.IsShare(folder))
        {
            return ShellIcons.NetworkIcon;
        }

        return ShellIcons.GetPlaceIcon(folder);
    }

    void AddSection(string name, int icon, IReadOnlyList<string> folders, Dictionary<string, string> labels)
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
            var folderIcon = PinnedIcon(folder);
            // Shortcuts, not a second copy of the folder tree: they have no children, and
            // picking one selects the folder in the tree under This PC.
            section.Nodes.Add(CreateNode(folder, labels[folder], folderIcon, expandable: false));
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
            return;
        }

        // The same drives, described again: one can have been renamed, or mapped elsewhere.
        foreach (TreeNode drive in computerNode.Nodes)
        {
            DescribeDrive(drive);
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
            var node = CreateNode(drive, "", ShellIcons.FolderIcon);
            DescribeDrive(node);
            computerNode.Nodes.Add(node);
        }
    }

    // A drive mapped to a share is named by its letter until the shell has described it, off
    // the UI thread, and then again once it has, if it is still in the tree.
    void DescribeDrive(TreeNode node)
    {
        var (displayName, _, icon) = ShellIcons.DescribeDrive(
            (string)node.Tag!,
            () =>
            {
                if (!IsDisposed &&
                    IsInTree(node))
                {
                    DescribeDrive(node);
                }
            });
        node.Text = displayName;
        node.ImageIndex = icon;
        node.SelectedImageIndex = icon;
    }

    static TreeNode CreateNode(string path, string text, int icon, bool expandable = true)
    {
        var node = new TreeNode(text)
        {
            Tag = path,
            ImageIndex = icon,
            SelectedImageIndex = icon
        };

        // A folder with subfolders starts with a placeholder child, so it shows an expander
        // before they are read. The real children load on first expand.
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

    // A folder's subfolders in display order, or null when the folder can't be read. Never on
    // the UI thread: finding the folders in a folder means going through everything in it, files
    // too, however many there are and however slow the drive.
    static List<Subfolder>? ReadSubfolders(string path, bool showHidden, SystemItems? hiddenSystem)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = showHidden ? default : FileAttributes.Hidden
            };

            // As File Explorer does, every folder on a share gets an expander, rather than a trip
            // across the network into each one to see whether it should.
            var lookInside = !Locations.IsRemote(path);
            var directories = new List<Subfolder>();
            foreach (var (name, fullPath) in FindSubfolders(path, options, hiddenSystem))
            {
                var hasSubfolders = !lookInside || HasSubfolders(fullPath, options, hiddenSystem);
                directories.Add(new(name, fullPath, hasSubfolders));
            }

            directories.Sort((first, second) => StrCmpLogicalW(first.Name, second.Name));
            return directories;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The folders in a folder that the tree shows, as they are found: hidden ones only when the
    // options do not skip them, and Windows' own only when those are shown.
    static IEnumerable<(string Name, string FullPath)> FindSubfolders(string path, EnumerationOptions options, SystemItems? hiddenSystem)
    {
        var found = new FileSystemEnumerable<(string Name, string FullPath, FileAttributes Attributes)>(
            path,
            (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.ToFullPath(), entry.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory
        };
        foreach (var (name, fullPath, attributes) in found)
        {
            if (hiddenSystem == null ||
                !hiddenSystem.IsSystem(fullPath, attributes))
            {
                yield return (name, fullPath);
            }
        }
    }

    // Stops at the first, so only a folder with none, or with only folders the tree leaves out,
    // is gone all the way through. A folder Lode is denied reads as empty, and gets no expander,
    // as expanding it would show nothing. One that fails to be read some other way keeps its
    // expander, and expanding it reads it again.
    static bool HasSubfolders(string path, EnumerationOptions options, SystemItems? hiddenSystem)
    {
        try
        {
            return FindSubfolders(path, options, hiddenSystem).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Reads folders' subfolders off the UI thread, by the settings as they are now. One result
    // for each folder, in the same order.
    Task<List<List<Subfolder>?>> ReadAsync(IReadOnlyList<string> paths)
    {
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        return Task.Run(
            () => paths
                .Select(_ => ReadSubfolders(_, showHidden, hiddenSystem))
                .ToList());
    }

    // Still in the tree, rather than taken out of it while its folder was being read.
    bool IsInTree(TreeNode node)
    {
        var root = node;
        while (root.Parent != null)
        {
            root = root.Parent;
        }

        return Nodes.Contains(root);
    }

    void LoadChildren(TreeNode node, List<Subfolder>? directories)
    {
        using (this.SuspendPainting())
        {
            node.Nodes.Clear();

            // An unreadable folder simply has no children to show.
            foreach (var directory in directories ?? [])
            {
                node.Nodes.Add(CreateNode(directory.FullPath, directory.Name, ShellIcons.FolderIcon, directory.HasSubfolders));
            }
        }

        Watch(node);
    }

    // The expander of a folder not read yet was clicked. It is read, off the UI thread, and
    // only then expanded: until then there is nothing to show under it.
    async Task ExpandOnceRead(TreeNode node)
    {
        if (!reading.Add(node))
        {
            return;
        }

        try
        {
            var read = await ReadAsync([(string)node.Tag!]);
            if (IsDisposed ||
                !IsInTree(node))
            {
                return;
            }

            // Unless the way down to a folder being selected got here first.
            if (NeedsLoading(node))
            {
                LoadChildren(node, read[0]);
            }

            node.Expand();
        }
        finally
        {
            reading.Remove(node);
        }
    }

    // Reads the folders again, off the UI thread, and brings the subfolders each shows in line
    // with what is there.
    async Task SyncAsync(List<TreeNode> nodes)
    {
        var read = await ReadAsync(nodes.Select(_ => (string)_.Tag!).ToList());
        if (IsDisposed)
        {
            return;
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            // Not folders collapsed or removed since. And not one that could not be read: there
            // is no telling what is there, and better to leave what is showing than to empty it.
            // If the folder itself has gone, its parent's sync removes it.
            if (watched.ContainsKey(nodes[index]) &&
                read[index] is { } directories)
            {
                SyncChildren(nodes[index], directories);
            }
        }
    }

    /// <summary>
    /// Brings a folder's loaded subfolders in line with the disk. The nodes of subfolders still
    /// there are kept, so whatever is expanded beneath them stays expanded.
    /// </summary>
    void SyncChildren(TreeNode node, List<Subfolder> directories)
    {
        var paths = directories
            .Select(_ => _.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (this.SuspendPainting())
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
                    Locations.Same(path, directory.FullPath))
                {
                    // Renamed to a different case.
                    if (nodes[i].Text != directory.Name)
                    {
                        nodes[i].Text = directory.Name;
                        nodes[i].Tag = directory.FullPath;
                    }

                    ShowExpander(nodes[i], directory.HasSubfolders);
                    continue;
                }

                nodes.Insert(i, CreateNode(directory.FullPath, directory.Name, ShellIcons.FolderIcon, directory.HasSubfolders));
            }
        }
    }

    // Gives a folder whose subfolders have not been read an expander when it has some, and
    // takes it away when it has none. A folder being kept up to date is left to that.
    void ShowExpander(TreeNode node, bool hasSubfolders)
    {
        if (watched.ContainsKey(node))
        {
            return;
        }

        if (hasSubfolders)
        {
            if (node.Nodes.Count == 0)
            {
                node.Nodes.Add(new TreeNode { Tag = placeholder });
            }

            return;
        }

        if (NeedsLoading(node))
        {
            node.Nodes.Clear();
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

        FolderWatcher? watcher;
        try
        {
            watcher = new(
                (string)node.Tag!,
                NotifyFilters.DirectoryName,
                () =>
                {
                    changed[node] = true;
                    changes.Raise();
                });
        }
        catch (Exception)
        {
            // Some locations (an offline share, an empty drive) can't be watched. Those are
            // read again whenever they are expanded.
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
        watchedForSelection = null;
    }

    // Keeps the selected folder up to date while it is selected, if nothing else does, and lets
    // go of the one before unless it has been expanded since.
    void WatchSelected(TreeNode? node)
    {
        if (watchedForSelection is { } previous &&
            previous != node)
        {
            watchedForSelection = null;
            if (!previous.IsExpanded)
            {
                Unwatch(previous);
            }
        }

        if (node != null &&
            IsFolder(node) &&
            !watched.ContainsKey(node))
        {
            Watch(node);
            watchedForSelection = node;
        }
    }

    // Brings the folders watchers have seen change up to date. False while that has to wait for
    // the round before it.
    bool SyncChanged()
    {
        if (syncing)
        {
            return false;
        }

        if (!showing ||
            changed.IsEmpty)
        {
            return true;
        }

        var nodes = new List<TreeNode>();
        foreach (var node in changed.Keys)
        {
            changed.TryRemove(node, out _);

            // Not folders collapsed or removed since.
            if (watched.ContainsKey(node))
            {
                nodes.Add(node);
            }
        }

        if (nodes.Count > 0)
        {
            Track(SyncChangedAsync(nodes));
        }

        return true;
    }

    async Task SyncChangedAsync(List<TreeNode> nodes)
    {
        syncing = true;
        try
        {
            await SyncAsync(nodes);
        }
        finally
        {
            syncing = false;
        }
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs eventArgs)
    {
        if (eventArgs.Node is { } node && IsFolder(node))
        {
            if (NeedsLoading(node))
            {
                // Not until it has been read, which is done off the UI thread. It expands then.
                eventArgs.Cancel = true;
                Track(ExpandOnceRead(node));
            }
            else if (!watched.ContainsKey(node))
            {
                // Read before, but not watched while collapsed. It expands as it was, and is
                // brought up to date once it has been read again.
                Watch(node);
                Track(SyncAsync([node]));
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

    // The tree has one selected row, and a picked Pinned or Recent folder hands it on to the
    // folder under This PC. So that the folder still shows as selected where it is pinned, its
    // shortcuts are given the selected look too, and every other shortcut loses it.
    void ShowShortcutsSelected(TreeNode? selected)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var path = selected?.Tag as string;
        foreach (var shortcut in Shortcuts())
        {
            if (shortcut == selected)
            {
                continue;
            }

            var same = Locations.Same((string)shortcut.Tag!, path);
            var item = new TreeViewItem
            {
                Mask = TreeViewItemState | TreeViewItemHandle,
                Item = shortcut.Handle,
                StateMask = TreeViewItemSelected,
                State = same ? TreeViewItemSelected : 0
            };
            SendMessage(Handle, TreeViewSetItem, IntPtr.Zero, ref item);
        }
    }

    IEnumerable<TreeNode> Shortcuts() =>
        Nodes
            .Cast<TreeNode>()
            .Where(_ => _.Tag is Section)
            .SelectMany(_ => _.Nodes.Cast<TreeNode>());

    protected override void OnAfterSelect(TreeViewEventArgs eventArgs)
    {
        base.OnAfterSelect(eventArgs);
        ShowShortcutsSelected(eventArgs.Node);
        WatchSelected(eventArgs.Node);

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
            // Anywhere else on its way to being selected has been overtaken.
            selectVersion++;
            selecting = null;
            return;
        }

        // Already on its way there.
        if (Locations.Same(selecting, path))
        {
            return;
        }

        Track(SelectAsync(path));
    }

    // Goes down to the folder as far as the tree has been read, reads the folders still in the
    // way, off the UI thread and all at once, and goes on from there. With nothing left to read,
    // which is so from the start for anywhere the tree has already been, the folder is selected
    // before this returns.
    async Task SelectAsync(string path)
    {
        var version = ++selectVersion;
        selecting = path;
        try
        {
            var read = new Dictionary<string, List<Subfolder>?>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var (node, unread) = Walk(path, read);
                if (unread.Count == 0)
                {
                    SelectedNode = node;
                    node?.EnsureVisible();
                    return;
                }

                var found = await ReadAsync(unread);

                // Somewhere else was selected meanwhile.
                if (version != selectVersion ||
                    IsDisposed)
                {
                    return;
                }

                for (var index = 0; index < unread.Count; index++)
                {
                    read[unread[index]] = found[index];
                }
            }
        }
        finally
        {
            if (version == selectVersion)
            {
                selecting = null;
            }
        }
    }

    // Walks down from the drive towards the folder, expanding on the way and filling in the
    // folders that have been read for it. Gives the node it reached, and the folders that must
    // be read before it can go further: none once it has gone as far as it can.
    (TreeNode? Node, List<string> Unread) Walk(string path, Dictionary<string, List<Subfolder>?> read)
    {
        if (Locations.IsThisPC(path))
        {
            if (places.ShowComputer)
            {
                return (computerNode, []);
            }

            return (null, []);
        }

        // Only when no drive holds the path (a network share, say) does a Pinned or Recent entry
        // for the folder itself stand in; those have no children, so it has to be the folder
        // exactly.
        var current = computerNode.Nodes
            .Cast<TreeNode>()
            .Where(_ => Locations.IsWithin(path, (string)_.Tag!))
            .MaxBy(_ => ((string)_.Tag!).Length);
        if (current == null)
        {
            return (Shortcuts().FirstOrDefault(_ => Locations.Same((string)_.Tag!, path)), []);
        }

        var currentPath = (string)current.Tag!;
        var segments = path[currentPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        using (this.SuspendPainting())
        {
            for (var index = 0; index < segments.Length; index++)
            {
                if (NeedsLoading(current))
                {
                    if (!read.TryGetValue(currentPath, out var directories))
                    {
                        // This folder, and each one below it on the way, none of which has a
                        // node yet: the last is the parent of the folder being selected.
                        var unread = new List<string> { currentPath };
                        for (var below = index; below < segments.Length - 1; below++)
                        {
                            unread.Add(Path.Join(unread[^1], segments[below]));
                        }

                        return (current, unread);
                    }

                    LoadChildren(current, directories);
                }

                currentPath = Path.Join(currentPath, segments[index]);
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
        }

        return (current, []);
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
        var node = eventArgs.Node;
        QuickMenu.Show(
            this,
            location,
            LodeActions.ForMenu(NodeCommands(path, FileOperations.ReadClipboard() != null, node), settings),
            () => ShowShellMenu(owner, path, location, quickAccess),
            settings.ShowsInMenu(LodeActions.More));
    }

    /// <summary>
    /// The fixed right-click menu for a folder in the tree, the same a folder gets in the list.
    /// A drive cannot be cut, renamed, deleted or given a shortcut. Rename edits
    /// <paramref name="node"/>'s label, so is only offered for a folder under This PC, not a
    /// Pinned or Recent entry. Paste puts what was cut or copied into the folder. A null entry
    /// is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> NodeCommands(string path, bool canPaste = false, TreeNode? node = null)
    {
        var owner = FindForm()?.Handle ?? Handle;
        // A drive, a machine on the network, or a share on one.
        var isDrive = Locations.IsThisPC(Locations.Parent(path) ?? "") ||
                      Locations.IsShare(path);
        var canRename = !isDrive && node is { Parent.Tag: not Section };
        return
        [
            LodeActions.Command("open", () => PathSelected?.Invoke(path)),
            LodeActions.Command("openInNewTab", () => NewTabRequested?.Invoke(path)),
            LodeActions.Command("terminal", () => FileOperations.OpenTerminal(path)),
            LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(path, admin: true)),
            .. ClaudeDesktop.Commands(this, [path]),
            null,
            LodeActions.Command("cut", () => FileOperations.CopyToClipboard([path], true), !isDrive),
            LodeActions.Command("copy", () => FileOperations.CopyToClipboard([path], false)),
            LodeActions.Command("paste", () => PasteInto(path), canPaste),
            LodeActions.Command("rename", () => BeginRename(node), canRename),
            LodeActions.Command("delete", () => FileOperations.Delete(owner, [path], false), !isDrive),
            null,
            LodeActions.Pin([path], places, SetPinned),
            LodeActions.Command("createShortcut", () => CreateShortcut(path), !isDrive),
            LodeActions.Command("copyAsPath", () => FolderView.CopyAsPath([path])),
            LodeActions.Command("properties", () => ShellContextMenu.InvokeVerb(owner, [path], "properties")),
            .. CodeMenu.Commands(this, settings, [path])
        ];
    }

    void PasteInto(string folder)
    {
        if (FileOperations.ReadClipboard() is not { } clipboard)
        {
            return;
        }

        FileOperations.Transfer(FindForm()?.Handle ?? Handle, clipboard.Paths, folder, clipboard.Move);
        if (clipboard.Move)
        {
            Clipboard.Clear();
        }
    }

    void CreateShortcut(string path)
    {
        try
        {
            FileOperations.CreateShortcut(path);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Set while Rename is editing a label, so other clicks don't start an edit.
    TreeNode? renaming;

    void BeginRename(TreeNode? node)
    {
        if (node == null)
        {
            return;
        }

        renaming = node;
        SelectedNode = node;
        node.BeginEdit();
    }

    protected override void OnBeforeLabelEdit(NodeLabelEditEventArgs eventArgs)
    {
        if (eventArgs.Node != renaming)
        {
            eventArgs.CancelEdit = true;
        }

        base.OnBeforeLabelEdit(eventArgs);
    }

    // The folder is renamed on disk, and the label put back: the parent's watcher reads the
    // folder again under its new name.
    protected override void OnAfterLabelEdit(NodeLabelEditEventArgs eventArgs)
    {
        base.OnAfterLabelEdit(eventArgs);
        renaming = null;
        eventArgs.CancelEdit = true;
        if (eventArgs.Node is not { Tag: string path, Parent: { } parent } ||
            eventArgs.Label?.Trim() is not { Length: > 0 } newName ||
            newName == Path.GetFileName(path))
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(
                this,
                "A folder name can't contain any of these characters:  \\ / : * ? \" < > |",
                "Lode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (FileOperations.Rename(FindForm()?.Handle ?? Handle, path, newName))
        {
            changed[parent] = true;
            changes.Raise();
        }
    }

    /// <summary>
    /// Pins a folder to Quick access. Replaced in tests, which must not change the real list.
    /// </summary>
    internal Action<string> Pin;

    /// <summary>
    /// Pins a folder to Quick access, or unpins it. Replaced in tests, which must not change the
    /// real list.
    /// </summary>
    internal Action<string, bool> SetPinned;

    /// <summary>
    /// Moves pinned folders to the end of the Pinned list, one after another, completing once
    /// they have moved. Replaced in tests, which must not change the real list.
    /// </summary>
    internal Func<IReadOnlyList<string>, Task> MovePinnedToEnd;

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
        if (eventArgs.Data?.GetDataPresent(pinnedFormat) == true)
        {
            if (PinnedDropTarget(eventArgs) is { } target)
            {
                if (target.Node.Tag is Section)
                {
                    ShowInsertMark(target.Node.Nodes[0], false);
                }
                else
                {
                    ShowInsertMark(target.Node, target.After);
                }

                return DragDropEffects.Move;
            }

            ClearInsertMark();
            return DragDropEffects.None;
        }

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

    protected override void OnDragLeave(EventArgs eventArgs)
    {
        base.OnDragLeave(eventArgs);
        ClearInsertMark();
    }

    protected override void OnDragOver(DragEventArgs eventArgs)
    {
        base.OnDragOver(eventArgs);
        eventArgs.Effect = DropEffect(eventArgs);
    }

    protected override void OnDragDrop(DragEventArgs eventArgs)
    {
        base.OnDragDrop(eventArgs);
        ClearInsertMark();
        var owner = FindForm()?.Handle ?? Handle;
        if (eventArgs.Data?.GetDataPresent(pinnedFormat) == true)
        {
            // Once the drag is over, so the tree it reorders is no longer the drag's target.
            if (PinnedDropTarget(eventArgs) is { } target)
            {
                BeginInvoke(() => MovePinned(target.Folder, target.Index));
            }

            return;
        }

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
            changes.Dispose();
            UnwatchAll();
        }

        base.Dispose(disposing);
    }
}
