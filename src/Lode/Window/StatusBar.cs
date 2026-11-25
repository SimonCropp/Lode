/// <summary>
/// A window's status bar, along its bottom: what the tab showing has, or has selected, or what
/// Find Results has found, then a newer Lode when there is one, the state of the repository the
/// folder is in, the site its origin is on, and the free space. Each part acts on what it says.
/// </summary>
sealed class StatusBar : IDisposable
{
    Form window;
    Settings settings;
    Action<string> openTab;

    // The instance the bar listens to, so it stops listening to that one when the window closes.
    Updates updates;

    // The bar's tooltips, shown above what they are for. The bar's own are shown below the
    // pointer, and at the bottom of the screen Windows moves them up over it, which the bar
    // takes for the pointer leaving: the tooltip hides, the pointer is over the bar again, and
    // it shows again, over and over.
    ToolTip tip = new()
    {
        ShowAlways = true
    };

    ToolStripStatusLabel textLabel = new()
    {
        TextAlign = ContentAlignment.MiddleLeft
    };

    // Right after what the search box found, while it has found anything or not: the same text
    // searched for inside the files instead, with Find in Files. Then the room left, so the
    // link sits beside the count rather than across the bar from it.
    ToolStripStatusLabel searchInsideLabel = new()
    {
        Name = "searchInside",
        Text = "Search inside the files",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Margin = new(12, 3, 0, 2),
        Visible = false
    };

    ToolStripStatusLabel room = new()
    {
        Spring = true
    };

    // Before the free space: the state of the repository the folder is in, a part for each of
    // what GitState says, each acting on what it says. The branch opens the branches above it,
    // to switch to, as GitHub Desktop's Current branch has them.
    ChevronDropDownButton gitButton = new("", Icons.ChevronUp)
    {
        Name = "git",
        Margin = new(0, 1, 0, 0),
        ToolTipText = "Switch branch (Ctrl+B)",
        Visible = false
    };

    // After the branch, each after a dot: that there are no commits yet, which there is nothing
    // to do about, then the changes, to commit or discard, and the commits to push and to pull.
    Dictionary<GitState.PartKind, (ToolStripStatusLabel Dot, ToolStripItem Item)> gitParts = [];

    // The repository the branch and the parts after it are about, and its state as they say it:
    // what clicking them acts on. Kept while the Diff tab shows, which leaves the bar as the
    // folder's tab had it, and has no folder of its own to ask.
    string? gitRepo;
    GitState? gitState;

    // After the branch: the site the repository's origin is on, GitHub say, which it opens.
    ToolStripStatusLabel siteLabel = new()
    {
        Name = "site",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Margin = new(0, 3, 12, 2),
        Visible = false
    };

    ToolStripStatusLabel spaceLabel = new();

    // Beside the free space: a newer Lode, when there is one. Clicking it opens Options at Updates.
    ToolStripStatusLabel updateLabel = new()
    {
        Name = "update",
        IsLink = true,
        LinkBehavior = LinkBehavior.HoverUnderline,
        ToolTipText = "Open Options to update",
        Margin = new(0, 3, 12, 2),
        Visible = false
    };

    // When the branches last closed, as Environment.TickCount64. Clicking the branch while they
    // are open closes them, as the click goes to the window first, and then the click itself
    // would open them again.
    long branchesClosedAt = long.MinValue / 2;

    /// <param name="window">The window the bar is along the bottom of, whose font and scale it has.</param>
    /// <param name="updates">Whether there is a newer Lode, which the bar follows.</param>
    /// <param name="activeView">The tab showing's view, null while there is none.</param>
    /// <param name="showOptions">Opens Options at the page named.</param>
    /// <param name="openSite">Opens a repository's page on its site, in the browser.</param>
    /// <param name="openTab">Opens a folder in a tab, for a branch's commands.</param>
    public StatusBar(Form window, Settings settings, Updates updates, Func<FolderView?> activeView, Action<string> showOptions, Action<string> openSite, Action<string> openTab)
    {
        this.window = window;
        this.settings = settings;
        this.updates = updates;
        this.openTab = openTab;

        // White, as Explorer's is, with its text further in from the edge, and no grip: Windows
        // 11 windows are resized from their edges.
        // In the window's font, as the folder tree and the list are, rather than the smaller one
        // a tool strip has otherwise.
        Strip = new()
        {
            Renderer = new ExplorerRenderer(),
            SizingGrip = false,
            Font = window.Font,
            Padding = new(window.LogicalToDeviceUnits(10), window.LogicalToDeviceUnits(2), window.LogicalToDeviceUnits(10), window.LogicalToDeviceUnits(2))
        };
        Strip.Items.Add(textLabel);
        Strip.Items.Add(searchInsideLabel);
        Strip.Items.Add(room);
        Strip.Items.Add(updateLabel);
        Strip.Items.Add(gitButton);
        AddGitPart(
            GitState.PartKind.NoCommits,
            new ToolStripStatusLabel
            {
                Margin = new(0, 3, 0, 2)
            });
        AddGitPart(GitState.PartKind.Changes, GitPartButton("Commit or discard them", GitState.PartKind.Changes));
        AddGitPart(GitState.PartKind.Ahead, GitPartButton("Push them", GitState.PartKind.Ahead));
        AddGitPart(GitState.PartKind.Behind, GitPartButton("Pull them", GitState.PartKind.Behind));
        AddGitPart(GitState.PartKind.ForkBehind, GitPartButton("Sync the fork with upstream", GitState.PartKind.ForkBehind));
        Strip.Items.Add(siteLabel);
        Strip.Items.Add(spaceLabel);

        // On the bar now, so the chevron's room is scaled as the bar is. A branch before the
        // name, as GitHub Desktop has.
        gitButton.Padding = gitButton.Padding with
        {
            Right = gitButton.Padding.Right + gitButton.ChevronWidth
        };
        gitButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        gitButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        gitButton.Image = Icons.Bitmap(Icons.Branch, window.LogicalToDeviceUnits(16), SystemColors.ControlText, window.LogicalToDeviceUnits(4));
        gitButton.Click += (_, _) => ShowBranches();
        updateLabel.Click += (_, _) => showOptions("Updates");
        searchInsideLabel.Click += (_, _) => activeView()?.SearchInsideTheFiles();
        siteLabel.Click += (_, _) =>
        {
            if (siteLabel.Tag is RemoteSite site)
            {
                openSite(site.Url);
            }
        };
        foreach (ToolStripItem item in Strip.Items)
        {
            item.MouseHover += (_, _) => ShowTip(item);
            item.MouseLeave += (_, _) => tip.Hide(Strip);
            item.MouseDown += (_, _) => tip.Hide(Strip);
        }

        updates.Changed += OnUpdatesChanged;
        ShowUpdate();
    }

    /// <summary>
    /// The strip the bar is, for the window to dock along its bottom.
    /// </summary>
    public StatusStrip Strip { get; }

    /// <summary>
    /// Shows what the tab showing has: what is selected in it, or what it holds, the search
    /// inside the files while its search box has found anything, and its repository, site and
    /// free space. Only ever the active view, with its selection as the actions bar has it.
    /// </summary>
    public void Update(FolderView view, IReadOnlyList<FileEntry> selection)
    {
        textLabel.Text = view.StatusFor(selection);
        var searchesInside = view.CanSearchInsideTheFiles;
        if (searchInsideLabel.Visible != searchesInside)
        {
            searchInsideLabel.Visible = searchesInside;
        }

        if (searchesInside)
        {
            searchInsideLabel.ToolTipText = $"Search for \"{view.Filter}\" in what the files hold, with Find in Files (Ctrl+Enter)";
        }

        ShowGitParts(view.GitRepo, view.GitRepoState);
        var site = view.GitSite;
        siteLabel.Tag = site;
        siteLabel.Visible = site != null;
        if (site != null)
        {
            siteLabel.Text = site.Name;
            siteLabel.ToolTipText = $"Open {site.Url}";
        }

        spaceLabel.Text = view.FreeSpaceText;
    }

    /// <summary>
    /// As Find Results shows: what it has found so far, <paramref name="progress"/>, and none of
    /// what is a folder's, its free space, its repository, or searching inside its files.
    /// </summary>
    public void ShowResults(string progress)
    {
        ShowProgress(progress);
        searchInsideLabel.Visible = false;
        spaceLabel.Text = "";
        siteLabel.Visible = false;
        ShowGitParts(null, null);
    }

    /// <summary>
    /// What Find Results has found so far, ten times a second while searching. Set once, and
    /// only when it says something else: the status bar emptied, even for a moment, is shorter,
    /// and everything above it moves.
    /// </summary>
    public void ShowProgress(string text)
    {
        if (textLabel.Text != text)
        {
            textLabel.Text = text;
        }
    }

    // Its text as of now: the site's changes with the folder.
    void ShowTip(ToolStripItem item)
    {
        var text = item.ToolTipText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Windows draws tooltips in the status font, with a margin round the text.
        var font = SystemFonts.StatusFont ?? window.Font;
        var height = TextRenderer.MeasureText(text, font).Height + window.LogicalToDeviceUnits(12);
        tip.Show(text, Strip, TipLocation(item.Bounds, height, window.LogicalToDeviceUnits(4)), tip.AutoPopDelay);
    }

    /// <summary>
    /// Where a tooltip <paramref name="height"/> high goes for a status bar item at
    /// <paramref name="item"/>, in the bar's coordinates: over the item's left, wholly above the
    /// bar with a <paramref name="gap"/>, so never under the pointer, which is on the bar.
    /// </summary>
    internal static Point TipLocation(Rectangle item, int height, int gap) =>
        new(item.Left, -height - gap);

    void AddGitPart(GitState.PartKind kind, ToolStripItem item)
    {
        var dot = new ToolStripStatusLabel("·")
        {
            Margin = new(0, 3, 0, 2),
            Visible = false
        };
        item.Visible = false;
        Strip.Items.Add(dot);
        Strip.Items.Add(item);
        gitParts[kind] = (dot, item);
    }

    ToolStripButton GitPartButton(string toolTip, GitState.PartKind kind)
    {
        var button = new ToolStripButton
        {
            Name = $"git{kind}",
            Margin = new(0, 1, 0, 0),
            AutoToolTip = false,
            ToolTipText = toolTip
        };
        button.Click += (_, _) => ActOnGit(kind);
        return button;
    }

    // The changes, ahead, behind or behind upstream clicked: asks what to do about them, pull
    // say. Those of the repository the bar shows, which is the Diff tab's too while it shows.
    void ActOnGit(GitState.PartKind kind)
    {
        if (gitRepo is { } repo &&
            gitState is { } state)
        {
            GitStatusActions.Act(window, kind, repo, state, settings.CodeDirectory);
        }
    }

    // A part for each of what the state of the repository says, the last spaced from what
    // follows. Each is set once, to what it is to be: hidden and shown again, the bar would be
    // laid out again for every status update, each change of selection say, closing a tooltip
    // open over it.
    void ShowGitParts(string? repo, GitState? state)
    {
        gitRepo = repo;
        gitState = state;
        var parts = state?.Parts() ?? [];
        var texts = parts.ToDictionary(_ => _.Kind, _ => _.Text);
        ToolStripItem? last = null;
        if (parts.Count > 0)
        {
            last = ItemOf(parts[^1].Kind);
        }

        Show(gitButton, texts.GetValueOrDefault(GitState.PartKind.Branch));
        foreach (var (kind, (dot, item)) in gitParts)
        {
            var text = texts.GetValueOrDefault(kind);
            dot.Visible = text != null;
            Show(item, text);
        }

        ToolStripItem ItemOf(GitState.PartKind kind)
        {
            if (kind == GitState.PartKind.Branch)
            {
                return gitButton;
            }

            return gitParts[kind].Item;
        }

        void Show(ToolStripItem item, string? text)
        {
            item.Visible = text != null;
            if (text == null)
            {
                return;
            }

            item.Text = text;

            // The chevron sits closer to the branch's right edge than text does to a part's, so
            // the dot after it gets the difference.
            var right = 0;
            if (item == last)
            {
                right = window.LogicalToDeviceUnits(12);
            }
            else if (item == gitButton)
            {
                right = window.LogicalToDeviceUnits(4);
            }

            item.Margin = item.Margin with
            {
                Right = right
            };
        }
    }

    /// <summary>
    /// Ctrl+B, or the branch clicked: the branches of the repository the bar shows, above the
    /// branch, found as they open. Outside a repository, nothing.
    /// </summary>
    public void ShowBranches()
    {
        if (!gitButton.Visible ||
            gitButton.Owner == null ||
            gitRepo is not { } repo ||
            Environment.TickCount64 - branchesClosedAt < 500)
        {
            return;
        }

        var state = gitState;
        var picker = BranchPicker.Show(
            window,
            gitButton.Owner.RectangleToScreen(gitButton.Bounds),
            Task.Run(() => GitBranches.Read(repo, Cancel.None)),
            branch => CodeMenu.SwitchBranch(window, repo, state, branch),
            name => CodeMenu.NewBranch(window, repo, name),
            (listing, branch) => CodeMenu.BranchCommands(window, repo, listing, branch, openTab));
        picker.FormClosed += (_, _) => branchesClosedAt = Environment.TickCount64;
    }

    void OnUpdatesChanged()
    {
        if (window.IsDisposed)
        {
            return;
        }

        if (window.InvokeRequired)
        {
            window.BeginInvoke(ShowUpdate);
            return;
        }

        ShowUpdate();
    }

    void ShowUpdate()
    {
        var available = updates.Available;
        updateLabel.Visible = available != null;
        if (available != null)
        {
            updateLabel.Text = $"Lode {available.ToNormalizedString()} is available";
        }
    }

    public void Dispose()
    {
        updates.Changed -= OnUpdatesChanged;
        tip.Dispose();
    }
}
