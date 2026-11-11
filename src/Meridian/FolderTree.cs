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

    public event Action<string>? PathSelected;
    public event Action<string>? NewTabRequested;

    public FolderTree(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        HideSelection = false;
        ShowLines = false;
        FullRowSelect = true;
        HotTracking = true;
        AllowDrop = true;
        BorderStyle = BorderStyle.None;

        // Explorer's spacing between rows, scaled the same way as the icons.
        ItemHeight = unscaledRowHeight * ShellIcons.SmallIconSize.Height / unscaledIconSize;
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
            section.Nodes.Add(CreateNode(folder, Locations.DisplayName(folder), folderIcon));
        }

        Nodes.Add(section);
        section.Expand();
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
        var actual = DriveInfo.GetDrives().Select(_ => _.Name);
        if (!current.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            LoadDrives();
        }
    }

    void LoadDrives()
    {
        computerNode.Nodes.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var (displayName, _, icon) = ShellIcons.Describe(drive.Name);
            computerNode.Nodes.Add(CreateNode(drive.Name, displayName, icon));
        }
    }

    static TreeNode CreateNode(string path, string text, int icon)
    {
        var node = new TreeNode(text)
        {
            Tag = path,
            ImageIndex = icon,
            SelectedImageIndex = icon
        };

        // Every folder starts with a placeholder child so it shows an expander without the
        // cost of checking for subfolders. The real children load on first expand.
        node.Nodes.Add(new TreeNode { Tag = placeholder });
        return node;
    }

    static bool NeedsLoading(TreeNode node) =>
        node.Nodes.Count == 1 && node.Nodes[0].Tag == placeholder;

    void LoadChildren(TreeNode node)
    {
        node.Nodes.Clear();
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
            foreach (var directory in directories)
            {
                node.Nodes.Add(CreateNode(directory.FullName, directory.Name, ShellIcons.FolderIcon));
            }
        }
        catch (Exception)
        {
            // An unreadable folder simply has no children to show.
        }
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs eventArgs)
    {
        if (eventArgs.Node is { } node && NeedsLoading(node))
        {
            LoadChildren(node);
        }

        base.OnBeforeExpand(eventArgs);
    }

    protected override void OnAfterSelect(TreeViewEventArgs eventArgs)
    {
        base.OnAfterSelect(eventArgs);

        // Unknown means the selection was set from code (SelectPath), not by the user.
        if (eventArgs.Action != TreeViewAction.Unknown && eventArgs.Node?.Tag is string path)
        {
            PathSelected?.Invoke(path);
        }
    }

    /// <summary>
    /// Moves the selection to the node for <paramref name="path"/>, expanding down to it.
    /// </summary>
    public void SelectPath(string path)
    {
        if (SelectedNode?.Tag is string selected && Locations.Same(selected, path))
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

        // Start from the closest folder at the top of the tree that contains the path: a pinned
        // or recent folder when there is one, otherwise the drive. On a tie, pinned wins.
        var current = Nodes
            .Cast<TreeNode>()
            .Where(_ => _.Tag is Section)
            .SelectMany(_ => _.Nodes.Cast<TreeNode>())
            .Concat(computerNode.Nodes.Cast<TreeNode>())
            .Where(_ => Locations.IsWithin(path, (string)_.Tag!))
            .MaxBy(_ => ((string)_.Tag!).Length);
        if (current == null)
        {
            return null;
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

        ShellContextMenu.CustomCommand[] customCommands =
        [
            new("Open in new tab", () => NewTabRequested?.Invoke(path))
        ];

        using var menu = new ShellContextMenu();
        menu.ShowForItems(
            FindForm()?.Handle ?? Handle,
            [path],
            PointToScreen(eventArgs.Location),
            customCommands,
            _ =>
            {
                if (!_.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                PathSelected?.Invoke(path);
                return true;
            });
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

    protected override void OnDragEnter(DragEventArgs eventArgs)
    {
        base.OnDragEnter(eventArgs);
        eventArgs.Effect = FileOperations.GetDropEffect(eventArgs, DropDestination(eventArgs));
    }

    protected override void OnDragOver(DragEventArgs eventArgs)
    {
        base.OnDragOver(eventArgs);
        eventArgs.Effect = FileOperations.GetDropEffect(eventArgs, DropDestination(eventArgs));
    }

    protected override void OnDragDrop(DragEventArgs eventArgs)
    {
        base.OnDragDrop(eventArgs);
        FileOperations.Drop(FindForm()?.Handle ?? Handle, eventArgs, DropDestination(eventArgs));
    }
}
