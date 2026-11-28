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

    /// <summary>
    /// What tags the Network heading, which is no folder: Lode can't list the network itself.
    /// </summary>
    public sealed record NetworkHeading;

    const int unscaledRowHeight = 32;
    const int unscaledIndent = 8;
    const int unscaledIconSize = 16;

    static object placeholder = new();

    /// <summary>
    /// The tree's icons at each scale it has been on. A node holds its icon's index in the system
    /// image list, which is at the scale Windows started at, and which the tree shows on a
    /// monitor at that scale. On a monitor at another, the tree shows a list of its own for that
    /// scale, with each icon a tree shows at that same index, so the nodes keep theirs, and the
    /// rest of the list blank. Every tree on a monitor of a scale shares the one list, and only
    /// the icons trees show are drawn into it, rather than every icon the system list has.
    /// </summary>
    static class ScaledIcons
    {
        static Lock gate = new();
        static HashSet<int> shown = [];
        static Dictionary<int, Set> sets = [];
        static int systemDpi = (int)GetDpiForSystem();

        sealed class Set
        {
            ImageList images = new() { ColorDepth = ColorDepth.Depth32Bit };
            Bitmap blank;

            public Set(int iconSize)
            {
                images.ImageSize = new(iconSize, iconSize);
                blank = new(iconSize, iconSize, PixelFormat.Format32bppArgb);

                // Created now so that each image is copied in as it is added. Until an image
                // list has its handle it only keeps a reference to what it is given.
                Handle = images.Handle;
                foreach (var icon in shown)
                {
                    Add(icon);
                }
            }

            public IntPtr Handle { get; }

            // At the system list's index for it, the indexes before it blank until a tree shows
            // the icon that is at one of them.
            public void Add(int icon)
            {
                while (images.Images.Count <= icon)
                {
                    images.Images.Add(blank);
                }

                var size = images.ImageSize;
                using var image = ShellIcons.Render(icon, size, Point.Empty, 0, size.Width);
                images.Images[icon] = image;
            }
        }

        /// <summary>
        /// How big an icon is at the scale of <paramref name="dpi"/>, in pixels: as big as the
        /// system list's are at the scale Windows started at, scaled.
        /// </summary>
        public static int Size(int dpi) =>
            ShellIcons.SmallIconSize.Width * dpi / systemDpi;

        // Tests make trees on more than one UI thread; the app has one.
        public static IntPtr Handle(int dpi)
        {
            if (dpi == systemDpi)
            {
                return ShellIcons.SmallImageList;
            }

            lock (gate)
            {
                if (!sets.TryGetValue(dpi, out var set))
                {
                    set = new(Size(dpi));
                    sets[dpi] = set;
                }

                return set.Handle;
            }
        }

        /// <summary>
        /// The system list's <paramref name="icon"/>, for a node to show: drawn into the list for
        /// each scale, and into one made later, as the node may be on its monitor by then.
        /// </summary>
        public static int Shown(int icon)
        {
            lock (gate)
            {
                if (icon >= 0 &&
                    shown.Add(icon))
                {
                    foreach (var set in sets.Values)
                    {
                        set.Add(icon);
                    }
                }
            }

            return icon;
        }
    }

    Settings settings;
    Places places;

    TreeNode computerNode = new(Locations.ThisPC)
    {
        Tag = Locations.ThisPC,
        ImageIndex = ScaledIcons.Shown(ShellIcons.ComputerIcon),
        SelectedImageIndex = ShellIcons.ComputerIcon
    };

    // The folders whose subfolders are kept up to date: those loaded and still expanded. Each has
    // a watcher, or null where the location can't be watched. A folder drops out when it is
    // collapsed, and is brought up to date when it is expanded again.
    Dictionary<TreeNode, FolderWatcher?> watched = [];

    // Folders a watcher has seen change. Watcher events arrive on thread-pool threads and can
    // come in bursts, so they only land here; the signal syncs each folder at most once per
    // interval on the UI thread.
    ConcurrentDictionary<TreeNode, bool> changed = [];
    ChangeSignal changes;

    // False while the window is hidden or minimized. Folders that change then wait, and are
    // synced when it shows again.
    bool showing = true;

    /// <summary>
    /// A subfolder as the tree shows it, and whether it has subfolders the tree would show
    /// beneath it, which is what gives it an expander.
    /// </summary>
    record struct Subfolder(string Name, string FullPath, bool HasSubfolders);

    // The selected folder, when it is watched only for being selected: kept up to date while it
    // is, as an expanded folder is, so that one with no subfolders gets its expander as soon as
    // it has one (New folder in the list, say), and one whose last goes loses it.
    TreeNode? watchedForSelection;

    // Folders being read because their expander was clicked, so that clicking it again
    // meanwhile doesn't read them again.
    HashSet<TreeNode> reading = [];

    // A round of bringing changed folders up to date is under way. The next waits for it, so an
    // older reading of a folder can't land after a newer one.
    bool syncing;

    // The folder being selected while the way down to it is read, and which attempt that is, so
    // one overtaken by another gives up.
    string? selecting;
    int selectVersion;

    // Which rebuild the tree is of, so expanding again what an earlier one had expanded gives
    // up. Its own count, not the selecting's: whoever rebuilds selects straight after, which
    // is no reason to stop expanding.
    int rebuildVersion;

    // The folder last asked to be selected. One the tree has not caught up with, just made or
    // renamed, say, is settled short of, and selected once it shows up.
    string? wanted;

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

    /// <summary>
    /// Open in new window on This PC's right-click menu was chosen.
    /// </summary>
    public event Action<string>? NewWindowRequested;

    /// <summary>
    /// New on a folder's right-click menu was chosen: a folder to make in it, or a file of the
    /// type given.
    /// </summary>
    public event Action<string, ShellNew.Entry?>? NewRequested;

    public FolderTree(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        SetPinned = (path, pin) => ShellContextMenu.SetPinned(FindForm()?.Handle ?? Handle, path, pin);
        Pin = _ => SetPinned(_, true);
        MovePinnedToEnd = _ => ShellContextMenu.MoveToEnd(FindForm()?.Handle ?? Handle, _);
        StartDrag = (data, allowed) => DragImage.Drag(this, data, allowed);
        DeleteFolder = (path, permanent) => FileOperations.Delete(FindForm()?.Handle ?? Handle, [path], permanent);
        Tell = (caption, text) => MessageBoxes.Show(this, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        ItemHeight = RowHeight(DeviceDpi);
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

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        BackColor = ExplorerStyle.Window;
        SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
        UseImagesFor(DeviceDpi);
    }

    // Moved to a monitor at another scale: the icons, and the spacing they set, for it.
    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        UseImagesFor(DeviceDpi);
    }

    /// <summary>
    /// The image list the nodes are showing. For tests.
    /// </summary>
    internal IntPtr RowImages =>
        SendMessage(Handle, TreeViewGetImageList, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Shows the icons at the scale of <paramref name="dpi"/>, the monitor's the tree is on, with
    /// the spacing they set. Tests call it, as they run unaware of scale.
    /// </summary>
    internal void UseImagesFor(int dpi)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        SendMessage(Handle, TreeViewSetImageList, IntPtr.Zero, ScaledIcons.Handle(dpi));
        ItemHeight = RowHeight(dpi);
        // Explorer's step in from one level to the next, scaled the same way as the icons: far
        // less than the tree's own, which is a little wider than an icon.
        Indent = unscaledIndent * ScaledIcons.Size(dpi) / unscaledIconSize;
    }

    // Explorer's spacing between rows, scaled the same way as the icons. Always even, as an odd
    // height has the tree made again.
    static int RowHeight(int dpi) =>
        unscaledRowHeight * ScaledIcons.Size(dpi) / unscaledIconSize;

    /// <summary>
    /// Builds the tree again from the settings and places, for what it lists having changed:
    /// hidden folders shown, say. What was expanded under This PC is expanded again, as it is
    /// read again. True when it selected a pinned folder that was just moved, which the caller
    /// then leaves selected.
    /// </summary>
    public bool Rebuild()
    {
        bool selectedMoved;
        if (pinnedMove is {IsCompleted: true})
        {
            pinnedMove = null;
        }

        var expanded = ExpandedFolders();

        // A folder on its way to being selected was being read for the tree that was, by the
        // settings that were. Whoever rebuilds selects again.
        selectVersion++;
        selecting = null;
        rebuildVersion++;
        using (this.SuspendPainting())
        {
            UnwatchAll();
            Nodes.Clear();
            computerNode.Nodes.Clear();
            AddSections();
            if (places.ShowComputer)
            {
                Nodes.Add(computerNode);
                LoadDrives();
                computerNode.Expand();
            }

            networkNode.Nodes.Clear();
            ShowNetwork();
            selectedMoved = SelectMovedPinned();
            ShowShortcutsSelected(SelectedNode);
        }

        if (expanded.Count > 0)
        {
            Track(ExpandAgain(expanded));
        }

        return selectedMoved;
    }

    /// <summary>
    /// Makes Pinned and Recent again from the places, for Quick access having changed: a pin,
    /// an unpin, or a pin moved. The folders under This PC are left as they are, expanded or
    /// not, and still watched. True when it selected a pinned folder that was just moved, which
    /// the caller then leaves selected.
    /// </summary>
    public bool RebuildPlaces()
    {
        if (pinnedMove is {IsCompleted: true})
        {
            pinnedMove = null;
        }

        using (this.SuspendPainting())
        {
            foreach (var section in Nodes.Cast<TreeNode>().Where(_ => _.Tag is Section).ToList())
            {
                UnwatchTree(section);
                Nodes.Remove(section);
            }

            AddSections();

            // A folder newly pinned on a machine not shown yet puts it in Network.
            ShowNetwork();
            var selectedMoved = SelectMovedPinned();
            ShowShortcutsSelected(SelectedNode);
            return selectedMoved;
        }
    }

    // Pinned then Recent, above This PC.
    void AddSections()
    {
        IReadOnlyList<string> recent = [];
        if (settings.ShowRecent)
        {
            recent = places.Recent;
        }

        // Told apart across both sections, which show side by side, as tabs are.
        var labels = Locations.Labels(places.Pinned.Concat(recent));
        var position = AddSection(0, "Pinned", ShellIcons.QuickAccessIcon, places.Pinned, labels);
        AddSection(position, "Recent", ShellIcons.RecentIcon, recent, labels);
    }

    // The folders under This PC that are expanded, shallowest first, as each is expanded again
    // once the one it is in has been.
    List<string> ExpandedFolders()
    {
        var expanded = new List<string>();
        Collect(computerNode);
        Collect(networkNode);
        return expanded
            .OrderBy(_ => _.Length)
            .ToList();

        void Collect(TreeNode node)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (child.IsExpanded &&
                    child.Tag is string path)
                {
                    expanded.Add(path);
                    Collect(child);
                }
            }
        }
    }

    // Each folder that was expanded, and is in the tree again once the one it is in has been
    // expanded, is read and expanded. One gone since is left out, and so is what was in it.
    // Another rebuild meanwhile overtakes it.
    async Task ExpandAgain(List<string> folders)
    {
        var version = rebuildVersion;
        foreach (var folder in folders)
        {
            if (IsDisposed ||
                version != rebuildVersion)
            {
                return;
            }

            if ((FindLoaded(computerNode, folder) ?? FindLoaded(networkNode, folder)) is not { } node)
            {
                continue;
            }

            if (NeedsLoading(node))
            {
                await ExpandOnceRead(node);
                continue;
            }

            node.Expand();
        }
    }

    // The node for a folder among those read so far under it.
    static TreeNode? FindLoaded(TreeNode parent, string path)
    {
        foreach (TreeNode child in parent.Nodes)
        {
            if (child.Tag is not string childPath)
            {
                continue;
            }

            if (Locations.Same(childPath, path))
            {
                return child;
            }

            if (Locations.IsWithin(path, childPath) &&
                FindLoaded(child, path) is { } found)
            {
                return found;
            }
        }

        return null;
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
        Nodes.Cast<TreeNode>().FirstOrDefault(_ => _.Tag is Section {Name: "Pinned"});

    static bool IsPinnedEntry(TreeNode? node) =>
        node is {Tag: string, Parent.Tag: Section {Name: "Pinned"}};

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

    // Alt+Shift+Up and Down move the selected pinned folder up or down the list. The keys of the
    // right-click menu's actions do the same to the selected folder.
    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyData is (Keys.Alt | Keys.Shift | Keys.Up) or (Keys.Alt | Keys.Shift | Keys.Down) &&
            IsPinnedEntry(SelectedNode))
        {
            var node = SelectedNode!;
            var offset = args.KeyCode == Keys.Up ? -1 : 1;
            MovePinned((string) node.Tag!, node.Index + offset);
            args.Handled = true;
            args.SuppressKeyPress = true;
            return;
        }

        if (KeyCommand(args.KeyData) is { } command)
        {
            command.Action();
            args.Handled = true;
            args.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(args);
    }

    /// <summary>
    /// What a key does to the selected folder, as it does in the list: what the right-click
    /// menu's entry for the key does, where the menu would let it. Shift+Delete, which the menu
    /// has no entry for, deletes outright what Delete would send to the Recycle Bin, and
    /// Ctrl+Shift+N makes a folder, as Folder under New does. On This PC, whose menu has none
    /// of a folder's entries, only Alt+Enter does anything: Properties. Null for a key that
    /// does nothing here.
    /// </summary>
    internal QuickMenu.Command? KeyCommand(Keys keyData)
    {
        if (SelectedNode is not {Tag: string path} node)
        {
            return null;
        }

        var permanent = keyData == (Keys.Shift | Keys.Delete);
        if (permanent)
        {
            keyData = Keys.Delete;
        }

        if (MenuAction(keyData) is not { } id)
        {
            return null;
        }

        List<QuickMenu.Command?> commands;
        if (Locations.IsThisPC(path))
        {
            commands = ThisPCCommands(node);
        }
        else
        {
            // The clipboard is only read for the key that pastes.
            var canPaste = id == "paste" &&
                           FileOperations.ReadClipboard() != null;
            commands = NodeCommands(path, canPaste, node);
        }

        var command = commands.FirstOrDefault(_ => _?.Id == id);
        if (command is not {Enabled: true})
        {
            return null;
        }

        if (permanent)
        {
            return command with {Action = () => DeleteFolder(path, true)};
        }

        // New's first entry is Folder.
        if (command.Children is [{ } folder, ..])
        {
            return folder;
        }

        return command;
    }

    // The right-click menu's action that a key does, as it does in the list.
    static string? MenuAction(Keys keyData) =>
        keyData switch
        {
            Keys.F2 => "rename",
            Keys.Delete => "delete",
            Keys.Control | Keys.X => "cut",
            Keys.Control | Keys.C => "copy",
            Keys.Control | Keys.Shift | Keys.C => "copyAsPath",
            Keys.Control | Keys.V => "paste",
            Keys.Alt | Keys.Enter => "properties",
            Keys.Control | Keys.Shift | Keys.N => "new",
            _ => null
        };

    // A pinned folder can be dragged up or down the Pinned list to reorder it, as in Explorer.
    const string pinnedFormat = "Lode.PinnedFolder";

    protected override void OnItemDrag(ItemDragEventArgs args)
    {
        base.OnItemDrag(args);
        // A press with Ctrl held that turns into a drag is no click, so opens no tab.
        controlPressed = null;
        if (args.Item is TreeNode node)
        {
            DragNode(node, args.Button);
        }
    }

    /// <summary>
    /// Starts the drag of a node: a pinned folder, with the left button, up or down the Pinned
    /// list; any other folder out of the tree, to the list, Explorer or another app, as Explorer's
    /// pane allows, a drive or share as a shortcut only. This PC, a heading or a machine on the
    /// network isn't dragged. Each carries a picture of the node's icon, as Explorer's drags do.
    /// </summary>
    internal void DragNode(TreeNode node, MouseButtons button)
    {
        if (button == MouseButtons.Left &&
            IsPinnedEntry(node))
        {
            var pinned = new DataObject();
            pinned.SetData(pinnedFormat, (string) node.Tag!);
            DragImage.Set(pinned, [(node.ImageIndex, 0)], 1, DeviceDpi);
            StartDrag(pinned, DragDropEffects.Move);
            ClearInsertMark();
            return;
        }

        if (node.Tag is not string path ||
            Locations.IsThisPC(path) ||
            Locations.IsServer(path))
        {
            return;
        }

        var allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        if (node.Parent == computerNode ||
            IsDrive(path))
        {
            allowed = DragDropEffects.Link;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragImage.Set(data, [(node.ImageIndex, 0)], 1, DeviceDpi);
        StartDrag(data, allowed);
        ClearInsertMark();
        HighlightDrop(null);
    }

    /// <summary>
    /// Drags what is given, until it is dropped. Tests swap it, as dragging holds the thread.
    /// </summary>
    internal Action<DataObject, DragDropEffects> StartDrag;

    // Where a dragged pinned folder would land: before or after a pinned folder, or first when
    // over the Pinned heading. Null when not over the Pinned list.
    (string Folder, int Index, TreeNode Node, bool After)? PinnedDropTarget(DragEventArgs args)
    {
        if (args.Data?.GetData(pinnedFormat) is not string folder)
        {
            return null;
        }

        var point = PointToClient(new(args.X, args.Y));
        var node = GetNodeAt(point);
        if (node?.Tag is Section {Name: "Pinned"})
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

    // Puts the section at the position given, giving the position after it.
    int AddSection(int position, string name, int icon, IReadOnlyList<string> folders, Dictionary<string, string> labels)
    {
        if (folders.Count == 0)
        {
            return position;
        }

        var section = new TreeNode(name)
        {
            Tag = new Section(name),
            ImageIndex = ScaledIcons.Shown(icon),
            SelectedImageIndex = icon
        };
        foreach (var folder in folders)
        {
            var folderIcon = PinnedIcon(folder);
            // Shortcuts, not a second copy of the folder tree: they have no children, and
            // picking one selects the folder in the tree under This PC.
            section.Nodes.Add(CreateNode(folder, labels[folder], folderIcon, expandable: false));
        }

        Nodes.Insert(position, section);
        if (!settings.CollapsedSections.Contains(name))
        {
            section.Expand();
        }

        return position + 1;
    }

    protected override void OnAfterExpand(TreeViewEventArgs args)
    {
        base.OnAfterExpand(args);
        if (args.Node?.Tag is Section section)
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

        var current = computerNode.Nodes.Cast<TreeNode>().Select(_ => (string) _.Tag!);
        var actual = places.DrivePaths();
        if (!current.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            UpdateDrives(actual);
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

    // Only the drives that came or went: those that stayed keep what is expanded in them, and
    // their watchers. In the order the drives come in.
    void UpdateDrives(IReadOnlyList<string> actual)
    {
        var wanted = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in computerNode.Nodes.Cast<TreeNode>().Where(_ => !wanted.Contains((string) _.Tag!)).ToList())
        {
            UnwatchTree(gone);
            computerNode.Nodes.Remove(gone);
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (index < computerNode.Nodes.Count &&
                Locations.Same(computerNode.Nodes[index].Tag as string, actual[index]))
            {
                continue;
            }

            var node = CreateNode(actual[index], "", ShellIcons.FolderIcon);
            DescribeDrive(node);
            computerNode.Nodes.Insert(index, node);
        }
    }

    // A drive is named by its letter, or as it was last described, until the shell has described
    // it, off the UI thread, and then again once it has, if it is still in the tree.
    void DescribeDrive(TreeNode node)
    {
        var (displayName, _, icon) = ShellIcons.DescribeDrive(
            (string) node.Tag!,
            () =>
            {
                if (!IsDisposed &&
                    IsInTree(node))
                {
                    DescribeDrive(node);
                }
            });
        node.Text = displayName;
        node.ImageIndex = ScaledIcons.Shown(icon);
        node.SelectedImageIndex = icon;
    }

    static TreeNode CreateNode(string path, string text, int icon, bool expandable = true)
    {
        var node = new TreeNode(text)
        {
            Tag = path,
            ImageIndex = ScaledIcons.Shown(icon),
            SelectedImageIndex = icon
        };

        // A folder with subfolders starts with a placeholder child, so it shows an expander
        // before they are read. The real children load on first expand.
        if (expandable)
        {
            node.Nodes.Add(new TreeNode {Tag = placeholder});
        }

        return node;
    }

    // A Pinned or Recent entry.
    // The Network heading, after This PC: the machines Lode knows of, from the folders pinned or
    // recent on them, and every share gone to since Lode started. Expanding one lists what it
    // shares. Lode can't find the machines on the network itself, as Explorer's Network does by
    // network discovery, which can take a minute. Only once there is a machine to show.
    TreeNode networkNode = new("Network")
    {
        Tag = new NetworkHeading()
    };

    SortedSet<string> servers = new(StringComparer.OrdinalIgnoreCase);

    // The shares Lode knows of on those machines: those of the folders pinned or recent, and
    // every share gone to since Lode started. Each is under its machine whether the machine
    // lists it or not: one of its administrative shares, c$ say, which a machine never lists,
    // or a share on one that won't say what it shares at all, as some NASes won't. Left to what
    // the machine lists, the tree would stop on the machine, short of where the tab is.
    HashSet<string> knownShares = new(StringComparer.OrdinalIgnoreCase);

    // The Network heading and its machines, as places and the shares gone to have them: one
    // added since shows, and those that show stay as they are, expanded or not.
    void ShowNetwork()
    {
        foreach (var folder in places.Pinned.Concat(places.Recent))
        {
            if (Locations.Server(folder) is not { } server)
            {
                continue;
            }

            servers.Add(server);
            if (Locations.ChildToward(folder, server) is { } share)
            {
                knownShares.Add(share);
            }
        }

        if (!places.ShowComputer ||
            servers.Count == 0)
        {
            return;
        }

        var icon = ShellIcons.NetworkIcon;
        networkNode.ImageIndex = ScaledIcons.Shown(icon);
        networkNode.SelectedImageIndex = icon;
        if (!Nodes.Contains(networkNode))
        {
            Nodes.Add(networkNode);
        }

        var index = 0;
        foreach (var server in servers)
        {
            if (ServerNode(server) == null)
            {
                networkNode.Nodes.Insert(index, CreateNode(server, server.TrimStart('\\'), icon));
            }

            index++;
        }

        networkNode.Expand();
    }

    // A machine on the network, shown in Network from now on.
    void AddServer(string server)
    {
        if (servers.Add(server))
        {
            ShowNetwork();
        }
    }

    TreeNode? ServerNode(string server) =>
        networkNode.Nodes
            .Cast<TreeNode>()
            .FirstOrDefault(_ => Locations.Same(_.Tag as string, server));

    // A Pinned or Recent entry for the folder itself.
    TreeNode? ShortcutFor(string path) =>
        Shortcuts().FirstOrDefault(_ => Locations.Same((string) _.Tag!, path));

    /// <summary>
    /// Lists what a machine on the network shares, off the UI thread. Tests swap it, having no
    /// network to ask.
    /// </summary>
    internal static Func<string, List<(string Name, bool Hidden)>> ListShares = Shares.List;

    /// <summary>
    /// Finds the folders in a folder on a share, off the UI thread, as those anywhere else are
    /// found. Tests swap it, having no network to ask.
    /// </summary>
    internal static Func<string, EnumerationOptions, SystemItems?, IEnumerable<(string Name, string FullPath)>> FindSharedSubfolders = FindSubfolders;

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
        // A machine's folders are what it shares, those named to be hidden only when hidden
        // items show, as Explorer has them.
        if (Locations.IsServer(path))
        {
            try
            {
                return ListShares(path)
                    .Where(_ => showHidden || !_.Hidden)
                    .Select(_ => new Subfolder(_.Name, Path.Join(path, _.Name), true))
                    .OrderBy(_ => _.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }

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
            var found = Locations.IsNetwork(path)
                ? FindSharedSubfolders(path, options, hiddenSystem)
                : FindSubfolders(path, options, hiddenSystem);
            foreach (var (name, fullPath) in found)
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

    // The folders in a folder that the tree shows, and the address bar's chevrons list, as they
    // are found: hidden ones only when the options do not skip them, and Windows' own only when
    // those are shown.
    internal static IEnumerable<(string Name, string FullPath)> FindSubfolders(string path, EnumerationOptions options, SystemItems? hiddenSystem)
    {
        var found = new FileSystemEnumerable<(string Name, string FullPath, FileAttributes Attributes)>(
            path,
            (ref entry) => (entry.FileName.ToString(), entry.ToFullPath(), entry.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref entry) => entry.IsDirectory
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
    // for each folder, in the same order. The watches given, that failed and could not be
    // started again then, watch again first, so nothing after the read is missed.
    async Task<List<List<Subfolder>?>> ReadAsync(IReadOnlyList<string> paths, IReadOnlyList<FolderWatcher?>? reviving = null)
    {
        var showHidden = settings.ShowHidden;
        var hiddenSystem = settings.HiddenSystemItems();
        var read = await Task.Run(() =>
        {
            foreach (var watcher in reviving ?? [])
            {
                watcher?.Revive();
            }

            return paths
                .Select(_ => ReadSubfolders(_, showHidden, hiddenSystem))
                .ToList();
        });

        for (var index = 0; index < paths.Count; index++)
        {
            read[index] = WithKnownShares(paths[index], read[index]);
        }

        return read;
    }

    // A machine's shares as it listed them, with those known on it that it didn't list, and
    // those alone where it couldn't be asked: a machine that won't say what it shares still
    // expands, to the shares gone to on it, and is asked again the next time it is expanded.
    List<Subfolder>? WithKnownShares(string path, List<Subfolder>? read)
    {
        if (!Locations.IsServer(path))
        {
            return read;
        }

        var unlisted = knownShares
            .Where(_ => Locations.Same(Locations.Server(_), path) &&
                        read?.Any(listed => Locations.Same(listed.FullPath, _)) != true)
            .Select(_ => new Subfolder(Locations.ShareName(_), _, true))
            .ToList();
        if (unlisted.Count == 0)
        {
            return read;
        }

        return (read ?? [])
            .Concat(unlisted)
            .OrderBy(_ => _.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Puts a share known on a machine among the shares it shows, in their order: for a machine
    // that was read before the share was known.
    static TreeNode AddShare(TreeNode machine, string share)
    {
        // One whose shares have not been read has only the stand-in for them, which goes.
        if (NeedsLoading(machine))
        {
            machine.Nodes.Clear();
        }

        var name = Locations.ShareName(share);
        var index = 0;
        while (index < machine.Nodes.Count &&
               StringComparer.CurrentCultureIgnoreCase.Compare(machine.Nodes[index].Text, name) < 0)
        {
            index++;
        }

        var node = CreateNode(share, name, ShellIcons.FolderIcon);
        machine.Nodes.Insert(index, node);
        return node;
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

    // A folder that could not be read, a drive not ready or a mapped drive that can't be
    // reached, keeps its expander and is not watched, so it is read again when next expanded,
    // or on the way down to a folder in it. Emptied and watched, it would never be read again:
    // with no expander it can't be expanded, and a watched folder's parent leaves it be.
    void LoadChildren(TreeNode node, List<Subfolder>? directories)
    {
        if (directories == null)
        {
            return;
        }

        using (this.SuspendPainting())
        {
            node.Nodes.Clear();
            foreach (var directory in directories)
            {
                node.Nodes.Add(CreateNode(directory.FullPath, directory.Name, ShellIcons.GetFolderIcon(directory.FullPath), directory.HasSubfolders));
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
            var read = await ReadAsync([(string) node.Tag!]);
            if (IsDisposed ||
                !IsInTree(node))
            {
                return;
            }

            // Unless the way down to a folder being selected got here first.
            if (NeedsLoading(node))
            {
                // Not read: there is nothing to show under it, and expanding it would only try
                // again now.
                if (read[0] is not { } directories)
                {
                    return;
                }

                LoadChildren(node, directories);
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
        var read = await ReadAsync(
            nodes.Select(_ => (string) _.Tag!).ToList(),
            nodes.Select(_ => watched.GetValueOrDefault(_)).ToList());
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

        // The folder asked to be selected, or one on the way down to it, has shown up.
        var arrived = false;
        using (this.SuspendPainting())
        {
            foreach (var child in node.Nodes.Cast<TreeNode>().ToList())
            {
                if (child.Tag is string path &&
                    paths.Contains(path))
                {
                    continue;
                }

                // Settle the selection on the folder rather than leave the control to pick some
                // other node. Set from code, so it doesn't navigate.
                if (SelectedNode != null &&
                    IsSelfOrDescendant(SelectedNode, child))
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

                nodes.Insert(i, CreateNode(directory.FullPath, directory.Name, ShellIcons.GetFolderIcon(directory.FullPath), directory.HasSubfolders));
                if (wanted != null &&
                    Locations.IsWithin(wanted, directory.FullPath))
                {
                    arrived = true;
                }
            }
        }

        if (arrived)
        {
            SelectPath(wanted!);
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
                node.Nodes.Add(new TreeNode {Tag = placeholder});
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

        // Watched from now, as far as the tree goes. Starting the watcher asks whether the folder
        // is there and opens it, which waits on a mapped drive that is offline, or a disk that has
        // to spin up, so is done off the UI thread, and the watcher given to the folder once made.
        watched[node] = null;
        var path = (string) node.Tag!;
        Track(StartWatching(node, path));
    }

    async Task StartWatching(TreeNode node, string path)
    {
        var watcher = await Task.Run(() => MakeWatcher(
            path,
            () =>
            {
                changed[node] = true;
                changes.Raise();
            }));

        // Let go of meanwhile, or another made for it first, or the tree gone.
        if (IsDisposed ||
            !watched.TryGetValue(node, out var current) ||
            current != null ||
            !Locations.Same(node.Tag as string, path))
        {
            watcher?.Dispose();
            return;
        }

        watched[node] = watcher;
    }

    /// <summary>
    /// Makes the watcher for a folder, on the thread pool, or null where it can't be watched:
    /// an offline share, an empty drive. Those are read again whenever they are expanded. Tests
    /// swap it, for a drive slow to answer.
    /// </summary>
    internal Func<string, Action, FolderWatcher?> MakeWatcher = static (path, changed) =>
    {
        try
        {
            return new(path, NotifyFilters.DirectoryName, changed);
        }
        catch (Exception)
        {
            return null;
        }
    };

    /// <summary>
    /// Whether the folder at <paramref name="path"/> is watched, for tests.
    /// </summary>
    internal bool IsWatched(string path) =>
        watched.Keys
            .Any(_ => _.Tag is string tag &&
                      Locations.Same(tag, path));

    /// <summary>
    /// Whether the folder at <paramref name="path"/> has its watcher, made off the UI thread,
    /// for tests.
    /// </summary>
    internal bool HasWatcher(string path) =>
        watched
            .Any(_ => _.Value != null &&
                      _.Key.Tag is string tag &&
                      Locations.Same(tag, path));

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

            // What it shows is from when it was last read, before it was collapsed, or, for one
            // with no expander, from its parent's read. Read again, so a folder made in it since
            // shows, and is selected when it is the one wanted: being watched now, it would
            // otherwise not be read again, not even when expanded. One not read yet is read
            // when it is expanded.
            if (!NeedsLoading(node))
            {
                Track(SyncAsync([node]));
            }
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

    protected override void OnBeforeExpand(TreeViewCancelEventArgs args)
    {
        if (args.Node is { } node &&
            IsFolder(node))
        {
            if (NeedsLoading(node))
            {
                // Not until it has been read, which is done off the UI thread. It expands then.
                args.Cancel = true;
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

        base.OnBeforeExpand(args);
    }

    protected override void OnAfterCollapse(TreeViewEventArgs args)
    {
        base.OnAfterCollapse(args);
        if (args.Node?.Tag is Section section &&
            !settings.CollapsedSections.Contains(section.Name))
        {
            settings.CollapsedSections.Add(section.Name);
        }

        if (args.Node is { } node &&
            IsFolder(node))
        {
            UnwatchUnlessSelected(node);
        }
    }

    // For a folder that no longer shows what is in it, collapsed say. The one selected is
    // watched while it is, though, and being selected already, no AfterSelect is coming to
    // watch it again: let go of, a folder made in it and gone to would never show, the tree
    // staying where it was.
    void UnwatchUnlessSelected(TreeNode node)
    {
        if (node == SelectedNode &&
            watched.ContainsKey(node))
        {
            watchedForSelection = node;
            return;
        }

        Unwatch(node);
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

            var same = Locations.Same((string) shortcut.Tag!, path);
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

    protected override void OnAfterSelect(TreeViewEventArgs args)
    {
        base.OnAfterSelect(args);
        ShowShortcutsSelected(args.Node);
        WatchSelected(args.Node);

        // Unknown means the selection was set from code (SelectPath), not by the user.
        if (args.Action != TreeViewAction.Unknown &&
            args.Node is {Tag: string path} node)
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
    /// Moves the selection to the node for <paramref name="path"/>, expanding down to it. With
    /// <see cref="Settings.ExpandToOpenFolder"/> off, only selects it where it already shows.
    /// </summary>
    public void SelectPath(string path)
    {
        wanted = path;

        if (!settings.ExpandToOpenFolder)
        {
            SelectShowing(path);
            return;
        }

        // A selected shortcut still moves to the folder in the tree below, when there is one.
        if (SelectedNode is {Tag: string selected} current &&
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

    // Without expanding to the open folder, as Explorer's tree is by default: nothing is read,
    // expanded or scrolled to. The folder is selected where it already shows, a pinned or recent
    // entry for it included, and otherwise nothing is, rather than a folder it has left.
    void SelectShowing(string path)
    {
        // Anywhere on its way to being selected, from before the option was turned off, has
        // been overtaken.
        selectVersion++;
        selecting = null;

        // A pinned entry just picked stays selected, rather than handing on to the folder below.
        if (SelectedNode is {Tag: string selected} &&
            Locations.Same(selected, path))
        {
            return;
        }

        Select(ShowingNode(path));
    }

    // Selects the node, or nothing. Taking the selection away raises no AfterSelect, so a
    // shortcut would keep its selected look, and the folder before stay watched.
    void Select(TreeNode? node)
    {
        SelectedNode = node;
        if (node == null)
        {
            ShowShortcutsSelected(null);
            WatchSelected(null);
        }
    }

    // The node for the folder where the tree shows it: under This PC, down through folders that
    // are expanded, or else an entry in a Pinned or Recent section that is. Null where it
    // doesn't show.
    TreeNode? ShowingNode(string path)
    {
        if (Locations.IsThisPC(path))
        {
            if (places.ShowComputer)
            {
                return computerNode;
            }

            return null;
        }

        TreeNode? current = null;
        if (places.ShowComputer &&
            computerNode.IsExpanded)
        {
            current = computerNode.Nodes
                .Cast<TreeNode>()
                .Where(_ => Locations.IsWithin(path, (string) _.Tag!))
                .MaxBy(_ => ((string) _.Tag!).Length);
        }

        if (current == null &&
            networkNode.IsExpanded &&
            Locations.Server(path) is { } server)
        {
            current = ServerNode(server);
        }

        while (current != null)
        {
            if (Locations.Same((string) current.Tag!, path))
            {
                return current;
            }

            if (!current.IsExpanded)
            {
                break;
            }

            current = current.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(_ => _.Tag is string child &&
                                     Locations.IsWithin(path, child));
        }

        return Nodes
            .Cast<TreeNode>()
            .Where(_ => _ is {Tag: Section, IsExpanded: true})
            .SelectMany(_ => _.Nodes.Cast<TreeNode>())
            .FirstOrDefault(_ => Locations.Same((string) _.Tag!, path));
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
                    Select(node);
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
            .Where(_ => Locations.IsWithin(path, (string) _.Tag!))
            .MaxBy(_ => ((string) _.Tag!).Length);

        // On a share, under the machine it is on, in Network, as Explorer has it: the machine
        // added there, if it isn't yet, and the share known on it from now on, listed or not.
        if (current == null &&
            places.ShowComputer &&
            Locations.Server(path) is { } server)
        {
            AddServer(server);
            current = ServerNode(server);
            if (Locations.ChildToward(path, server) is { } share)
            {
                knownShares.Add(share);
            }
        }

        if (current == null)
        {
            return (ShortcutFor(path), []);
        }

        var currentPath = (string) current.Tag!;
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
                        var unread = new List<string> {currentPath};
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

                // The share, known now, on a machine read before it was.
                if (child == null &&
                    Locations.IsServer((string) current.Tag!))
                {
                    child = AddShare(current, currentPath);
                }

                if (child == null)
                {
                    // A folder on a share that can't be read, its machine offline say, is
                    // selected by a Pinned or Recent entry for it, where there is one, as it
                    // was before Network: better than the share.
                    if (ShortcutFor(path) is { } shortcut &&
                        Locations.IsNetwork(path))
                    {
                        return (shortcut, []);
                    }

                    // Hidden or since-deleted folder: settle for the deepest ancestor we have.
                    // Read on the way, it was watched, as a folder showing what is in it is.
                    // Not expanded, it is watched only while selected, as any other is.
                    if (!current.IsExpanded)
                    {
                        UnwatchUnlessSelected(current);
                    }

                    break;
                }

                current.Expand();
                current = child;
            }
        }

        return (current, []);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs args)
    {
        base.OnNodeMouseClick(args);
        if (args.Button != MouseButtons.Right ||
            args.Node?.Tag is not string path)
        {
            return;
        }

        var owner = FindForm()?.Handle ?? Handle;
        var location = PointToScreen(args.Location);
        var quickAccess = args.Node.Parent?.Tag is Section;
        var node = args.Node;
        List<QuickMenu.Command?> commands;
        if (Locations.IsThisPC(path))
        {
            commands = ThisPCCommands(node);
        }
        else
        {
            commands = NodeCommands(path, FileOperations.ReadClipboard() != null, node);
        }

        QuickMenu.Show(
            this,
            location,
            LodeActions.ForMenu(commands, settings),
            async () => await ShowMoreOptions(owner, path, location, quickAccess, node),
            settings.ShowsInMenu(LodeActions.More));
    }

    // WM_LBUTTONDOWN and the other presses that open a folder in a new tab, and the MK_CONTROL
    // and MK_SHIFT a mouse message's WPARAM has for the keys held.
    const int leftButtonDown = 0x0201;
    const int leftButtonDoubleClick = 0x0203;
    const int middleButtonDown = 0x0207;
    const int middleButtonUp = 0x0208;
    const int middleButtonDoubleClick = 0x0209;
    const int controlHeld = 0x0008;
    const int shiftHeld = 0x0004;

    // The row pressed with Ctrl held, while the tree works out whether the press is a click or
    // the start of a drag. Null again once a drag starts: Ctrl held for a drag is the drag's,
    // to copy.
    TreeNode? controlPressed;

    // The row the middle button went down on, which opens in a new tab if the button comes up
    // over it.
    TreeNode? middlePressed;

    /// <summary>
    /// A folder clicked with the middle button, as in Explorer's pane, or with Ctrl held, as a
    /// link is in a browser, opens in a new tab behind the one showing, which stays where it
    /// is. Its expander still only opens and closes it. Each click opens a tab, a second soon
    /// after the first too, which comes as a double click.
    /// </summary>
    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case leftButtonDown when ControlClicked(message) is { } row:
                PressWithControl(ref message, row);
                return;
            // Kept from the tree, which would open or close the folder.
            case leftButtonDoubleClick when ControlClicked(message) is { } row:
                OpenInNewTab(row);
                return;
            case middleButtonDown or middleButtonDoubleClick:
                middlePressed = NewTabRow(PointOf(message.LParam));
                break;
            case middleButtonUp:
                base.WndProc(ref message);
                MiddleReleased(PointOf(message.LParam));
                return;
        }

        base.WndProc(ref message);
    }

    // The tree doesn't come back from a press until the button is up or a drag is over. A
    // click selects the row on the way, which OnBeforeSelect turns down.
    void PressWithControl(ref Message message, TreeNode row)
    {
        controlPressed = row;
        bool clicked;
        try
        {
            base.WndProc(ref message);
        }
        finally
        {
            clicked = controlPressed != null;
            controlPressed = null;
        }

        if (clicked)
        {
            OpenInNewTab(row);
        }
    }

    void MiddleReleased(Point point)
    {
        var pressed = middlePressed;
        middlePressed = null;
        if (pressed != null &&
            NewTabRow(point) == pressed)
        {
            OpenInNewTab(pressed);
        }
    }

    // The row a press with Ctrl held, and not Shift, is on, when that opens it in a new tab.
    TreeNode? ControlClicked(Message message)
    {
        var keys = (int) message.WParam;
        if ((keys & controlHeld) == 0 ||
            (keys & shiftHeld) != 0)
        {
            return null;
        }

        return NewTabRow(PointOf(message.LParam));
    }

    // The folder, or This PC, at a point, which a click there with the middle button or Ctrl
    // held opens in a new tab. None on an expander, which only opens and closes its folder, nor
    // a section heading, which is no folder.
    TreeNode? NewTabRow(Point point)
    {
        var hit = HitTest(point);
        if (hit.Node is not {Tag: string} node ||
            hit.Location == TreeViewHitTestLocations.PlusMinus)
        {
            return null;
        }

        return node;
    }

    void OpenInNewTab(TreeNode node) =>
        NewTabRequested?.Invoke((string) node.Tag!);

    static Point PointOf(IntPtr lParam) =>
        new((short) (lParam & 0xFFFF), (short) ((lParam >> 16) & 0xFFFF));

    protected override void OnBeforeSelect(TreeViewCancelEventArgs args)
    {
        base.OnBeforeSelect(args);
        // Clicked with Ctrl held, the row opens in a new tab instead.
        if (controlPressed != null &&
            args.Action == TreeViewAction.ByMouse)
        {
            args.Cancel = true;
        }
    }

    /// <summary>
    /// The right-click menu for This PC, as Explorer's has, less Delete and Rename: Expand or
    /// Collapse, Manage, Map network drive, Disconnect network drive, Open in new tab, Open in
    /// new window, Add a network location and Properties. Manage, the network drive entries
    /// and Properties are the shell's own verbs on This PC, and Add a network location the one
    /// on its empty area, so each does what it does in Explorer. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> ThisPCCommands(TreeNode node)
    {
        var owner = FindForm()?.Handle ?? Handle;
        QuickMenu.Command toggle;
        if (node.IsExpanded)
        {
            toggle = new("Collapse", node.Collapse)
            {
                Glyph = Icons.ChevronUp
            };
        }
        else
        {
            toggle = new("Expand", node.Expand)
            {
                Glyph = Icons.ChevronDown
            };
        }

        return
        [
            toggle,
            null,
            new("Manage", () => Verb("Manage"))
            {
                Glyph = Icons.Manage
            },
            new("Map network drive...", () => Verb("connectNetworkDrive"))
            {
                Glyph = Icons.MapDrive
            },
            new("Disconnect network drive...", () => Verb("disconnectNetworkDrive"))
            {
                Glyph = Icons.DisconnectDrive
            },
            LodeActions.Command("openInNewTab", () => NewTabRequested?.Invoke(Locations.ThisPC)),
            new("Open in new window", () => NewWindowRequested?.Invoke(Locations.ThisPC))
            {
                Glyph = Icons.NewWindow
            },
            null,
            new("Add a network location", () => ShellContextMenu.InvokeFolderVerb(owner, Locations.ThisPCParsingName, "addnetworkplace"))
            {
                Glyph = Icons.Network
            },
            null,
            LodeActions.Command("properties", () => Verb("properties"))
        ];

        void Verb(string verb) =>
            ShellContextMenu.InvokeVerb(owner, [Locations.ThisPCParsingName], verb);
    }

    /// <summary>
    /// The fixed right-click menu for a folder in the tree, the same a folder gets in the list.
    /// A drive cannot be cut, renamed, deleted or given a shortcut. Rename edits
    /// <paramref name="node"/>'s label, so is only offered for a folder under This PC, not a
    /// Pinned or Recent entry. New and Paste put things in the folder: New as the list's own New
    /// does, with the folder open in the tab, which a machine on the network can't have, having
    /// shares rather than folders. <paramref name="newTypes"/> stands in for the types Windows
    /// offers under New. A null entry is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> NodeCommands(string path, bool canPaste = false, TreeNode? node = null, IReadOnlyList<ShellNew.Entry>? newTypes = null)
    {
        var owner = FindForm()?.Handle ?? Handle;
        // A drive, a machine on the network, or a share on one.
        var isDrive = IsDrive(path);
        var canRename = CanRename(path, node);
        var newCommands = FolderView.NewCommands(
            newTypes ?? ShellNew.Current,
            () => NewRequested?.Invoke(path, null),
            _ => NewRequested?.Invoke(path, _));
        return
        [
            LodeActions.Command("open", () => PathSelected?.Invoke(path)),
            LodeActions.Command("openInNewTab", () => NewTabRequested?.Invoke(path)),
            LodeActions.Command("terminal", () => FileOperations.OpenTerminal(path)),
            LodeActions.Command("terminalAdmin", () => FileOperations.OpenTerminal(path, admin: true)),
            .. ClaudeDesktop.Commands(this, [path]),
            .. CodingAgents.Commands([path]),
            null,
            LodeActions.Submenu("new", newCommands) with {Enabled = !Locations.IsServer(path)},
            LodeActions.Command("cut", () => FileOperations.CopyToClipboard([path], true), !isDrive),
            LodeActions.Command("copy", () => FileOperations.CopyToClipboard([path], false)),
            LodeActions.Command("paste", () => PasteInto(path), canPaste),
            LodeActions.Command("rename", () => BeginRename(node), canRename),
            LodeActions.Command("delete", () => DeleteFolder(path, false), !isDrive),
            null,
            LodeActions.Pin([path], places, SetPinned),
            LodeActions.Command("createShortcut", () => CreateShortcut(path), !isDrive),
            LodeActions.Command("compress", () => _ = Zip.CompressInBackground([path], Path.GetDirectoryName(path)!), !isDrive),
            LodeActions.Command("copyAsPath", () => FolderView.CopyAsPath([path])),
            LodeActions.Command("properties", async () => await ShowProperties(owner, path)),
            .. CodeMenu.Commands(this, settings, [path])
        ];
    }

    void PasteInto(string folder)
    {
        if (FileOperations.ReadClipboard() is not { } clipboard)
        {
            return;
        }

        _ = FileOperations.Paste(FindForm()?.Handle ?? Handle, clipboard, folder);
    }

    void CreateShortcut(string path)
    {
        try
        {
            FileOperations.CreateShortcut(path);
        }
        catch (Exception exception)
        {
            MessageBoxes.Show(this, exception.Message, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    protected override void OnBeforeLabelEdit(NodeLabelEditEventArgs args)
    {
        if (args.Node != renaming)
        {
            args.CancelEdit = true;
        }

        base.OnBeforeLabelEdit(args);
    }

    // The folder is renamed on disk, and the label put back: the parent's watcher reads the
    // folder again under its new name.
    protected override void OnAfterLabelEdit(NodeLabelEditEventArgs args)
    {
        base.OnAfterLabelEdit(args);
        renaming = null;
        args.CancelEdit = true;
        if (args.Node is not {Tag: string path, Parent: { } parent} ||
            args.Label?.Trim() is not {Length: > 0} newName ||
            newName == Path.GetFileName(path))
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBoxes.Show(
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

    /// <summary>
    /// Deletes a folder: to the Recycle Bin, or outright when permanent. Replaced in tests, which
    /// must not have the shell delete anything.
    /// </summary>
    internal Action<string, bool> DeleteFolder;

    // A drive, a machine on the network, or a share on one.
    static bool IsDrive(string path) =>
        Locations.IsThisPC(Locations.Parent(path) ?? "") ||
        Locations.IsShare(path);

    // Not a drive, nor a Pinned or Recent entry, which is Quick access's, not a folder's.
    static bool CanRename(string path, TreeNode? node) =>
        !IsDrive(path) &&
        node is {Parent.Tag: not Section};

    /// <summary>
    /// Whether a folder is on the network, where asking the shell about it asks the machine it is
    /// on: a machine itself, a share, a folder on one, or on a drive mapped to one. Tests swap
    /// it, to have a folder of theirs count.
    /// </summary>
    internal Func<string, bool> IsRemote = Locations.IsRemote;

    /// <summary>
    /// Asks the machine a folder on the network is on about it, returning once the machine has
    /// answered, whatever it answers: the folder there or not, Lode let in or not. The shell,
    /// asked about the folder then, has its answer as quickly. A machine that is off, or can't
    /// be reached, off the VPN say, has Windows wait twenty seconds or more before it gives up.
    /// Never asked on the UI thread. Tests swap it, to see where it is asked, and to stand in for
    /// a machine that doesn't answer.
    /// </summary>
    internal Action<string> AskMachine = path =>
    {
        // A machine has no folder of its own to ask about, so is asked what it shares, as
        // expanding it asks.
        if (Locations.IsServer(path))
        {
            _ = Shares.Answers(path);
            return;
        }

        _ = Directory.Exists(path);
    };

    /// <summary>
    /// How long a machine has to answer before the tree says it can't be reached. One that is
    /// there answers in well under a second, over a VPN too. Tests shorten it.
    /// </summary>
    internal TimeSpan MachineWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Says a machine can't be reached. Tests listen in.
    /// </summary>
    internal Action<string, string> Tell;

    /// <summary>
    /// Show more options: the shell's own menu for the folder, at <paramref name="location"/>,
    /// once the machine it is on has answered, for one on the network: see
    /// <see cref="MachineAnswers"/>. Completes once the menu has closed, or the tree has said
    /// the machine can't be reached.
    /// </summary>
    internal async Task ShowMoreOptions(IntPtr owner, string path, Point location, bool quickAccess, TreeNode node)
    {
        if (await MachineAnswers(path))
        {
            ShowShellMenu(owner, path, location, quickAccess, node);
        }
    }

    /// <summary>
    /// The shell's Properties for the folder, once the machine it is on has answered, for one on
    /// the network: see <see cref="MachineAnswers"/>.
    /// </summary>
    internal async Task ShowProperties(IntPtr owner, string path)
    {
        if (await MachineAnswers(path))
        {
            ShellContextMenu.InvokeVerb(owner, [path], "properties");
        }
    }

    // Whether the shell can be asked about the folder now, on the UI thread, without every
    // window waiting on it. Asked about a folder on the network, the shell asks the machine it is
    // on, and waits for it, on the one UI thread all of Lode's windows share: a machine that is
    // off would have them all stop, for as long as Windows takes to give up on it. The guard on
    // shell extensions cancels a call one is stuck in, but not what Windows waits on of the
    // network. So the machine is asked first, off the UI thread, and the shell only once it has
    // answered; one that hasn't in time is said to be unreachable, and its answer, should it
    // come later, is left. A folder not on the network is the shell's straight away.
    async Task<bool> MachineAnswers(string path)
    {
        if (!IsRemote(path))
        {
            return true;
        }

        var ask = AskMachine;
        var asked = Task.Run(() => ask(path));
        if (await Task.WhenAny(asked, Task.Delay(MachineWait)) == asked)
        {
            return !IsDisposed;
        }

        if (!IsDisposed)
        {
            var machine = Locations.Server(path) ?? Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar) ?? path;
            Tell("Lode", $"{machine} can't be reached. Check that it's on, and on the network, then try again.");
        }

        return false;
    }

    void ShowShellMenu(IntPtr owner, string path, Point location, bool quickAccess, TreeNode node)
    {
        // Rename is offered where the tree's own menu offers it, and done by the tree, in place.
        var canRename = CanRename(path, node);
        ShellContextMenu.CustomCommand[] customCommands =
        [
            new("Open in new tab", () => NewTabRequested?.Invoke(path))
        ];

        using var menu = new ShellContextMenu();

        if (Locations.IsThisPC(path))
        {
            menu.ShowForThisPC(owner, location, customCommands, HandleVerb);
            return;
        }

        // Pinned and Recent folders are Quick access entries, whose menu offers Unpin.
        if (quickAccess)
        {
            menu.ShowForQuickAccessItem(owner, path, location, customCommands, HandleVerb, canRename);
            return;
        }

        menu.ShowForItems(owner, [path], location, customCommands, HandleVerb, canRename);

        bool HandleVerb(string verb) =>
            HandleShellVerb(verb, path, node);
    }

    // Done by the tree, as its own menu does them: Rename in place, Open in the window, and
    // Delete and Create shortcut so Ctrl+Z hears of them. Not for This PC or a drive, which its
    // own menu doesn't delete or make a shortcut beside.
    internal bool HandleShellVerb(string verb, string path, TreeNode node)
    {
        if (verb.Equals("rename", StringComparison.OrdinalIgnoreCase) &&
            CanRename(path, node))
        {
            BeginRename(node);
            return true;
        }

        if (!IsDrive(path) &&
            !Locations.IsThisPC(path))
        {
            if (verb.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                DeleteFolder(path, ModifierKeys.HasFlag(Keys.Shift));
                return true;
            }

            if (verb.Equals("link", StringComparison.OrdinalIgnoreCase))
            {
                CreateShortcut(path);
                return true;
            }
        }

        if (!verb.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PathSelected?.Invoke(path);
        return true;
    }

    string? DropDestination(DragEventArgs args)
    {
        var point = PointToClient(new(args.X, args.Y));
        if (GetNodeAt(point)?.Tag is not string path || Locations.IsThisPC(path))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Where a folder dropped at a point is pinned, as in Explorer: on the Pinned heading, at the
    /// end; at the top or bottom of a pinned folder, before or after it, where the insert mark
    /// shows. Over the middle of a pinned folder a drop goes into it, so null, as it is
    /// anywhere else.
    /// </summary>
    internal (int Index, TreeNode? Mark, bool After)? PinTarget(Point clientLocation)
    {
        var node = GetNodeAt(clientLocation);
        if (node?.Tag is Section {Name: "Pinned"})
        {
            return (places.Pinned.Count, null, false);
        }

        if (!IsPinnedEntry(node))
        {
            return null;
        }

        var bounds = node!.Bounds;
        var third = bounds.Height / 3;
        var index = IndexOf(places.Pinned, node.Tag as string);
        if (index < 0)
        {
            return null;
        }

        if (clientLocation.Y < bounds.Top + third)
        {
            return (index, node, false);
        }

        if (clientLocation.Y >= bounds.Bottom - third)
        {
            return (index + 1, node, true);
        }

        return null;

        // Run on each move of a drag, so the list is searched where it is, not copied first.
        static int IndexOf(IReadOnlyList<string> pinned, string? path)
        {
            for (var position = 0; position < pinned.Count; position++)
            {
                if (Locations.Same(pinned[position], path))
                {
                    return position;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Pins the folder where the screen point is in the Pinned list, as <see cref="PinTarget"/>
    /// has it. True when it was there, so the caller treats the drop as handled.
    /// </summary>
    public bool PinIfOverPinned(Point screenLocation, string path)
    {
        ClearInsertMark();
        if (PinTarget(PointToClient(screenLocation)) is not { } target)
        {
            return false;
        }

        PinAt(Pinnable([path]), target.Index);
        return true;
    }

    /// <summary>
    /// Shows where a folder dragged to the screen point would be pinned, as it is dragged: the
    /// insert mark between two pinned folders, or none. For a tab dragged off its row, which is
    /// no drag and drop the tree hears of; null once it has gone.
    /// </summary>
    public void ShowPinTarget(Point? screenLocation)
    {
        if (screenLocation is { } location &&
            PinTarget(PointToClient(location)) is {Mark: { } mark} target)
        {
            ShowInsertMark(mark, target.After);
            return;
        }

        ClearInsertMark();
    }

    /// <summary>
    /// Pins folders at <paramref name="index"/> in the Pinned list. Pinning puts each at the
    /// end, so those pinned already from there on are then moved after them.
    /// </summary>
    internal void PinAt(IReadOnlyList<string> folders, int index)
    {
        if (folders.Count == 0)
        {
            return;
        }

        var after = places.Pinned.Skip(index).ToList();
        foreach (var folder in folders)
        {
            Pin(folder);
        }

        if (after.Count == 0 ||
            pinnedMove != null)
        {
            return;
        }

        pinnedMove = MovePinnedToEnd(after);
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

    // Folders dropped in the Pinned list get pinned to Quick access there, as in Explorer.
    (string[] Folders, int Index, TreeNode? Mark, bool After)? FoldersToPin(DragEventArgs args)
    {
        var point = PointToClient(new(args.X, args.Y));
        if (PinTarget(point) is not { } target ||
            args.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        var folders = FoldersDragged(args.Data, paths)
            .Where(_ => !places.Pinned.Any(pinned => Locations.Same(pinned, _)))
            .ToArray();
        if (folders.Length == 0)
        {
            return null;
        }

        return (folders, target.Index, target.Mark, target.After);
    }

    // The folders among what a drag carries, asked of the disk once a drag rather than at every
    // move of it over the pinned folders: each asking is a trip to the disk, or to a share,
    // with the window waiting on it.
    (object? Data, string[] Folders) dragged;

    string[] FoldersDragged(object data, string[] paths)
    {
        if (!ReferenceEquals(dragged.Data, data))
        {
            dragged = (
                data,
                paths
                    .Where(_ => !Locations.IsThisPC(_) &&
                                Directory.Exists(_))
                    .ToArray());
        }

        return dragged.Folders;
    }

    // TVM_SELECTITEM and TVM_GETNEXTITEM, for the item drawn as where a drop would go.
    const int treeViewSelectItem = 0x110B;
    const int treeViewGetNextItem = 0x110A;
    const int dropHighlight = 0x8;

    /// <summary>
    /// The folder drawn as where what is dragged would be dropped, as Explorer draws it. For
    /// tests.
    /// </summary>
    internal TreeNode? DropHighlighted
    {
        get
        {
            var handle = SendMessage(Handle, treeViewGetNextItem, dropHighlight, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            return TreeNode.FromHandle(this, handle);
        }
    }

    void HighlightDrop(TreeNode? node) =>
        SendMessage(Handle, treeViewSelectItem, dropHighlight, node?.Handle ?? IntPtr.Zero);

    /// <summary>
    /// What a drag over the tree would do, shown as it goes: the folder it would go into
    /// highlighted, or where it would be pinned marked, and said beside the drag's picture, in
    /// Explorer's words.
    /// </summary>
    internal DragDropEffects DropEffect(DragEventArgs args)
    {
        if (args.Data?.GetDataPresent(pinnedFormat) == true)
        {
            HighlightDrop(null);
            if (PinnedDropTarget(args) is { } target)
            {
                if (target.Node.Tag is Section)
                {
                    ShowInsertMark(target.Node.Nodes[0], false);
                }
                else
                {
                    ShowInsertMark(target.Node, target.After);
                }

                DragImage.Describe(args, DropImageType.Move, "Move here", null);
                return DragDropEffects.Move;
            }

            ClearInsertMark();
            DragImage.Describe(args, DragDropEffects.None, null);
            return DragDropEffects.None;
        }

        if (FoldersToPin(args) is { } pin)
        {
            HighlightDrop(null);
            if (pin.Mark is { } mark)
            {
                ShowInsertMark(mark, pin.After);
            }
            else
            {
                ClearInsertMark();
            }

            // Pinned is Quick access's pins.
            DragImage.Describe(args, DropImageType.Link, "Pin to %1", "Quick access");
            return DragDropEffects.Link;
        }

        ClearInsertMark();
        var effect = FileOperations.GetDropEffect(args, DropDestination(args));
        TreeNode? into = null;
        if (effect != DragDropEffects.None)
        {
            into = GetNodeAt(PointToClient(new(args.X, args.Y)));
        }

        HighlightDrop(into);
        DragImage.Describe(args, effect, into?.Text);
        return effect;
    }

    // Whether what is being dragged over the tree is dragged with the right button, which is up
    // by the drop.
    bool rightDrag;

    protected override void OnDragEnter(DragEventArgs args)
    {
        base.OnDragEnter(args);
        dragged = default;
        rightDrag = FileOperations.RightButton(args);
        args.Effect = DropEffect(args);
    }

    protected override void OnDragLeave(EventArgs args)
    {
        base.OnDragLeave(args);
        dragged = default;
        ClearInsertMark();
        HighlightDrop(null);
        heldOver = null;
    }

    protected override void OnDragOver(DragEventArgs args)
    {
        base.OnDragOver(args);
        rightDrag = FileOperations.RightButton(args);
        FollowDrag(PointToClient(new(args.X, args.Y)));
        args.Effect = DropEffect(args);
    }

    // How long a drag is held over a closed folder before it opens.
    static TimeSpan openAfter = TimeSpan.FromMilliseconds(800);

    // How often a drag near the top or bottom scrolls a line.
    static TimeSpan scrollEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The time, which tests set.
    /// </summary>
    internal TimeProvider Time = TimeProvider.System;

    // The node a drag is over, and since when, to open it once it has been held there.
    TreeNode? heldOver;
    DateTimeOffset heldSince;
    DateTimeOffset scrolledAt;

    // WM_VSCROLL, a line up or down.
    const int verticalScroll = 0x115;
    const int lineUp = 0;
    const int lineDown = 1;

    /// <summary>
    /// Follows a drag at a point in the tree, as Explorer's pane does: held over a closed folder
    /// for a moment, it opens it, and near the top or bottom, it scrolls, so a folder anywhere
    /// can be reached. Windows tells the tree of the drag over and over while it is held still,
    /// so neither needs a timer.
    /// </summary>
    internal void FollowDrag(Point location)
    {
        var now = Time.GetUtcNow();
        var edge = ItemHeight;
        var scroll = -1;
        if (location.Y < edge)
        {
            scroll = lineUp;
        }
        else if (location.Y >= ClientSize.Height - edge)
        {
            scroll = lineDown;
        }

        if (scroll >= 0 &&
            now - scrolledAt >= scrollEvery)
        {
            scrolledAt = now;
            SendMessage(Handle, verticalScroll, scroll, IntPtr.Zero);
        }

        var node = GetNodeAt(location);
        if (node != heldOver)
        {
            heldOver = node;
            heldSince = now;
            return;
        }

        if (node is { IsExpanded: false, Nodes.Count: > 0 } &&
            now - heldSince >= openAfter)
        {
            node.Expand();
        }
    }

    protected override void OnDragDrop(DragEventArgs args)
    {
        base.OnDragDrop(args);
        ClearInsertMark();
        HighlightDrop(null);
        heldOver = null;
        var owner = FindForm()?.Handle ?? Handle;
        if (args.Data?.GetDataPresent(pinnedFormat) == true)
        {
            // Once the drag is over, so the tree it reorders is no longer the drag's target.
            if (PinnedDropTarget(args) is { } target)
            {
                BeginInvoke(() => MovePinned(target.Folder, target.Index));
            }

            return;
        }

        if (FoldersToPin(args) is { } pin)
        {
            PinAt(pin.Folders, pin.Index);
            return;
        }

        FileOperations.Drop(owner, args, DropDestination(args), rightDrag);
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
