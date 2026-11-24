class MainForm : Form
{
    // Glyphs of the icon font (see Icons) for the address row's buttons.
    const string backGlyph = "\uE72B";
    const string forwardGlyph = "\uE72A";
    const string upGlyph = "\uE74A";
    const string refreshGlyph = "\uE72C";
    const string moreGlyph = "\uE712";
    const string addGlyph = "\uE710";

    const int browserBackwardCommand = 1;
    const int browserForwardCommand = 2;
    const int maximumTabTextLength = 28;

    ToolTip toolTip = new();

    // The status bar's tooltips, shown above what they are for. The bar's own are shown below
    // the pointer, and at the bottom of the screen Windows moves them up over it, which the bar
    // takes for the pointer leaving: the tooltip hides, the pointer is over the bar again, and
    // it shows again, over and over.
    ToolTip statusTip = new()
    {
        ShowAlways = true
    };
    Breadcrumb breadcrumb = new();
    TextBox addressBox;

    /// <summary>
    /// Explorer's command bar, under the address bar: buttons for what is most often done to the
    /// selection in the tab showing.
    /// </summary>
    internal ActionsBar ActionsBar { get; }

    FramedTextBox filterFrame = new();
    TextBox filterBox;
    PaneSplitContainer split = new();
    Settings settings;
    Places places;
    internal FolderTree Tree { get; }
    ActionsPanel actionsPanel = new();
    internal ClosableTabControl Tabs { get; } = new();

    /// <summary>
    /// The Changes and History panes, for the repository the tab showing is in, and the strip
    /// down the right edge they slide out from.
    /// </summary>
    internal GitPanes GitPanes { get; }

    /// <summary>
    /// Where the file picked in a pane shows its changes: the Diff tab, over the folder list, or
    /// the diff window.
    /// </summary>
    internal DiffHost DiffHost { get; }

    ToolStripStatusLabel statusLabel = new()
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

    ToolStripStatusLabel statusRoom = new()
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

    // The instance this window listens to, so it stops listening to that one when it closes.
    Updates updates = Updates.Instance;

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
    Button backButton;
    Button forwardButton;
    Button upButton;
    bool updatingFilter;

    // Folders of tabs closed in this window, most recent last, for Ctrl+Shift+T.
    List<string> closedTabs = [];
    const int maximumClosedTabs = 20;

    // False for a window opened from another: it is placed beside that one rather than where
    // the last window to close was.
    bool restoreBounds = true;

    /// <summary>
    /// A window opened from another one (Ctrl+N, or a tab dragged off), just before it is
    /// shown. The app runs until every window is closed, so it needs to hear of each.
    /// </summary>
    public static event Action<MainForm>? WindowOpened;

    /// <summary>
    /// Exit was chosen from a window's menu. The app otherwise keeps running in the tray.
    /// </summary>
    public static event Action? ExitRequested;

    /// <summary>
    /// When set, closing the last visible window hides it rather than closing it, so the app
    /// keeps running in the tray. Set by the app; tests leave it off.
    /// </summary>
    public static bool HideLastWindowOnClose { get; set; }

    /// <summary>
    /// After every window title: " (Lode Debug)" in a Debug build, so a build being worked on is
    /// easy to tell from the installed Lode. Tests clear it, so titles and captures are the same
    /// in either build.
    /// </summary>
    internal static string TitleSuffix { get; set; } =
#if DEBUG
        " (Lode Debug)";
#else
        "";
#endif

    /// <summary>
    /// When each tab was last looked at is read from this, for closing unused tabs. Tests put in
    /// a clock of their own.
    /// </summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    public FolderView? ActiveView => Tabs.SelectedTab?.Controls.OfType<FolderView>().FirstOrDefault();

    // Every tab but Find Results and the Diff tab holds one.
    public IEnumerable<FolderView> Views =>
        Tabs.TabPages.Cast<TabPage>().SelectMany(_ => _.Controls.OfType<FolderView>());

    static bool HoldsFolder(TabPage page) =>
        page.Controls.OfType<FolderView>().Any();

    /// <summary>
    /// Find in Files: its strip along the bottom, and the Find Results tab.
    /// </summary>
    internal FindHost FindHost { get; }

    public MainForm(string[] arguments, Settings settings, Places places) :
        this(settings, places) =>
        OpenInitialTabs(arguments);

    // A window with no tabs yet.
    MainForm(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        OpenSite = url => FileOperations.Open(this, url);
        Tree = new(settings, places);
        addressBox = breadcrumb.Box;
        filterBox = filterFrame.Box;
        FindHost = new(this, settings, Tabs, () => ActiveView, () => Views, ShowInFolder);
        FindHost.ProgressChanged += ShowResultsProgress;
        GitPanes = new(this, settings)
        {
            FocusList = () => ActiveView?.FocusList()
        };
        GitPanes.ShowInFolderRequested += ShowInFolder;
        GitPanes.OpenSiteRequested += url => OpenSite(url);
        DiffHost = new(this, settings, Tabs, () => ActiveView, () => ActiveView?.FocusList());
        GitPanes.DiffRequested += DiffHost.Request;
        GitPanes.ChangesRead += DiffHost.ChangesRead;
        GitPanes.PinnedChanged += ApplyPinnedInAllWindows;
        GitPanes.StripLabelsChanged += ApplyStripLabelsInAllWindows;
        ActionsBar = new(this, settings, () => ActiveView, ShowOptions, ApplySettingsToAllWindows);
        places.Changed += RebuildPlaces;
        UndoHistory.Instance.Changed += OnUndoChanged;
        updates.Changed += OnUpdatesChanged;
        ActionLog.Changed += ReadGitState;
        trayTimer.Tick += (_, _) =>
        {
            trayTimer.Stop();
            if (hidden)
            {
                ReleaseMemory();
            }
        };
        unusedTabsTimer.Tick += (_, _) =>
        {
            // Not while Lode is the app in front: a tab closing moves those after it along, out
            // from under the pointer. They close once the user has gone to another app.
            if (ActiveForm == null)
            {
                CloseUnusedTabs();
            }
        };
        unusedTabsTimer.Start();
        tabsTimer.Tick += (_, _) =>
        {
            tabsTimer.Stop();
            SaveTabs();
        };
        drivesTimer.Tick += (_, _) =>
        {
            drivesTimer.Stop();
            DrivesChanged();
        };

        SuspendLayout();
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Lode" + TitleSuffix;
        Icon = ShellIcons.FolderWindowIcon();
        ClientSize = new(1200, 760);
        MinimumSize = new(520, 320);

        backButton = CreateButton(backGlyph, "Back (Alt+Left)", () => ActiveView?.GoBack());
        forwardButton = CreateButton(forwardGlyph, "Forward (Alt+Right)", () => ActiveView?.GoForward());

        // Right-clicked, each lists where it would go, to go several steps at once.
        backButton.MouseUp += (_, args) => ShowHistory(backButton, args, _ => _.BackHistory);
        forwardButton.MouseUp += (_, args) => ShowHistory(forwardButton, args, _ => _.ForwardHistory);
        upButton = CreateButton(upGlyph, "Up (Alt+Up)", () => ActiveView?.GoUp());

        Controls.Add(BuildContent());

        // Straight after the content, so it docks after the bars, the find strip and the status
        // bar have taken their edges: down the right between the command bar and the find strip.
        Controls.Add(GitPanes.Strip);
        Controls.Add(ActionsBar.Strip);
        Controls.Add(BuildNavigationBar());

        // Before the status bar, so it docks above it.
        Controls.Add(FindHost.Panel);
        Controls.Add(BuildStatusBar());
        ResumeLayout(false);
        PerformLayout();
    }

    Control BuildNavigationBar()
    {
        var refreshButton = CreateButton(refreshGlyph, "Refresh (F5)", Reload);
        var newTabButton = CreateButton(addGlyph, "New tab (Ctrl+T)", NewTab);
        var menuButton = CreateButton(moreGlyph, "Menu", () => { });
        menuButton.Click += (_, _) => ShowWindowMenu(menuButton);

        addressBox.Name = "address";
        addressBox.AccessibleName = "Address";
        breadcrumb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        breadcrumb.Margin = new(6, 0, 4, 0);
        breadcrumb.PathClicked += _ =>
        {
            ActiveView?.Navigate(_);
            ActiveView?.FocusList();
        };
        addressBox.AutoCompleteMode = AutoCompleteMode.Suggest;
        addressBox.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        addressBox.KeyDown += OnAddressKeyDown;
        addressBox.Enter += (_, _) => BeginInvoke(addressBox.SelectAll);

        filterBox.Name = "filter";
        filterBox.AccessibleName = "Search";
        filterFrame.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        filterFrame.Margin = new(4, 0, 4, 0);
        filterBox.PlaceholderText = "Search this folder (Ctrl+F)";
        filterFrame.Glyph = Icons.Search;
        filterBox.TextChanged += OnFilterTextChanged;
        filterBox.KeyDown += OnFilterKeyDown;

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new(4, 3, 4, 3),
            // Explorer's address row, a shade off white, with the command bar white below it.
            BackColor = ExplorerStyle.AddressRow
        };
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new(SizeType.Absolute, 240));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.Controls.Add(backButton, 0, 0);
        bar.Controls.Add(forwardButton, 1, 0);
        bar.Controls.Add(upButton, 2, 0);
        bar.Controls.Add(refreshButton, 3, 0);
        bar.Controls.Add(breadcrumb, 4, 0);
        bar.Controls.Add(filterFrame, 5, 0);
        bar.Controls.Add(newTabButton, 6, 0);
        bar.Controls.Add(menuButton, 7, 0);
        return bar;
    }

    // Its text is a glyph of the icon font, which a screen reader can't say, so it is named by
    // its tooltip, less the keys, which describe it.
    // How many places Back's and Forward's lists show, the nearest: as many as are easily read.
    const int mostHistory = 15;

    void ShowHistory(Button button, MouseEventArgs args, Func<FolderView, IReadOnlyList<(string Path, int Position)>> history)
    {
        if (args.Button != MouseButtons.Right ||
            ActiveView is not { } view ||
            HistoryCommands(view, history(view)) is not { Count: > 0 } commands)
        {
            return;
        }

        var menu = new ExplorerMenu();
        foreach (var (text, go) in commands)
        {
            menu.Items.Add(text, null, (_, _) => go());
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(button, new(0, button.Height));
    }

    /// <summary>
    /// What Back's or Forward's list offers: each place by its name, the nearest first, and
    /// going straight to it.
    /// </summary>
    internal static List<(string Text, Action Go)> HistoryCommands(FolderView view, IReadOnlyList<(string Path, int Position)> history) =>
        history
            .Take(mostHistory)
            .Select(_ => (Locations.DisplayName(_.Path), (Action)(() => view.GoToHistory(_.Position))))
            .ToList();

    Button CreateButton(string glyph, string tip, Action action)
    {
        var name = tip;
        var keys = tip.IndexOf(" (", StringComparison.Ordinal);
        if (keys > 0)
        {
            name = tip[..keys];
        }

        var button = new GlyphButton
        {
            Text = glyph,
            AccessibleName = name,
            AccessibleDescription = tip,
            Size = new(36, 30),
            Margin = new(1),
            TabStop = false
        };
        button.Click += (_, _) => action();
        toolTip.SetToolTip(button, tip);
        return button;
    }

    // The ⋯ menu at the right of the address bar: Lode's own commands, apart from the actions,
    // which are buttons or under See more.
    void ShowWindowMenu(Control button)
    {
        Action? chosen = null;
        var strip = QuickMenu.Build(WindowCommands(), null, _ => chosen = _);
        // Run the choice once the menu has closed, so a dialog it opens gets the focus.
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                chosen?.Invoke();
            });
        strip.Show(button, new(0, button.Height));
    }

    /// <summary>
    /// What the ⋯ menu at the right of the address bar lists: Lode's own commands. A null entry
    /// is a separator.
    /// </summary>
    internal List<QuickMenu.Command?> WindowCommands()
    {
        var commands = new List<QuickMenu.Command?>
        {
            new("New tab", NewTab)
            {
                Glyph = Icons.NewTab,
                Shortcut = "Ctrl+T"
            },
            new("New window", NewWindow)
            {
                Glyph = Icons.NewWindow,
                Shortcut = "Ctrl+N"
            },
            new("Reopen closed tab", ReopenClosedTab, CanReopenClosedTab)
            {
                Glyph = Icons.Reopen,
                Shortcut = "Ctrl+Shift+T"
            },
            new("Command palette", ShowCommandPalette)
            {
                Glyph = Icons.CommandPalette,
                Shortcut = "Ctrl+Shift+P"
            },
            new("Find in files", FindHost.Open)
            {
                Glyph = Icons.Search,
                Shortcut = "Ctrl+Shift+F"
            },
            new("Clone a repository…", ShowClone)
            {
                Glyph = Icons.Clone,
                Shortcut = "Ctrl+Shift+O"
            },
            null,
            new("Show hidden items", ToggleHidden)
            {
                Glyph = Icons.ShowHidden,
                Shortcut = "Ctrl+H",
                Checked = settings.ShowHidden
            },
            new("Show system files", ToggleSystem)
            {
                Glyph = Icons.ShowSystem,
                Checked = settings.ShowSystem
            }
        };
        if (GitPanes.Repo != null)
        {
            var changesPinned = GitPanes.IsPinned(GitPane.Changes);
            var historyPinned = GitPanes.IsPinned(GitPane.History);
            commands.AddRange(
            [
                null,
                new("Changes", () => GitPanes.Toggle(GitPane.Changes))
                {
                    Glyph = Icons.Changes,
                    Shortcut = "Ctrl+Shift+G"
                },
                new("History", () => GitPanes.Toggle(GitPane.History))
                {
                    Glyph = Icons.History,
                    Shortcut = "Ctrl+Shift+H"
                },
                new("Pin changes", () => GitPanes.SetPinned(GitPane.Changes, !changesPinned))
                {
                    Checked = changesPinned
                },
                new("Pin history", () => GitPanes.SetPinned(GitPane.History, !historyPinned))
                {
                    Checked = historyPinned
                }
            ]);
        }

        if (PinnablePath() is { } path)
        {
            var pinned = IsPinned(path);
            commands.Add(
                new(PinMenuText(path), TogglePinned)
                {
                    Glyph = pinned ? Icons.Unpin : Icons.Pin
                });
        }

        commands.AddRange(
        [
            null,
            new("Options…", ShowOptions)
            {
                Glyph = Icons.Options
            },
            new("About Lode", ShowAbout)
            {
                Glyph = Icons.About
            },
            null,
            new("Exit Lode", () => ExitRequested?.Invoke())
            {
                Glyph = Icons.Exit
            }
        ]);
        return commands;
    }

    /// <summary>
    /// New on the tree's right-click menu: the folder opens in the tab, and once it has been
    /// read, New does there what it does in the list, making the item with its name ready to
    /// edit. Nothing is made if the tab ends up somewhere else, as when the folder has gone.
    /// </summary>
    internal async Task CreateFromTree(string folder, ShellNew.Entry? type)
    {
        if (ActiveView is not { } view)
        {
            return;
        }

        if (!Locations.Same(view.CurrentPath, folder))
        {
            view.Navigate(folder);
            await view.Loading;
        }

        if (view.IsDisposed ||
            !Locations.Same(view.CurrentPath, folder))
        {
            return;
        }

        if (type == null)
        {
            view.CreateFolder();
            return;
        }

        view.CreateFile(type);
    }

    Control BuildContent()
    {
        Tree.Dock = DockStyle.Fill;
        Tree.PathSelected += _ => ActiveView?.Navigate(_);
        Tree.NewTabRequested += _ => AddTab(_, false);
        Tree.NewWindowRequested += NewWindow;
        Tree.NewRequested += (folder, type) => _ = CreateFromTree(folder, type);

        Tabs.Dock = DockStyle.Fill;
        Tabs.CloseRequested += CloseTab;
        Tabs.TabDraggedOff += OnTabDraggedOff;
        Tabs.TabDraggingOff += OnTabDraggingOff;
        Tabs.SelectedIndexChanged += (_, _) => DiffHost.TabChanged();
        Tabs.SelectedIndexChanged += (_, _) => SyncToActiveView();
        Tabs.SelectedIndexChanged += (_, _) => SaveTabsSoon();
        Tabs.ControlAdded += (_, _) => SaveTabsSoon();
        Tabs.ControlRemoved += (_, _) => SaveTabsSoon();
        Tabs.MouseUp += OnTabsMouseUp;

        split.Dock = DockStyle.Fill;
        split.FixedPanel = FixedPanel.Panel1;
        // Added after the tree so it is docked first, and the tree fills what is left above it.
        split.Panel1.Controls.Add(Tree);
        split.Panel1.Controls.Add(actionsPanel);
        GitPanes.Attach(split.Panel2, Tabs);
        return split;
    }

    Control BuildStatusBar()
    {
        // White, as Explorer's is, with its text further in from the edge, and no grip: Windows
        // 11 windows are resized from their edges.
        // In the window's font, as the folder tree and the list are, rather than the smaller one
        // a tool strip has otherwise.
        var status = new StatusStrip
        {
            Renderer = new ExplorerRenderer(),
            SizingGrip = false,
            Font = Font,
            Padding = new(LogicalToDeviceUnits(10), LogicalToDeviceUnits(2), LogicalToDeviceUnits(10), LogicalToDeviceUnits(2))
        };
        status.Items.Add(statusLabel);
        status.Items.Add(searchInsideLabel);
        status.Items.Add(statusRoom);
        status.Items.Add(updateLabel);
        status.Items.Add(gitButton);
        AddGitPart(
            status,
            GitState.PartKind.NoCommits,
            new ToolStripStatusLabel
            {
                Margin = new(0, 3, 0, 2)
            });
        AddGitPart(status, GitState.PartKind.Changes, GitPartButton("Commit or discard them", GitState.PartKind.Changes));
        AddGitPart(status, GitState.PartKind.Ahead, GitPartButton("Push them", GitState.PartKind.Ahead));
        AddGitPart(status, GitState.PartKind.Behind, GitPartButton("Pull them", GitState.PartKind.Behind));
        AddGitPart(status, GitState.PartKind.ForkBehind, GitPartButton("Sync the fork with upstream", GitState.PartKind.ForkBehind));
        status.Items.Add(siteLabel);
        status.Items.Add(spaceLabel);

        // On the bar now, so the chevron's room is scaled as the bar is. A branch before the
        // name, as GitHub Desktop has.
        gitButton.Padding = gitButton.Padding with
        {
            Right = gitButton.Padding.Right + gitButton.ChevronWidth
        };
        gitButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        gitButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        gitButton.Image = Icons.Bitmap(Icons.Branch, LogicalToDeviceUnits(16), SystemColors.ControlText, LogicalToDeviceUnits(4));
        gitButton.Click += (_, _) => ShowBranches();
        updateLabel.Click += (_, _) => ShowOptions("Updates");
        searchInsideLabel.Click += (_, _) => ActiveView?.SearchInsideTheFiles();
        siteLabel.Click += (_, _) =>
        {
            if (siteLabel.Tag is RemoteSite site)
            {
                OpenSite(site.Url);
            }
        };
        foreach (ToolStripItem item in status.Items)
        {
            item.MouseHover += (_, _) => ShowStatusTip(status, item);
            item.MouseLeave += (_, _) => statusTip.Hide(status);
            item.MouseDown += (_, _) => statusTip.Hide(status);
        }

        ShowUpdate();
        return status;
    }

    // Its text as of now: the site's changes with the folder.
    void ShowStatusTip(StatusStrip status, ToolStripItem item)
    {
        var text = item.ToolTipText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Windows draws tooltips in the status font, with a margin round the text.
        var font = SystemFonts.StatusFont ?? Font;
        var height = TextRenderer.MeasureText(text, font).Height + LogicalToDeviceUnits(12);
        statusTip.Show(text, status, StatusTipLocation(item.Bounds, height, LogicalToDeviceUnits(4)), statusTip.AutoPopDelay);
    }

    /// <summary>
    /// Where a tooltip <paramref name="height"/> high goes for a status bar item at
    /// <paramref name="item"/>, in the bar's coordinates: over the item's left, wholly above the
    /// bar with a <paramref name="gap"/>, so never under the pointer, which is on the bar.
    /// </summary>
    internal static Point StatusTipLocation(Rectangle item, int height, int gap) =>
        new(item.Left, -height - gap);

    void AddGitPart(StatusStrip status, GitState.PartKind kind, ToolStripItem item)
    {
        var dot = new ToolStripStatusLabel("·")
        {
            Margin = new(0, 3, 0, 2),
            Visible = false
        };
        item.Visible = false;
        status.Items.Add(dot);
        status.Items.Add(item);
        gitParts[kind] = (dot, item);
    }

    ToolStripButton GitPartButton(string toolTip, GitState.PartKind kind)
    {
        var button = new ToolStripButton
        {
            Margin = new(0, 1, 0, 0),
            AutoToolTip = false,
            ToolTipText = toolTip
        };
        button.Click += (_, _) => ActOnGit(kind);
        return button;
    }

    // The changes, ahead, behind or behind upstream clicked: asks what to do about them, pull
    // say.
    void ActOnGit(GitState.PartKind kind)
    {
        if (ActiveView is { GitRepo: { } repo, GitRepoState: { } state })
        {
            GitStatusActions.Act(this, kind, repo, state, settings.CodeDirectory);
        }
    }

    // A part for each of what the state says, the last spaced from what follows. Each is set
    // once, to what it is to be: hidden and shown again, the bar would be laid out again for
    // every status update, each change of selection say, closing a tooltip open over it.
    void ShowGitParts(GitState? state)
    {
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
                right = LogicalToDeviceUnits(12);
            }
            else if (item == gitButton)
            {
                right = LogicalToDeviceUnits(4);
            }

            item.Margin = item.Margin with
            {
                Right = right
            };
        }
    }

    /// <summary>
    /// The folders and files among a launch's <paramref name="arguments"/>, as full paths, and
    /// This PC.
    /// </summary>
    internal static List<string> Openable(IEnumerable<string> arguments) =>
        arguments
            .Select(_ => Locations.Normalize(_))
            .OfType<string>()
            .Where(_ => Locations.IsThisPC(_) ||
                        Locations.IsRemote(_) ||
                        Directory.Exists(_) ||
                        File.Exists(_) ||
                        Zip.IsArchivePath(_))
            .ToList();

    void OpenInitialTabs(string[] arguments)
    {
        var paths = Openable(arguments);
        var active = paths.Count - 1;

        // Restored, a tab that was inside a zip opens inside it again. A zip launched with is
        // shown selected, as any file is.
        var restoring = paths.Count == 0;

        // When each tab restored was last looked at, in the order of paths.
        var lastShown = new List<DateTimeOffset>();
        if (paths.Count == 0)
        {
            var saved = settings.TabsLastShown.Count == settings.Tabs.Count;
            var now = Time.GetUtcNow();

            // The tab that was active is as many along as there are tabs before it still to
            // open: one whose folder has gone since leaves those after it a place earlier.
            active = 0;
            for (var index = 0; index < settings.Tabs.Count; index++)
            {
                var path = settings.Tabs[index];
                if (!Locations.IsThisPC(path) &&
                    !Locations.IsRemote(path) &&
                    !Directory.Exists(path) &&
                    !Zip.IsArchivePath(path))
                {
                    continue;
                }

                if (index < settings.ActiveTab)
                {
                    active++;
                }

                paths.Add(path);

                // Not after now, which a clock put back would leave: the tab would stay until
                // the clock caught up.
                if (saved &&
                    settings.TabsLastShown[index] < now)
                {
                    lastShown.Add(settings.TabsLastShown[index]);
                }
                else
                {
                    lastShown.Add(now);
                }
            }
        }

        if (paths.Count == 0)
        {
            paths.Add(settings.StartLocation());
        }

        // Named once they are all open, rather than every tab again as each is added.
        openingTabs = true;
        try
        {
            foreach (var path in paths)
            {
                AddTab(path, false, intoZip: restoring);
            }
        }
        finally
        {
            openingTabs = false;
        }

        Tabs.SelectedIndex = Math.Clamp(active, 0, Tabs.TabCount - 1);
        SyncToActiveView();

        // Once the tab that was active is picked: before, the first tab, which showed while they
        // were added, would be counted as looked at until it was.
        foreach (var (view, shown) in Views.Zip(lastShown))
        {
            view.LastShown = shown;
        }
    }

    bool openingTabs;

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        if (restoreBounds)
        {
            var saved = new Rectangle(settings.Left, settings.Top, settings.Width, settings.Height);
            var onScreen = Screen.AllScreens.Any(_ => _.WorkingArea.IntersectsWith(saved));
            if (saved is { Width: > 0, Height: > 0 } && onScreen)
            {
                Bounds = saved;
            }

            if (settings.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }

        var distance = settings.SplitterDistance > 0 ? settings.SplitterDistance : LogicalToDeviceUnits(250);
        split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Width / 2));
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        ActiveView?.FocusList();
    }

    // Again whenever WinForms makes the window anew, which changing some properties does.
    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        TaskbarIdentity.Apply(Handle);

        // The title bar in Mica, the material Explorer's tab strip is drawn in, tinted by the
        // desktop behind the window. Windows 10 and early Windows 11 don't have it, and ignore
        // the setting.
        var backdrop = DwmSystemBackdropMainWindow;
        DwmSetWindowAttribute(Handle, DwmWindowSystemBackdropType, ref backdrop, sizeof(int));

        // Told of every change to the clipboard, so what is cut, here or in Explorer, shows dimmed.
        AddClipboardFormatListener(Handle);
        CutItems.Refresh();
    }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        RemoveClipboardFormatListener(Handle);
        TaskbarIdentity.Clear(Handle);
        base.OnHandleDestroyed(args);
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        base.OnFormClosing(args);
        // Not one disposed without being closed, which can stay listed.
        var others = Application.OpenForms
            .OfType<MainForm>()
            .Where(_ => _ != this &&
                        !_.IsDisposed)
            .ToList();
        // UserClosing is the X, Alt+F4 or the taskbar's Close; None is another program sending
        // WM_CLOSE. Windows shutting down, or Exit (which turns HideLastWindowOnClose off), closes
        // for real.
        var hiding = HideLastWindowOnClose &&
                     !others.Any(_ => _.Visible) &&
                     args.CloseReason is
                         CloseReason.UserClosing or
                         CloseReason.None;
        if (hiding)
        {
            // One window in the tray at most: the one closed last, which is also the one the
            // next start restores. Otherwise each window opened for a folder from another app,
            // while the others were in the tray, would join them there when closed, and all of
            // them would come back at once. Closed first, so this one's tabs are saved last.
            foreach (var inTray in others.Where(_ => _.hidden))
            {
                inTray.Close();
            }
        }

        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.Left = bounds.Left;
        settings.Top = bounds.Top;
        settings.Width = bounds.Width;
        settings.Height = bounds.Height;
        settings.Maximized = WasMaximized;
        settings.SplitterDistance = split.SplitterDistance;
        tabsTimer.Stop();
        RememberTabs();
        settings.Save();

        if (hiding)
        {
            args.Cancel = true;
            Hide();
        }
    }

    // Puts the window's tabs in the settings, for the next start to open. Not those of a window
    // with no folder left in it, its last tab dragged to another window, which closes for
    // that: none would be saved, in place of the tabs last saved, until the other closed.
    internal void RememberTabs()
    {
        if (!Views.Any())
        {
            return;
        }

        settings.Tabs = Views
            .Select(_ => _.CurrentPath)
            .ToList();
        // The tab showing is looked at until now.
        var now = Time.GetUtcNow();
        foreach (var view in Views.Where(_ => _.Showing))
        {
            view.LastShown = now;
        }

        settings.TabsLastShown = Views
            .Select(_ => _.LastShown)
            .ToList();
        // Among the folders' tabs, which are all that open again: with Find Results showing, the
        // one before it.
        settings.ActiveTab = Math.Max(0, TabPages.Take(Tabs.SelectedIndex + 1).Count(HoldsFolder) - 1);
    }

    // The window used last: the one whose tabs are saved while Lode runs, as it is the one
    // whose tabs are saved last when Lode exits.
    static MainForm? usedLast;

    Timer tabsTimer = new()
    {
        Interval = 5000
    };

    // The tabs are saved a little after they change, or the window comes to the front, not only
    // as it closes. Left until then, a Lode ended by a crash, the power going or Task Manager
    // came back with the tabs of whichever window closed last, days before perhaps. A little
    // after, as tabs change in runs: a start putting its tabs back, a folder gone down into a
    // level at a time.
    void SaveTabsSoon()
    {
        // Its tabs go as a window does, which is no change to save, and its timer has gone.
        if (Disposing ||
            IsDisposed)
        {
            return;
        }

        tabsTimer.Stop();
        tabsTimer.Start();
    }

    /// <summary>
    /// Saves the tabs, if this is the window used last, and has any.
    /// </summary>
    internal void SaveTabs()
    {
        if (Disposing ||
            IsDisposed ||
            usedLast != this ||
            !Views.Any())
        {
            return;
        }

        RememberTabs();
        settings.Save();
    }

    /// <param name="intoZip">For somewhere a tab was, inside a zip: the tab opens inside it,
    /// rather than showing the zip selected, as for a file.</param>
    public FolderView AddTab(string path, bool activate, bool intoZip = false)
    {
        var view = new FolderView(settings, places)
        {
            Dock = DockStyle.Fill,
            // Until it is the tab showing, which is when it reads its folder.
            Showing = false,
            // A tab opened in the background has the hours from now before it counts as unused.
            LastShown = Time.GetUtcNow()
        };
        var page = new TabPage
        {
            Padding = Padding.Empty
        };
        page.Controls.Add(view);

        // Handled by whichever window the tab is in at the time, since a tab can be dragged to
        // another window.
        view.PathChanged += () => WindowOf(view)?.OnViewPathChanged(view);
        view.ColumnsChanged += RefreshColumnsInAllWindows;
        view.ColumnWidthsChanged += RefreshColumnWidthsInAllWindows;
        view.StatusChanged += () => WindowOf(view)?.OnViewStatusChanged(view);
        view.NewTabRequested += _ => WindowOf(view)?.AddTab(_, false, intoZip: true);
        view.FindInFilesRequested += (find, regex) => WindowOf(view)?.FindHost.FindInFolder(view.CurrentPath, find, regex);
        Tabs.TabPages.Add(page);
        view.OpenAt(path, intoZip);

        if (activate)
        {
            Tabs.SelectedTab = page;
            // In a window not shown yet, the tab control has no handle, so selecting a tab only
            // notes which, and nothing says it changed: the title, address bar, buttons and
            // tree are brought in line here instead.
            if (!Tabs.IsHandleCreated)
            {
                SyncToActiveView();
            }

            view.FocusList();
        }

        UpdateShowing();
        return view;
    }

    /// <summary>
    /// Selects the tab showing <paramref name="path"/>, or the folder it is in when it is a file,
    /// with the file selected: the selected tab when that is one, otherwise the first. False
    /// when no tab shows it.
    /// </summary>
    internal bool SelectTabShowing(string path)
    {
        var file = !Locations.IsRemote(path) &&
                   File.Exists(path);
        var folder = path;
        if (file)
        {
            folder = Path.GetDirectoryName(path) ?? path;
        }

        var active = ActiveView;
        var view = Views
            .OrderByDescending(_ => _ == active)
            .FirstOrDefault(_ => Locations.Same(_.CurrentPath, folder));
        if (view == null)
        {
            return false;
        }

        // Before the tab shows, so a tab in the background reads its folder once.
        if (file)
        {
            view.Reveal(path);
        }

        Tabs.SelectedTab = (TabPage)view.Parent!;
        view.FocusList();
        return true;
    }

    /// <summary>
    /// Brings the window to the front, from the tray or the taskbar too, as it was before it
    /// went there: maximized if it was.
    /// </summary>
    internal void ShowInFront()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            ShowWindow(Handle, ShowWindowRestore);
        }

        Activate();
    }

    void NewTab() =>
        AddTab(settings.StartLocation(), true);

    internal void CloseTab(TabPage page)
    {
        if (Tabs.TabCount == 1)
        {
            Close();
            return;
        }

        RememberClosed(page);
        DiffHost.Leaving(page);
        LeaveTab(page);
        Tabs.TabPages.Remove(page);
        FindHost.Closing(page);
        page.Dispose();

        // A folder that had the same name as this one's may go back to its name alone.
        Retitle();
    }

    // The selected tab going, the one after it is selected first, or the one before for the
    // last: removed while selected, Windows Forms would select the first tab, which would show,
    // read its folder and have the tree walk to it, on the way to the one wanted.
    void LeaveTab(TabPage page)
    {
        if (Tabs.SelectedTab != page)
        {
            return;
        }

        var index = Tabs.TabPages.IndexOf(page);
        if (index + 1 < Tabs.TabCount)
        {
            Tabs.SelectedIndex = index + 1;
            return;
        }

        if (index > 0)
        {
            Tabs.SelectedIndex = index - 1;
        }
    }

    void RememberClosed(TabPage page)
    {
        if (page.Controls.OfType<FolderView>().FirstOrDefault() is not { } view)
        {
            return;
        }

        closedTabs.Add(view.CurrentPath);
        if (closedTabs.Count > maximumClosedTabs)
        {
            closedTabs.RemoveAt(0);
        }
    }

    internal bool CanReopenClosedTab => closedTabs.Count > 0;

    /// <summary>
    /// Opens the most recently closed tab again, at the folder it was showing, or, where that
    /// has gone since, the nearest folder above it still there, as a tab whose folder is
    /// deleted goes to. Somewhere on the network is taken on trust, as asking after it could
    /// keep the window waiting.
    /// </summary>
    internal void ReopenClosedTab()
    {
        if (closedTabs.Count == 0)
        {
            return;
        }

        var path = closedTabs[^1];
        closedTabs.RemoveAt(closedTabs.Count - 1);
        if (!Locations.IsRemote(path) &&
            FolderView.NearestFolder(path) is { } nearest)
        {
            path = nearest;
        }

        AddTab(path, true, intoZip: true);
    }

    static MainForm? WindowOf(Control? control) =>
        control?.FindForm() as MainForm;

    // The tree a tab being dragged off its row is over, in any window, showing where it would be
    // pinned.
    FolderTree? tabOverTree;

    void OnTabDraggingOff(Point? screenLocation)
    {
        FolderTree? tree = null;
        if (screenLocation is { } location)
        {
            tree = WindowAt(location) as FolderTree;
        }

        if (tabOverTree != tree &&
            tabOverTree is { IsDisposed: false })
        {
            tabOverTree.ShowPinTarget(null);
        }

        tabOverTree = tree;
        tree?.ShowPinTarget(screenLocation);
    }

    void OnTabDraggedOff(TabPage page, Point screenLocation)
    {
        // Find Results stays with the window whose strip it is of.
        if (page.Controls.OfType<FolderView>().FirstOrDefault() is not { } view)
        {
            return;
        }

        // A tab dropped in the Pinned list pins its folder there, and stays where it was.
        if (WindowAt(screenLocation) is FolderTree tree &&
            tree.PinIfOverPinned(screenLocation, view.CurrentPath))
        {
            return;
        }

        // Between two of another window's tabs, it goes in there.
        var target = WindowOf(WindowAt(screenLocation));
        if (target != null &&
            target != this)
        {
            MoveTab(page, target, target.Tabs.DropIndex(screenLocation));
            target.Activate();
            return;
        }

        // Dropped away from the tabs, in this window or off every window, it gets a window of
        // its own. Within this window counts too, as a maximized window leaves nowhere else to
        // drop it. A lone tab would only swap one window for another.
        if (Tabs.TabCount > 1)
        {
            MoveTabToNewWindow(page, screenLocation);
        }
    }

    // The top-most of this app's controls at a point on the screen, if any.
    static Control? WindowAt(Point screenLocation) =>
        FromChildHandle(WindowFromPoint(new(screenLocation.X, screenLocation.Y)));

    /// <summary>
    /// Moves a tab, with its history, to another window's tabs, at <paramref name="index"/>
    /// among them or else at the end, and shows it there. This window closes if that was its
    /// last tab.
    /// </summary>
    public void MoveTab(TabPage page, MainForm target, int? index = null)
    {
        RemoveTab(page);
        target.AdoptTab(page, index ?? target.Tabs.TabCount);
    }

    /// <summary>
    /// Moves a tab into a window of its own, placed so the tab lands near the point.
    /// </summary>
    public MainForm MoveTabToNewWindow(TabPage page, Point screenLocation)
    {
        // Puts the new window's tab under the pointer, as if it had been carried there.
        var firstTab = Tabs.GetTabRect(0);
        var firstTabOnScreen = Tabs.PointToScreen(firstTab.Location);
        var grab = new Size(
            firstTabOnScreen.X - Left + firstTab.Width / 2,
            firstTabOnScreen.Y - Top + firstTab.Height / 2);
        var window = CreateWindow(new(screenLocation - grab, NormalSize));
        MoveTab(page, window);
        window.Show();
        return window;
    }

    // A window opened from this one, with no tabs yet. The caller adds them, then shows it.
    MainForm CreateWindow(Rectangle bounds)
    {
        var window = new MainForm(settings, places)
        {
            restoreBounds = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds
        };
        WindowOpened?.Invoke(window);
        return window;
    }

    void AdoptTab(TabPage page, int index)
    {
        if (index < Tabs.TabCount)
        {
            Tabs.TabPages.Insert(index, page);
        }
        else
        {
            Tabs.TabPages.Add(page);
        }

        Tabs.SelectedTab = page;
        var view = page.Controls.OfType<FolderView>().First();
        OnViewPathChanged(view);
        SyncToActiveView();
        view.FocusList();
    }

    // Takes a tab out of this window without disposing it, so it can go to another. Closes the
    // window when it was the last.
    void RemoveTab(TabPage page)
    {
        DiffHost.Leaving(page);
        LeaveTab(page);
        Tabs.TabPages.Remove(page);
        if (Tabs.TabCount == 0)
        {
            Close();
            return;
        }

        Retitle();
    }

    void OnViewStatusChanged(FolderView view)
    {
        if (view == ActiveView)
        {
            UpdateStatus(view);
        }
    }

    void OnViewPathChanged(FolderView view)
    {
        if (view.Parent is not TabPage)
        {
            return;
        }

        DiffHost.FolderChanged(view);
        SaveTabsSoon();

        if (view == ActiveView)
        {
            SyncToActiveView();
        }
        else
        {
            Retitle();
        }

        ShellWindows.Sync(this);
    }

    /// <summary>
    /// For an app that asked the shell to show items selected, a file by GitHub Desktop's Show in
    /// Explorer say, and found <paramref name="view"/> showing their folder (see
    /// <see cref="ShellWindows"/>): selects them, picks the tab, and brings its window to the
    /// front, as another launch handing over a file does. The tab showing in that window is
    /// used instead when it shows the same folder, as it would be for that launch.
    /// </summary>
    internal static void ShowItems(FolderView view, IReadOnlyList<string> paths)
    {
        if (view.IsDisposed ||
            WindowOf(view) is not { } window)
        {
            return;
        }

        if (window.ActiveView is { } active &&
            Locations.Same(active.CurrentPath, view.CurrentPath))
        {
            view = active;
        }

        // Before the tab shows, so a tab in the background reads its folder once.
        view.Reveal(paths);
        window.Tabs.SelectedTab = (TabPage)view.Parent!;
        view.FocusList();
        window.ShowInFront();
    }

    /// <summary>
    /// Names the tabs, and titles the window: a folder is called by its name, unless another tab
    /// in this window has a folder with the same name, when both take in as much of their paths
    /// as tells them apart, "Lode\src" and "Other\src". Tabs in other windows don't count: each
    /// window is seen on its own.
    /// </summary>
    void Retitle()
    {
        if (openingTabs)
        {
            return;
        }

        var labels = Locations.Labels(Views.Select(_ => _.CurrentPath));
        foreach (var view in Views)
        {
            var page = (TabPage)view.Parent!;
            var label = labels[view.CurrentPath];
            var text = label.Length > maximumTabTextLength ? string.Concat(label.AsSpan(0, maximumTabTextLength), "…") : label;
            // Setting a tab's text, even to what it was, has the tab control update the tab.
            if (page.Text != text)
            {
                page.Text = text;
            }

            page.ToolTipText = view.CurrentPath;
            Tabs.SetIcon(page, TabIcon(view.CurrentPath));
        }

        if (ActiveView is { } active)
        {
            Text = labels[active.CurrentPath] + TitleSuffix;
        }
    }

    // The folder's own icon, as the tree shows it: a drive's, Downloads' arrow, This PC's. A
    // machine on the network and a share have Network's, and a folder in a share the plain one,
    // since asking for its own could keep the window waiting on the network.
    static int TabIcon(string path)
    {
        if (Locations.IsThisPC(path))
        {
            return ShellIcons.ComputerIcon;
        }

        if (Locations.IsServer(path) ||
            Locations.IsShare(path))
        {
            return ShellIcons.NetworkIcon;
        }

        return ShellIcons.GetPlaceIcon(path);
    }

    // Brings the address bar, filter box, buttons, tree and status bar in line with whichever
    // tab is showing.
    /// <summary>
    /// Raised each time the window brings itself in line with the tab showing, with that tab's
    /// view. For tests.
    /// </summary>
    internal event Action<FolderView>? Synced;

    void SyncToActiveView()
    {
        if (ActiveView is { } synced)
        {
            Synced?.Invoke(synced);
        }

        // First, so the tab now showing has started reading its folder by the time its status
        // is asked for.
        UpdateShowing();
        var view = ActiveView;
        ShowingFolder(view != null);
        if (view == null)
        {
            // The Diff tab keeps the panes on the repository its diff is of, so the files listed
            // stay beside it, as in GitHub Desktop.
            if (DiffHost is { TabShowing: true, TabRepo: { } diffRepo })
            {
                GitState? state = null;
                if (ChangesPane.SameRepo(GitPanes.Repo, diffRepo))
                {
                    state = GitPanes.State;
                }

                GitPanes.ShowRepo(diffRepo, state);
                Text = "Diff" + TitleSuffix;
                return;
            }

            GitPanes.ShowRepo(null, null);
            if (FindHost.ResultsShowing)
            {
                Text = "Find Results" + TitleSuffix;
                UpdateResultsStatus();
            }

            return;
        }

        breadcrumb.Path = view.CurrentPath;
        updatingFilter = true;
        filterBox.Text = view.Filter;
        updatingFilter = false;
        Retitle();
        backButton.Enabled = view.CanGoBack;
        forwardButton.Enabled = view.CanGoForward;
        upButton.Enabled = view.CanGoUp;
        Tree.SelectPath(view.CurrentPath);
        UpdateStatus(view);
    }

    // Only ever the active view. Its selection is read from the list once, for the buttons and
    // the status bar both: this follows every change of selection.
    void UpdateStatus(FolderView view)
    {
        var selection = view.SelectedEntries();
        ActionsBar.Update(selection);
        statusLabel.Text = view.StatusFor(selection);
        var searchesInside = view.CanSearchInsideTheFiles;
        if (searchInsideLabel.Visible != searchesInside)
        {
            searchInsideLabel.Visible = searchesInside;
        }

        if (searchesInside)
        {
            searchInsideLabel.ToolTipText = $"Search for \"{view.Filter}\" in what the files hold, with Find in Files (Ctrl+Enter)";
        }

        ShowGitParts(view.GitRepoState);
        var site = view.GitSite;
        siteLabel.Tag = site;
        siteLabel.Visible = site != null;
        if (site != null)
        {
            siteLabel.Text = site.Name;
            siteLabel.ToolTipText = $"Open {site.Url}";
        }

        spaceLabel.Text = view.FreeSpaceText;
        ShowPanes(view);
    }

    // The Changes and History panes follow the repository the tab showing is in. While the tab
    // reads its folder, the repository it was in stays: going between folders of one, or to a
    // tab not read yet, doesn't take the panes away to bring them back a moment later.
    void ShowPanes(FolderView view)
    {
        if (!view.Loading.IsCompleted)
        {
            return;
        }

        var repo = view.Repo;
        GitState? state = null;
        if (repo != null &&
            Locations.Same(view.GitRepo, repo))
        {
            state = view.GitRepoState;
        }

        GitPanes.ShowRepo(repo, state);
    }

    /// <summary>
    /// Asks what to clone, and where to, then clones it in the Actions panel, opening a tab on
    /// the clone once it is there. Tests answer for the user.
    /// </summary>
    internal Func<CloneDialog, DialogResult> ShowCloneDialog = dialog => dialog.ShowDialog(dialog.Owner);

    void ShowClone()
    {
        // Into the code folder, or, without one, the folder open.
        var parent = settings.CodeDirectory;
        if (parent == null ||
            !Directory.Exists(parent))
        {
            parent = PinnablePath() ?? Locations.Home;
        }

        using var dialog = new CloneDialog(settings, parent)
        {
            Owner = this
        };
        if (ShowCloneDialog(dialog) != DialogResult.OK)
        {
            return;
        }

        Clone(dialog.Remote, dialog.Destination);
    }

    async void Clone(string remote, string destination)
    {
        var entry = await ActionLog.Run(
            "Git clone",
            [destination],
            async (progress, cancel) =>
            {
                var result = await Task.Run(() => RepositoryClone.Clone(remote, destination, null, progress.Report, cancel), cancel);
                return CodeCommands.GitOutcome([result]);
            });
        if (entry.State == ActionLog.State.Succeeded &&
            !IsDisposed)
        {
            AddTab(destination, true);
        }
    }

    // Pinned or unpinned in one window, for a repository, in every window showing it.
    static void ApplyPinnedInAllWindows(string repo)
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            window.GitPanes.ApplyPinned(repo);
        }
    }

    // The strip's tabs labelled another way in one window, in every window.
    static void ApplyStripLabelsInAllWindows()
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            window.GitPanes.ApplyStripLabels();
        }
    }

    /// <summary>
    /// Opens the repository's page on its site, in the browser. Tests swap it.
    /// </summary>
    internal Action<string> OpenSite;

    // When the branches last closed, as Environment.TickCount64. Clicking the branch while they
    // are open closes them, as the click goes to the window first, and then the click itself
    // would open them again.
    long branchesClosedAt = long.MinValue / 2;

    // Ctrl+B, or the branch clicked: the branches of the repository the folder is in, above the
    // branch, found as they open. Outside a repository, nothing.
    void ShowBranches()
    {
        if (!gitButton.Visible ||
            gitButton.Owner == null ||
            ActiveView is not { GitRepo: { } repo } view ||
            Environment.TickCount64 - branchesClosedAt < 500)
        {
            return;
        }

        var state = view.GitRepoState;
        var picker = BranchPicker.Show(
            this,
            gitButton.Owner.RectangleToScreen(gitButton.Bounds),
            Task.Run(() => GitBranches.Read(repo, Cancel.None)),
            branch => CodeMenu.SwitchBranch(this, repo, state, branch),
            name => CodeMenu.NewBranch(this, repo, name),
            (listing, branch) => CodeMenu.BranchCommands(this, repo, listing, branch, folder => AddTab(folder, true)));
        picker.FormClosed += (_, _) => branchesClosedAt = Environment.TickCount64;
    }

    // A git action that started or finished.
    void ReadGitState()
    {
        foreach (var view in Views)
        {
            view.ReadGitState();
        }
    }

    // Back at the front, where a commit made in a terminal meanwhile went unseen: git changes
    // only .git, not the folder listed.
    protected override void OnActivated(EventArgs args)
    {
        base.OnActivated(args);
        usedLast = this;
        SaveTabsSoon();
        foreach (var view in Views)
        {
            view.ReadGitStateUnlessFresh();
        }
    }

    // Only the tab showing, in a window that is, reads its folder and follows the changes to
    // it. The rest read theirs when they are shown: a tab in the background, when it is picked;
    // a window in the tray or minimized, when it comes back.
    void UpdateShowing()
    {
        // A window on its way out has nothing left to show, and its tabs may already be gone.
        if (Disposing || IsDisposed)
        {
            return;
        }

        var windowShowing = !hidden &&
                            !minimized;
        var active = ActiveView;
        var now = Time.GetUtcNow();
        foreach (var view in Views)
        {
            var showing = windowShowing &&
                          view == active;

            // A tab is looked at while it shows, so one that stops showing was last looked at
            // now. One that starts is noted too, though while it shows it is never unused.
            if (showing || view.Showing)
            {
                view.LastShown = now;
            }

            view.Showing = showing;
        }

        Tree.Showing = windowShowing;
    }

    // Shown and then hidden, to the tray, rather than not shown yet: a window being built reads
    // its first folder before it shows.
    bool hidden;
    bool minimized;

    protected override void OnVisibleChanged(EventArgs args)
    {
        base.OnVisibleChanged(args);
        hidden = !Visible;
        UpdateShowing();
        ShellWindows.Sync(this);

        // Once it has been in the tray a little while, not the moment it goes: closed and
        // brought straight back, it has let go of nothing.
        if (hidden)
        {
            trayTimer.Start();
        }
        else
        {
            trayTimer.Stop();
        }
    }

    Timer trayTimer = new()
    {
        Interval = 5000
    };

    /// <summary>
    /// For a window gone to the tray, where Lode can sit for days. The tabs nobody was looking
    /// at let go of their folders, which are read again when one is picked. The tab that was
    /// showing keeps its rows, so the window comes back just as it was left. Then the memory
    /// that frees, with whatever else has built up, is handed back to Windows.
    /// </summary>
    internal void ReleaseMemory()
    {
        var active = ActiveView;
        foreach (var view in Views)
        {
            if (view != active)
            {
                view.Unload();
            }
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // Hours are what Options counts in, so every five minutes is close enough.
    Timer unusedTabsTimer = new()
    {
        Interval = 300_000
    };

    /// <summary>
    /// With closing unused tabs on, in Options, closes the tabs nobody has looked at for the hours
    /// it gives: those that have not, for that long, been the tab showing in a window that is
    /// itself showing, rather than minimized or in the tray. Never the tab the window shows, or
    /// shows again when it comes back, so never the last one. The tab looked at longest ago
    /// closes first, so Reopen closed tab brings back the most recent first.
    /// </summary>
    internal void CloseUnusedTabs()
    {
        if (!settings.CloseUnusedTabs)
        {
            return;
        }

        // At least an hour, as Options allows, whatever the settings file says: none would close
        // every tab the moment it stopped showing.
        var limit = TimeSpan.FromHours(Math.Max(1, settings.UnusedTabHours));
        var now = Time.GetUtcNow();
        var active = ActiveView;
        CloseTabs(
            Views
                .Where(_ => _ != active &&
                            now - _.LastShown >= limit)
                .OrderBy(_ => _.LastShown)
                .Select(_ => (TabPage)_.Parent!));
    }

    // Whether it was maximized when last not minimized, which is how it comes back.
    bool maximizedBeforeMinimizing;

    /// <summary>
    /// Whether the window is maximized, or was before it was minimized, as it is to come back
    /// the next time Lode starts.
    /// </summary>
    internal bool WasMaximized
    {
        get
        {
            if (WindowState == FormWindowState.Minimized)
            {
                return maximizedBeforeMinimizing;
            }

            return WindowState == FormWindowState.Maximized;
        }
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        var nowMinimized = WindowState == FormWindowState.Minimized;
        if (!nowMinimized)
        {
            maximizedBeforeMinimizing = WindowState == FormWindowState.Maximized;
        }

        if (nowMinimized == minimized)
        {
            return;
        }

        minimized = nowMinimized;
        UpdateShowing();
    }

    // Refresh also asks again what it otherwise remembers about files: their own icons, and
    // which types are text.
    void Reload()
    {
        ShellIcons.ForgetFileIcons();
        ShellIcons.ForgetPlaceIcons();
        OpenWith.Forget();
        ShellIcons.ForgetDrives();
        TextEditors.ForgetTypes();
        Tree.RefreshDrives();
        GitPanes.Reload();
        if (FindHost.ResultsShowing)
        {
            FindHost.ResultsView?.SearchAgain();
            return;
        }

        ActiveView?.ReloadRows();
    }

    void ToggleHidden()
    {
        settings.ShowHidden = !settings.ShowHidden;
        ApplySettingsToAllWindows(SettingsChange.Listing);
    }

    void ToggleSystem()
    {
        settings.ShowSystem = !settings.ShowSystem;
        ApplySettingsToAllWindows(SettingsChange.Listing);
    }

    void OpenTerminal() =>
        OpenTerminal(admin: false);

    void OpenAdminTerminal() =>
        OpenTerminal(admin: true);

    void OpenTerminal(bool admin)
    {
        if (ActiveView is {IsReadOnly: false} view)
        {
            FileOperations.OpenTerminal(view.CurrentPath, admin);
        }
    }

    void OpenInClaude()
    {
        if (PinnablePath() is { } path)
        {
            ClaudeDesktop.Open(this, path);
        }
    }

    void OpenAgent(CodingAgents.Agent agent)
    {
        if (PinnablePath() is { } path)
        {
            CodingAgents.Open(agent, [path]);
        }
    }

    string? PinnablePath()
    {
        if (ActiveView is not { } view ||
            Locations.IsThisPC(view.CurrentPath) ||
            view.InArchive)
        {
            return null;
        }

        return view.CurrentPath;
    }

    /// <summary>
    /// What the main menu's pin item says for the active folder, or null when it is hidden.
    /// </summary>
    internal string? PinMenuText()
    {
        if (PinnablePath() is not { } path)
        {
            return null;
        }

        return PinMenuText(path);
    }

    // "This folder": the one the tab shows, whatever is selected. Pin to Quick access, the
    // action, is for the selection.
    string PinMenuText(string path)
    {
        if (IsPinned(path))
        {
            return "Unpin this folder from Quick access";
        }

        return "Pin this folder to Quick access";
    }

    bool IsPinned(string path) =>
        places.Pinned.Any(_ => Locations.Same(_, path));

    void TogglePinned()
    {
        if (PinnablePath() is { } path)
        {
            ShellContextMenu.SetPinned(Handle, path, !IsPinned(path));
        }
    }

    void NewWindow() =>
        NewWindow(ActiveView?.CurrentPath ?? settings.StartLocation());

    // In this process rather than a new one, so tabs can be dragged between the two.
    internal void NewWindow(string path)
    {
        var offset = new Size(LogicalToDeviceUnits(30), LogicalToDeviceUnits(30));
        var window = CreateWindow(new(Location + offset, NormalSize));
        window.AddTab(path, true, intoZip: true);
        window.Show();
    }

    Size NormalSize
    {
        get
        {
            if (WindowState == FormWindowState.Normal)
            {
                return Size;
            }

            return RestoreBounds.Size;
        }
    }

    void ShowOptions() =>
        ShowOptions(null);

    void ShowOptions(string? page)
    {
        using var options = new OptionsForm(ExplorerReplacement.ForCurrentUser(), settings, TextEditors.Find(), updates);
        options.SettingsChanged += ApplySettingsToAllWindows;
        if (page != null)
        {
            options.ShowPage(page);
        }

        options.ShowDialog(this);

        // The update waits for Lode to exit, so it goes as Exit Lode does, once Options has.
        if (options.UpdateStarted)
        {
            ExitRequested?.Invoke();
        }
    }

    void OnUpdatesChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(ShowUpdate);
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

    // A column hidden or shown in one view goes for every tab of every window.
    static void RefreshColumnsInAllWindows()
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            foreach (var view in window.Views)
            {
                view.RefreshColumns();
            }
        }
    }

    // A column sized in one view is as wide in every other tab of every window. Not the one it
    // was sized in, which has it to the pixel, where the width kept at 100% scale may round to
    // one off it.
    static void RefreshColumnWidthsInAllWindows(FolderView sized)
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            foreach (var view in window.Views)
            {
                if (view != sized)
                {
                    view.RefreshColumnWidths();
                }
            }
        }
    }

    // Settings are shared by every window, so a change shows in all of them.
    static void ApplySettingsToAllWindows(SettingsChange change)
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            window.ApplySettings(change);
        }
    }

    // Only what the change changes is done again: a tick on the Actions page reads no folder
    // and leaves the tree as it is.
    internal void ApplySettings(SettingsChange change)
    {
        switch (change)
        {
            case SettingsChange.Actions:
                ActionsBar.Rebuild();
                return;
            case SettingsChange.Listing:
                foreach (var view in Views)
                {
                    view.Reload();
                }

                RebuildTree();
                return;
            case SettingsChange.Places:
                RebuildPlaces();
                return;
            case SettingsChange.TreeSelection:
                if (ActiveView is { } active)
                {
                    Tree.SelectPath(active.CurrentPath);
                }

                return;
        }
    }

    void RebuildTree() =>
        SelectAfter(Tree.Rebuild());

    // Quick access changed, a pin or an unpin say: only Pinned and Recent are made again.
    void RebuildPlaces() =>
        SelectAfter(Tree.RebuildPlaces());

    // Not when the tree kept a pinned folder selected that was just moved, so moving it again
    // with the keyboard carries on from it.
    void SelectAfter(bool selectedMoved)
    {
        if (!selectedMoved &&
            ActiveView is { } view)
        {
            Tree.SelectPath(view.CurrentPath);
        }
    }

    const string projectUrl = "https://github.com/SimonCropp/Lode";

    // Not a URL: the issue's is built when clicked, with the version and OS in its body.
    const string raiseIssue = "raise-issue";

    void ShowAbout()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        var page = new TaskDialogPage
        {
            Caption = "About Lode",
            Heading = $"Lode {version}",
            Text = $"""
                    A tabbed file manager with the classic Windows context menu.

                    <a href="{projectUrl}">{projectUrl}</a>

                    <a href="{raiseIssue}">Raise an issue</a>
                    """,
            EnableLinks = true,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK }
        };
        page.LinkClicked += (_, args) =>
        {
            if (args.LinkHref == raiseIssue)
            {
                IssueLauncher.Launch();
                return;
            }

            IssueLauncher.OpenUrl(args.LinkHref);
        };
        TaskDialog.ShowDialog(this, page);
    }

    void FocusAddress() =>
        breadcrumb.BeginEdit();

    void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        var view = ActiveView;
        if (view == null)
        {
            return;
        }

        if (args.KeyCode == Keys.Enter)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            view.GoToTyped(addressBox.Text);
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
        else if (args.KeyCode == Keys.Escape)
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
    }

    void OnFilterTextChanged(object? sender, EventArgs args)
    {
        if (!updatingFilter &&
            ActiveView is { } view)
        {
            view.Filter = filterBox.Text;
        }
    }

    void OnFilterKeyDown(object? sender, KeyEventArgs args)
    {
        // What it says, searched for inside the files instead.
        if (args.KeyData == (Keys.Control | Keys.Enter))
        {
            args.Handled = true;
            args.SuppressKeyPress = true;
            if (ActiveView is { CanSearchInsideTheFiles: true } view)
            {
                view.SearchInsideTheFiles();
            }

            return;
        }

        if (args.KeyCode == Keys.Escape)
        {
            filterBox.Clear();
        }
        else if (args.KeyCode is not (Keys.Enter or Keys.Down))
        {
            return;
        }

        args.Handled = true;
        args.SuppressKeyPress = true;
        ActiveView?.FocusList();
    }

    void OnTabsMouseUp(object? sender, MouseEventArgs args)
    {
        var page = TabAt(args.Location);
        if (page == null)
        {
            return;
        }

        if (args.Button == MouseButtons.Middle)
        {
            CloseTab(page);
        }
        else if (args.Button == MouseButtons.Right)
        {
            var menu = TabMenu(page);
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(Tabs, args.Location);
        }
    }

    /// <summary>
    /// What right-clicking <paramref name="page"/> offers. Pin and Unpin are for the folder that
    /// tab shows, which need not be the tab selected.
    /// </summary>
    internal ExplorerMenu TabMenu(TabPage page)
    {
        var menu = new ExplorerMenu();

        // Find Results has no folder to duplicate or pin.
        if (page.Controls.OfType<FolderView>().FirstOrDefault() is { } view)
        {
            menu.Items.Add("Duplicate tab", null, (_, _) => AddTab(view.CurrentPath, true, intoZip: true));
            var path = view.CurrentPath;
            if (!Locations.IsThisPC(path))
            {
                var pinned = IsPinned(path);
                var pinText = pinned ? "Unpin from Quick access" : "Pin to Quick access";
                menu.Items.Add(pinText, null, (_, _) => ShellContextMenu.SetPinned(Handle, path, !pinned));
            }

            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Close tab", null, (_, _) => CloseTab(page));
        var index = Tabs.TabPages.IndexOf(page);
        menu.Items.Add("Close other tabs", null, (_, _) => CloseOtherTabs(page));
        menu.Items.Add(new ToolStripMenuItem("Close tabs to the left", null, (_, _) => CloseTabsToTheLeft(page)) { Enabled = index > 0 });
        menu.Items.Add(new ToolStripMenuItem("Close tabs to the right", null, (_, _) => CloseTabsToTheRight(page)) { Enabled = index < Tabs.TabCount - 1 });
        menu.Items.Add("Close all tabs", null, (_, _) => CloseAllTabs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reopen closed tab", null, (_, _) => ReopenClosedTab()) { Enabled = CanReopenClosedTab, ShortcutKeyDisplayString = "Ctrl+Shift+T" });
        return menu;
    }

    void ShowCommandPalette() =>
        CommandPalette.Show(this, PaletteCommands());

    // Shortcuts of the view's own commands, which its menus do not show.
    static Dictionary<string, string> viewShortcuts = new()
    {
        ["Cut"] = "Ctrl+X",
        ["Copy"] = "Ctrl+C",
        ["Paste"] = "Ctrl+V",
        ["Rename"] = "F2",
        ["Delete"] = "Delete",
        ["Properties"] = "Alt+Enter",
        ["New: Folder"] = "Ctrl+Shift+N"
    };

    /// <summary>
    /// What the command palette offers: the window's commands, then those the right-click menu
    /// would show for the selection, or for the folder when nothing is selected. Commands that
    /// cannot run now are left out.
    /// </summary>
    internal List<CommandPalette.Command> PaletteCommands()
    {
        var view = ActiveView;
        var page = Tabs.SelectedTab;
        var index = Tabs.SelectedIndex;
        var commands = new List<CommandPalette.Command>();

        void Add(string name, Action action, string shortcut = "", bool enabled = true)
        {
            if (enabled)
            {
                commands.Add(new(name, action, shortcut));
            }
        }

        Add("Back", () => ActiveView?.GoBack(), "Alt+Left", view?.CanGoBack == true);
        Add("Forward", () => ActiveView?.GoForward(), "Alt+Right", view?.CanGoForward == true);
        Add("Up", () => ActiveView?.GoUp(), "Alt+Up", view?.CanGoUp == true);
        Add("Refresh", Reload, "F5");
        Add("Go to address", FocusAddress, "Ctrl+L");
        Add("Search this folder", FocusFilter, "Ctrl+F");
        Add("New tab", NewTab, "Ctrl+T");
        Add("New window", NewWindow, "Ctrl+N");
        Add("Find in files", FindHost.Open, "Ctrl+Shift+F");
        Add("Clone a repository…", ShowClone, "Ctrl+Shift+O");
        if (GitPanes.Repo != null)
        {
            Add("Changes", () => GitPanes.Toggle(GitPane.Changes), "Ctrl+Shift+G");
            Add("History", () => GitPanes.Toggle(GitPane.History), "Ctrl+Shift+H");
            Add(GitPanes.IsPinned(GitPane.Changes) ? "Unpin changes" : "Pin changes", () => GitPanes.SetPinned(GitPane.Changes, !GitPanes.IsPinned(GitPane.Changes)));
            Add(GitPanes.IsPinned(GitPane.History) ? "Unpin history" : "Pin history", () => GitPanes.SetPinned(GitPane.History, !GitPanes.IsPinned(GitPane.History)));
        }

        if (page != null)
        {
            if (view != null)
            {
                Add("Duplicate tab", () => AddTab(view.CurrentPath, true, intoZip: true));
            }

            Add("Close tab", () => CloseTab(page), "Ctrl+W");
            Add("Close other tabs", () => CloseOtherTabs(page), enabled: Tabs.TabCount > 1);
            Add("Close tabs to the left", () => CloseTabsToTheLeft(page), enabled: index > 0);
            Add("Close tabs to the right", () => CloseTabsToTheRight(page), enabled: index < Tabs.TabCount - 1);
            Add("Close all tabs", CloseAllTabs);
        }

        Add("Reopen closed tab", ReopenClosedTab, "Ctrl+Shift+T", CanReopenClosedTab);
        var history = UndoHistory.Instance;
        Add(history.UndoText, Undo, "Ctrl+Z", history.CanUndo);
        Add(history.RedoText, Redo, "Ctrl+Y", history.CanRedo);
        Add("Next tab", () => SelectAdjacentTab(1), "Ctrl+Tab", Tabs.TabCount > 1);
        Add("Previous tab", () => SelectAdjacentTab(-1), "Ctrl+Shift+Tab", Tabs.TabCount > 1);
        Add(settings.ShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden, "Ctrl+H");
        Add(settings.ShowSystem ? "Hide system files" : "Show system files", ToggleSystem);
        Add("Terminal", OpenTerminal, enabled: PinnablePath() != null);
        Add("Terminal (admin)", OpenAdminTerminal, enabled: PinnablePath() != null);
        if (ClaudeDesktop.IsInstalled())
        {
            Add("Open in Claude", OpenInClaude, enabled: PinnablePath() != null);
        }

        // Named as the folder's own would be, which these stand in for when a file is selected.
        foreach (var agent in CodingAgents.Installed())
        {
            Add($"Coding agent: {agent.Name}", () => OpenAgent(agent), enabled: PinnablePath() != null);
        }

        if (PinMenuText() is { } pinText)
        {
            Add(pinText, TogglePinned);
        }

        if (view != null)
        {
            var selection = view.SelectedEntries();
            List<QuickMenu.Command?> viewCommands;
            if (selection.Count > 0)
            {
                viewCommands = view.ItemCommands(selection);
            }
            else if (!view.ListsPlaces)
            {
                viewCommands = view.BackgroundCommands(FileOperations.ReadClipboard() != null);
            }
            else
            {
                viewCommands = [];
            }

            var selectionCommands = new List<CommandPalette.Command>();
            AddViewCommands(selectionCommands, view, viewCommands, "");

            // The window's Terminal, Open in Claude and Coding agent open in the tab's folder.
            // Where the selection has its own, that one stands in, in the window's place, so it
            // opens in the folder selected, as the right-click menu and the actions bar do.
            for (var position = 0; position < commands.Count; position++)
            {
                if (OpensInTheFolder(commands[position].Name) &&
                    selectionCommands.FirstOrDefault(_ => SameName(_, commands[position])) is { } own)
                {
                    commands[position] = own;
                }
            }

            commands.AddRange(selectionCommands);
        }

        Add("Options…", ShowOptions);
        Add("About Lode", ShowAbout);
        Add("Exit Lode", () => ExitRequested?.Invoke());

        // A view command named as a window one (New folder, say) would only repeat it.
        return commands
            .DistinctBy(_ => _.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static bool OpensInTheFolder(string name) =>
        name is "Terminal" or "Terminal (admin)" or "Open in Claude" ||
        name.StartsWith("Coding agent: ", StringComparison.Ordinal);

    static bool SameName(CommandPalette.Command command, CommandPalette.Command other) =>
        string.Equals(command.Name, other.Name, StringComparison.CurrentCultureIgnoreCase);

    // A submenu's entries are named after it: "New: Text Document", "Open with: Notepad".
    static void AddViewCommands(List<CommandPalette.Command> commands, FolderView view, IReadOnlyList<QuickMenu.Command?> viewCommands, string prefix)
    {
        foreach (var command in viewCommands)
        {
            if (command is not { Enabled: true })
            {
                continue;
            }

            var name = prefix + command.Text;
            if (command.Children != null)
            {
                AddViewCommands(commands, view, command.Children, $"{name}: ");
                continue;
            }

            var action = command.Action;
            commands.Add(
                new(
                    name,
                    () =>
                    {
                        // Back on the list first, so Rename has an item to edit.
                        view.FocusList();
                        action();
                    },
                    viewShortcuts.GetValueOrDefault(name, "")));
        }
    }

    void FocusFilter()
    {
        filterBox.Focus();
        filterBox.SelectAll();
    }

    // As Find Results shows: what was found so far in the status bar, and none of what is a
    // folder's, its free space, its repository, or the actions on its selection.
    void UpdateResultsStatus()
    {
        ShowResultsProgress();
        searchInsideLabel.Visible = false;
        spaceLabel.Text = "";
        siteLabel.Visible = false;
        ShowGitParts(null);
        ActionsBar.Update();
    }

    // Ten times a second while searching. Set once, and only when it says something else: the
    // status bar emptied, even for a moment, is shorter, and everything above it moves.
    void ShowResultsProgress()
    {
        var text = FindHost.Progress;
        if (statusLabel.Text != text)
        {
            statusLabel.Text = text;
        }
    }

    // Find Results has no folder: going back, forward and up, the address and the search box
    // are for the folders' tabs.
    void ShowingFolder(bool folder)
    {
        breadcrumb.Enabled = folder;
        filterFrame.Enabled = folder;
        if (folder)
        {
            return;
        }

        backButton.Enabled = false;
        forwardButton.Enabled = false;
        upButton.Enabled = false;
    }

    // Show in folder, from Find Results: the tab showing the folder the file is in, or a new
    // one, with the file selected.
    void ShowInFolder(string path)
    {
        if (SelectTabShowing(path))
        {
            return;
        }

        AddTab(path, true);
    }

    // Ctrl+Z and Ctrl+Y: through the tab's view, which says when one can't be done; with Find
    // Results showing, which has none, the window says.
    void Undo()
    {
        if (ActiveView is { } view)
        {
            view.Undo();
            return;
        }

        RunUndo(UndoHistory.Instance.Undo);
    }

    void Redo()
    {
        if (ActiveView is { } view)
        {
            view.Redo();
            return;
        }

        RunUndo(UndoHistory.Instance.Redo);
    }

    async void RunUndo(Func<IntPtr, Task<string?>> step)
    {
        var failure = await step(Handle);
        if (failure != null &&
            !IsDisposed)
        {
            MessageBox.Show(this, failure, "Lode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    TabPage? TabAt(Point location)
    {
        for (var index = 0; index < Tabs.TabCount; index++)
        {
            if (Tabs.GetTabRect(index).Contains(location))
            {
                return Tabs.TabPages[index];
            }
        }

        return null;
    }

    internal void CloseOtherTabs(TabPage keep) =>
        CloseTabs(TabPages.Where(_ => _ != keep));

    internal void CloseTabsToTheLeft(TabPage keep) =>
        CloseTabs(TabPages.TakeWhile(_ => _ != keep));

    internal void CloseTabsToTheRight(TabPage keep) =>
        CloseTabs(TabPages.SkipWhile(_ => _ != keep).Skip(1));

    // Closing the last one closes the window, as Ctrl+W on a lone tab does.
    internal void CloseAllTabs() =>
        CloseTabs(TabPages);

    IEnumerable<TabPage> TabPages => Tabs.TabPages.Cast<TabPage>();

    // The selected tab among those going, the nearest one staying is selected first, so the
    // others going are not each shown, and their folders read, in turn.
    void CloseTabs(IEnumerable<TabPage> pages)
    {
        var closing = pages.ToList();
        if (Tabs.SelectedTab is { } selected &&
            closing.Contains(selected))
        {
            var all = TabPages.ToList();
            var index = all.IndexOf(selected);
            var staying = all
                .Where(_ => !closing.Contains(_))
                .OrderBy(_ => Math.Abs(all.IndexOf(_) - index))
                .ThenByDescending(all.IndexOf)
                .FirstOrDefault();
            if (staying != null)
            {
                Tabs.SelectedTab = staying;
            }
        }

        foreach (var page in closing)
        {
            CloseTab(page);
        }
    }

    void SelectAdjacentTab(int offset)
    {
        if (Tabs.TabCount > 1)
        {
            Tabs.SelectedIndex = (Tabs.SelectedIndex + offset + Tabs.TabCount) % Tabs.TabCount;
            ActiveView?.FocusList();
        }
    }

    // Ctrl+1 to Ctrl+8 go to the tab at that place, and Ctrl+9 to the last, as in Explorer and
    // the browsers. One that isn't there does nothing.
    void SelectTabAt(int number)
    {
        var index = number - 1;
        if (number == 9)
        {
            index = Tabs.TabCount - 1;
        }

        if (index < 0 ||
            index >= Tabs.TabCount)
        {
            return;
        }

        Tabs.SelectedIndex = index;
        ActiveView?.FocusList();
    }

    // The number Ctrl was held with, on the top row or the number pad, or 0.
    static int TabNumber(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != Keys.Control)
        {
            return 0;
        }

        var key = keyData & Keys.KeyCode;
        if (key is >= Keys.D1 and <= Keys.D9)
        {
            return key - Keys.D0;
        }

        if (key is >= Keys.NumPad1 and <= Keys.NumPad9)
        {
            return key - Keys.NumPad0;
        }

        return 0;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (TabNumber(keyData) is > 0 and var number)
        {
            SelectTabAt(number);
            return true;
        }

        switch (keyData)
        {
            case Keys.Alt | Keys.Left:
                ActiveView?.GoBack();
                return true;
            case Keys.Alt | Keys.Right:
                ActiveView?.GoForward();
                return true;
            case Keys.Alt | Keys.Up:
                ActiveView?.GoUp();
                return true;
            case Keys.F5:
                Reload();
                return true;
            case Keys.Control | Keys.T:
                NewTab();
                return true;
            case Keys.Control | Keys.W:
                if (Tabs.SelectedTab is { } selected)
                {
                    CloseTab(selected);
                }

                return true;
            case Keys.Control | Keys.Z when !IsEditingText():
                Undo();
                return true;
            case Keys.Control | Keys.Y when !IsEditingText():
            case Keys.Control | Keys.Shift | Keys.Z when !IsEditingText():
                Redo();
                return true;
            case Keys.Control | Keys.Shift | Keys.F:
                FindHost.Open();
                return true;
            case Keys.Control | Keys.Shift | Keys.T:
                ReopenClosedTab();
                return true;
            case Keys.Control | Keys.Shift | Keys.P:
                ShowCommandPalette();
                return true;
            case Keys.Control | Keys.Tab:
                SelectAdjacentTab(1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                SelectAdjacentTab(-1);
                return true;
            // With Find Results showing, which has no address, the next match and the one before.
            case Keys.F4 when FindHost.ResultsShowing:
                FindHost.ResultsView?.GoToMatch(true);
                return true;
            case Keys.Shift | Keys.F4 when FindHost.ResultsShowing:
                FindHost.ResultsView?.GoToMatch(false);
                return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F4:
                FocusAddress();
                return true;
            case Keys.Control | Keys.F:
            case Keys.F3:
                FocusFilter();
                return true;
            case Keys.Control | Keys.N:
                NewWindow();
                return true;
            case Keys.Control | Keys.H:
                ToggleHidden();
                return true;
            case Keys.Control | Keys.B:
                ShowBranches();
                return true;
            case Keys.Control | Keys.Shift | Keys.G:
                GitPanes.Toggle(GitPane.Changes);
                return true;
            case Keys.Control | Keys.Shift | Keys.H:
                GitPanes.Toggle(GitPane.History);
                return true;
            case Keys.Control | Keys.Shift | Keys.O:
                ShowClone();
                return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    // The history changes on the thread that did the work; the buttons follow on the UI thread.
    void OnUndoChanged()
    {
        if (IsHandleCreated &&
            !IsDisposed)
        {
            BeginInvoke(ActionsBar.Update);
        }
    }

    // Typing in a box (the address, the filter, a name being edited) keeps Ctrl+Z for the text.
    static unsafe bool IsEditingText()
    {
        var focus = GetFocus();
        if (focus == IntPtr.Zero)
        {
            return false;
        }

        const int capacity = 64;
        var name = stackalloc char[capacity];
        var length = GetClassNameW(focus, name, capacity);
        return new CharSpan(name, Math.Max(length, 0)).Contains("Edit", StringComparison.OrdinalIgnoreCase);
    }

    // What Explorer tells every window when it starts, as the taskbar: after a crash, or when
    // restarted from Task Manager, which empties the shell's list of folder windows.
    static int explorerStartedMessage = RegisterWindowMessage("TaskbarCreated");

    // WM_CLIPBOARDUPDATE.
    const int clipboardUpdate = 0x031D;

    // WM_DEVICECHANGE, a device's arrival and removal, and the broadcast's type for a volume.
    // Windows tells every top-level window of a drive plugged in, mapped, taken out or
    // disconnected.
    const int deviceChange = 0x0219;
    const int deviceArrival = 0x8000;
    const int deviceRemoveComplete = 0x8004;
    const int volumeDevice = 2;

    // DEV_BROADCAST_HDR's device type, after its size.
    static bool IsVolume(IntPtr broadcast) =>
        broadcast != IntPtr.Zero &&
        Marshal.ReadInt32(broadcast, 4) == volumeDevice;

    // A drive coming or going is told of more than once, as each of its parts arrives, and
    // several drives can come at once, from a card reader say; the drives are read once, a
    // little after the last.
    Timer drivesTimer = new()
    {
        Interval = 300
    };

    // The drives in the tree, and in the tabs showing This PC, read and described again, as on
    // F5: another disk can be under a letter now. Only those that came or went are added or
    // taken away.
    void DrivesChanged()
    {
        ShellIcons.ForgetDrives();
        Tree.RefreshDrives();
        foreach (var view in Views)
        {
            if (Locations.IsThisPC(view.CurrentPath))
            {
                view.Reload();
            }
        }
    }

    // The mouse's back and forward buttons arrive as app commands from whichever control is
    // under the pointer.
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == explorerStartedMessage)
        {
            ShellWindows.ExplorerRestarted(this);
        }

        if (message.Msg == clipboardUpdate)
        {
            CutItems.Refresh();
        }

        if (message.Msg == deviceChange &&
            (int)message.WParam is deviceArrival or deviceRemoveComplete &&
            IsVolume(message.LParam))
        {
            drivesTimer.Stop();
            drivesTimer.Start();
        }

        if (message.Msg == WindowMessageAppCommand)
        {
            var command = (int)((message.LParam.ToInt64() >> 16) & 0xFFF);
            if (command is browserBackwardCommand or browserForwardCommand)
            {
                if (command == browserBackwardCommand)
                {
                    ActiveView?.GoBack();
                }
                else
                {
                    ActiveView?.GoForward();
                }

                message.Result = 1;
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            places.Changed -= RebuildPlaces;
            UndoHistory.Instance.Changed -= OnUndoChanged;
            updates.Changed -= OnUpdatesChanged;
            ActionLog.Changed -= ReadGitState;
            GitPanes.Dispose();
            DiffHost.Dispose();
            toolTip.Dispose();
            statusTip.Dispose();
            trayTimer.Dispose();
            unusedTabsTimer.Dispose();
            tabsTimer.Dispose();
            drivesTimer.Dispose();
            if (usedLast == this)
            {
                usedLast = null;
            }
        }

        base.Dispose(disposing);
    }
}
