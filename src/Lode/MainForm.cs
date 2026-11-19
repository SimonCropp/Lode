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
    Breadcrumb breadcrumb = new();
    TextBox addressBox;
    ToolStrip actionsBar = new();

    // The actions bar's buttons, by action id.
    readonly Dictionary<string, ToolStripItem> actionButtons = [];

    // The actions bar's drop-downs for parents that Options nests buttons under, each with the
    // actions in it, in LodeActions' order.
    readonly Dictionary<LodeActions.Parent, (ToolStripItem Button, List<LodeActions.Definition> Members)> parentButtons = [];
    FramedTextBox filterFrame = new();
    TextBox filterBox;
    PaneSplitContainer split = new();
    Settings settings;
    Places places;
    FolderTree tree;
    readonly ActionsPanel actionsPanel = new();
    ClosableTabControl tabs = new();
    ToolStripStatusLabel statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    ToolStripStatusLabel spaceLabel = new();

    // The instance this window listens to, so it stops listening to that one when it closes.
    readonly Updates updates = Updates.Instance;

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
    readonly List<string> closedTabs = [];
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

    public FolderView? ActiveView => tabs.SelectedTab?.Controls.OfType<FolderView>().FirstOrDefault();

    public IEnumerable<FolderView> Views =>
        tabs.TabPages.Cast<TabPage>().Select(_ => _.Controls.OfType<FolderView>().First());

    public MainForm(string[] arguments, Settings settings, Places places) :
        this(settings, places) =>
        OpenInitialTabs(arguments);

    // A window with no tabs yet.
    MainForm(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        tree = new(settings, places);
        addressBox = breadcrumb.Box;
        filterBox = filterFrame.Box;
        places.Changed += RebuildPlaces;
        UndoHistory.Instance.Changed += OnUndoChanged;
        updates.Changed += OnUpdatesChanged;
        trayTimer.Tick += (_, _) =>
        {
            trayTimer.Stop();
            if (hidden)
            {
                ReleaseMemory();
            }
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
        upButton = CreateButton(upGlyph, "Up (Alt+Up)", () => ActiveView?.GoUp());

        Controls.Add(BuildContent());
        Controls.Add(BuildActionsBar());
        Controls.Add(BuildNavigationBar());
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

    Button CreateButton(string glyph, string tip, Action action)
    {
        var button = new GlyphButton
        {
            Text = glyph,
            Size = new(36, 30),
            Margin = new(1),
            TabStop = false
        };
        button.Click += (_, _) => action();
        toolTip.SetToolTip(button, tip);
        return button;
    }

    // Explorer's command bar: buttons for what is most often done to the selection.
    Control BuildActionsBar()
    {
        actionsBar.Dock = DockStyle.Top;
        actionsBar.GripStyle = ToolStripGripStyle.Hidden;
        // In a menu's font, which a tool strip takes unless told otherwise: smaller than the rest
        // of the window, as Explorer's command bar is.
        actionsBar.Renderer = new ExplorerRenderer();
        // Roomy, and as tall as Explorer's command bar.
        actionsBar.Padding = new(0, LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(6));
        RebuildActionsBar();
        return actionsBar;
    }

    // The buttons Options asks for, in LodeActions' order, with a separator between groups, then
    // See more, as Explorer's command bar ends, for the actions that are not buttons.
    void RebuildActionsBar()
    {
        actionsBar.SuspendLayout();
        foreach (var item in actionsBar.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }

        actionsBar.Items.Clear();
        actionButtons.Clear();
        parentButtons.Clear();
        // Explorer's size for its command bar's icons.
        var iconSize = LogicalToDeviceUnits(16);
        actionsBar.ImageScalingSize = new(iconSize, iconSize);
        int? group = null;
        foreach (var action in LodeActions.Offered().Where(_ => settings.ShowsAsButton(_.Id)))
        {
            // A nested button is in its parent's drop-down, which stands where its first member
            // would have.
            var parent = settings.NestsButton(action.Id) ? action.Parent : null;
            if (parent != null &&
                parentButtons.TryGetValue(parent, out var known))
            {
                known.Members.Add(action);
                continue;
            }

            if (group != null &&
                group != action.Group)
            {
                actionsBar.Items.Add(ActionsSeparator());
            }

            group = action.Group;
            if (parent != null)
            {
                List<LodeActions.Definition> members = [action];
                var dropDown = CreateDropDown(parent.Text, parent.Glyph, parent.Text, iconSize, () => ParentEntries(members));
                parentButtons[parent] = (dropDown, members);
                actionsBar.Items.Add(dropDown);
                continue;
            }

            var button = CreateActionButton(action, iconSize);
            actionButtons[action.Id] = button;
            actionsBar.Items.Add(button);
        }

        if (LodeActions.Offered().Any(_ => !settings.ShowsAsButton(_.Id)))
        {
            if (actionsBar.Items.Count > 0)
            {
                actionsBar.Items.Add(ActionsSeparator());
            }

            // Larger than the rest: at their size the dots are smaller than Explorer's.
            actionsBar.Items.Add(CreateOverflowButton(LogicalToDeviceUnits(20)));
        }

        actionsBar.Visible = actionsBar.Items.Count > 0;

        actionsBar.ResumeLayout();
        UpdateActions();
    }

    // Explorer's See more: the actions not shown as buttons, in a list of their own.
    ToolStripDropDownButton CreateOverflowButton(int iconSize)
    {
        var button = new ToolStripDropDownButton("See more")
        {
            Name = "seeMore",
            Image = Icons.Bitmap(Icons.More, iconSize, SystemColors.ControlText),
            ImageScaling = ToolStripItemImageScaling.None,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ShowDropDownArrow = false,
            ToolTipText = "See more",
            Padding = new(LogicalToDeviceUnits(8), LogicalToDeviceUnits(5), LogicalToDeviceUnits(8), LogicalToDeviceUnits(5)),
            Margin = new(LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(2), 0)
        };
        // Without an item it would not open; filled with the real ones as it opens.
        button.DropDownItems.Add("See more");
        button.DropDownOpening += (_, _) => FillOverflow(button);
        ExplorerRenderer.Style(button.DropDown);
        return button;
    }

    void FillOverflow(ToolStripDropDownButton button)
    {
        foreach (var image in dropDownImages)
        {
            image.Dispose();
        }

        dropDownImages.Clear();
        button.DropDownItems.Clear();
        var iconSize = LogicalToDeviceUnits(16);
        button.DropDown.ImageScalingSize = new(iconSize, iconSize);
        var below = actionsBar.PointToScreen(new(button.Bounds.Left, button.Bounds.Bottom));
        QuickMenu.AddItems(button.DropDownItems, OverflowCommands(below), RunOnList, iconSize, dropDownImages);
    }

    /// <summary>
    /// What See more lists: each action not shown as a button, in LodeActions' order with a
    /// separator between groups, as the right-click menu would offer it now, or greyed out where
    /// it does not apply. Show more options opens the full Windows menu at
    /// <paramref name="location"/>, on the screen.
    /// </summary>
    internal List<QuickMenu.Command?> OverflowCommands(Point location)
    {
        var commands = ContextCommands();
        var result = new List<QuickMenu.Command?>();
        int? group = null;
        foreach (var action in LodeActions.Offered().Where(_ => !settings.ShowsAsButton(_.Id)))
        {
            if (group != null &&
                group != action.Group)
            {
                result.Add(null);
            }

            group = action.Group;
            if (action.Id == LodeActions.More)
            {
                result.Add(
                    LodeActions.Command(
                        action.Id,
                        () => ActiveView?.ShowShellMenu(location),
                        ActiveView?.CanShowShellMenu == true));
                continue;
            }

            result.Add(ActionEntry(action, commands));
        }

        return result;
    }

    // An action as the right-click menu would offer it now, out of its commands, or greyed out
    // where it does not apply.
    static QuickMenu.Command ActionEntry(LodeActions.Definition action, Dictionary<string, QuickMenu.Command> commands)
    {
        if (commands.TryGetValue(action.Id, out var command))
        {
            return command;
        }

        return LodeActions.Command(action.Id, () => { }, enabled: false);
    }

    // What a parent's drop-down on the bar lists: each action nested in it, as See more would,
    // by what it is called under its parent.
    List<QuickMenu.Command?> ParentEntries(IReadOnlyList<LodeActions.Definition> members)
    {
        var commands = ContextCommands();
        return [.. members.Select(_ => LodeActions.Nested(ActionEntry(_, commands)))];
    }

    /// <summary>
    /// What the drop-down on the bar for the parent named <paramref name="text"/> lists, for
    /// tests.
    /// </summary>
    internal List<QuickMenu.Command?> ParentEntries(string text) =>
        ParentEntries(parentButtons.Single(_ => _.Key.Text == text).Value.Members);

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

    // As in Explorer's command bar, drop-downs (New, Open with) keep their name beside their
    // icon; the rest show theirs as a tooltip. Disabled buttons grey their icon themselves.
    ToolStripItem CreateActionButton(LodeActions.Definition action, int iconSize)
    {
        var toolTip = action.Text;
        if (action.Shortcut.Length > 0)
        {
            toolTip = $"{action.Text} ({action.Shortcut})";
        }

        if (action.Submenu)
        {
            return CreateDropDown(action.Text, action.Glyph, toolTip, iconSize, () => SubmenuEntries(action.Id));
        }

        var item = new ToolStripButton(action.Text)
        {
            Image = Icons.Bitmap(action.Glyph, iconSize, SystemColors.ControlText),
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = toolTip
        };
        item.Click += (_, _) => RunAction(action.Id, item);
        SpaceActionButton(item);
        return item;
    }

    // Spaced as Explorer's are, about 48 pixels apart at 100%, and as tall as the bar needs.
    void SpaceActionButton(ToolStripItem item)
    {
        item.ImageScaling = ToolStripItemImageScaling.None;
        item.Padding = new(LogicalToDeviceUnits(13), LogicalToDeviceUnits(7), LogicalToDeviceUnits(13), LogicalToDeviceUnits(7));
        item.Margin = new(LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(2), 0);
    }

    // A drop-down on the bar, for a submenu action (New, Open with) or a parent of nested
    // buttons: its name beside its icon, then a chevron, filled with <paramref name="entries"/>
    // as it opens.
    ChevronDropDownButton CreateDropDown(string text, char glyph, string toolTip, int iconSize, Func<IReadOnlyList<QuickMenu.Command?>> entries)
    {
        var dropDown = new ChevronDropDownButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ToolTipText = toolTip,
            // Its name as far from its icon as Explorer's New is.
            Image = Icons.Bitmap(glyph, iconSize, SystemColors.ControlText, LogicalToDeviceUnits(6))
        };
        // Without an item it would not open; filled with the real ones as it opens.
        dropDown.DropDownItems.Add(text);
        dropDown.DropDownOpening += (_, _) => FillDropDown(dropDown, entries());
        ExplorerRenderer.Style(dropDown.DropDown);
        SpaceActionButton(dropDown);
        dropDown.Padding = dropDown.Padding with
        {
            Right = dropDown.Padding.Right + dropDown.ChevronWidth
        };
        return dropDown;
    }

    ToolStripSeparator ActionsSeparator() =>
        new()
        {
            Margin = new(LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4), 0)
        };

    // A submenu action's drop-down lists the submenu the right-click menu would show now.
    IReadOnlyList<QuickMenu.Command?> SubmenuEntries(string id)
    {
        if (ContextCommands().TryGetValue(id, out var command) &&
            command.Children != null)
        {
            return command.Children;
        }

        return [];
    }

    void FillDropDown(ChevronDropDownButton dropDown, IReadOnlyList<QuickMenu.Command?> entries)
    {
        foreach (var image in dropDownImages)
        {
            image.Dispose();
        }

        dropDownImages.Clear();
        dropDown.DropDownItems.Clear();
        var iconSize = LogicalToDeviceUnits(16);
        dropDown.DropDown.ImageScalingSize = new(iconSize, iconSize);
        QuickMenu.AddItems(dropDown.DropDownItems, entries, RunOnList, iconSize, dropDownImages);
    }

    // Icons drawn for the open drop-down's entries.
    readonly List<Image> dropDownImages = [];

    // Back on the list first, so Rename has an item to edit and what the action opens has the
    // focus to return to.
    void RunOnList(Action action)
    {
        ActiveView?.FocusList();
        action();
    }

    /// <summary>
    /// Runs an action from its button: what the right-click menu would do for the selection, or
    /// for the folder when nothing is selected.
    /// </summary>
    internal void RunAction(string id, ToolStripItem? button = null)
    {
        if (ActiveView is not { } view)
        {
            return;
        }

        if (id == LodeActions.More)
        {
            var below = Point.Empty;
            if (button != null)
            {
                below = actionsBar.PointToScreen(new(button.Bounds.Left, button.Bounds.Bottom));
            }

            view.ShowShellMenu(below);
            return;
        }

        if (ContextCommands().TryGetValue(id, out var command) &&
            command is { Enabled: true, Children: null })
        {
            RunOnList(command.Action);
        }
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

    // What the buttons act on: the right-click menu's commands for the selection, then the
    // folder's, so New and Paste still work with something selected. The selection's win where
    // both have one (Terminal on a selected folder opens it). The selection is the active view's,
    // read from its list here unless the caller already has it.
    Dictionary<string, QuickMenu.Command> ContextCommands(List<FileEntry>? selection = null)
    {
        var result = new Dictionary<string, QuickMenu.Command>();
        if (ActiveView is not { } view)
        {
            return result;
        }

        var commands = new List<QuickMenu.Command?>();
        selection ??= view.SelectedEntries();
        if (selection.Count > 0)
        {
            commands.AddRange(view.ItemCommands(selection));
        }

        if (!view.ListsPlaces)
        {
            // Not tied to the clipboard, which changes without telling Lode. Paste with nothing
            // to paste does nothing.
            commands.AddRange(view.BackgroundCommands(canPaste: true));
        }

        // Undo and Redo work anywhere, This PC included.
        commands.AddRange(view.UndoCommands());

        foreach (var command in commands)
        {
            if (command is { Id: { } id })
            {
                result.TryAdd(id, command);
            }
        }

        return result;
    }

    /// <summary>
    /// Enables the actions bar's buttons for the active view and its selection.
    /// </summary>
    internal void UpdateActions() =>
        UpdateActions(null);

    // For a selection the caller has already read from the active view's list.
    void UpdateActions(List<FileEntry>? selection)
    {
        if (actionButtons.Count == 0 &&
            parentButtons.Count == 0)
        {
            return;
        }

        var commands = ContextCommands(selection);
        foreach (var (id, button) in actionButtons)
        {
            if (id == LodeActions.More)
            {
                button.Enabled = ActiveView?.CanShowShellMenu == true;
                continue;
            }

            button.Enabled = Applies(commands, id);
        }

        // A parent's drop-down while any action in it applies.
        foreach (var (button, members) in parentButtons.Values)
        {
            button.Enabled = members.Any(_ => Applies(commands, _.Id));
        }
    }

    // Whether the action has a command now that can run, or a submenu with one in it.
    static bool Applies(Dictionary<string, QuickMenu.Command> commands, string id) =>
        commands.TryGetValue(id, out var command) &&
        command.Enabled &&
        (command.Children == null || command.Children.Any(_ => _ is { Enabled: true }));

    /// <summary>
    /// The actions bar's buttons and whether each is enabled, for tests.
    /// </summary>
    internal IEnumerable<string> DescribeActions() =>
        actionsBar.Items
            .OfType<ToolStripItem>()
            .Select(DescribeAction);

    static string DescribeAction(ToolStripItem item)
    {
        if (item is ToolStripSeparator)
        {
            return "----";
        }

        if (item.Enabled)
        {
            return item.Text!;
        }

        return $"{item.Text} (disabled)";
    }

    Control BuildContent()
    {
        tree.Dock = DockStyle.Fill;
        tree.PathSelected += _ => ActiveView?.Navigate(_);
        tree.NewTabRequested += _ => AddTab(_, false);
        tree.NewWindowRequested += NewWindow;
        tree.NewRequested += (folder, type) => _ = CreateFromTree(folder, type);

        tabs.Dock = DockStyle.Fill;
        tabs.CloseRequested += CloseTab;
        tabs.TabDraggedOff += OnTabDraggedOff;
        tabs.SelectedIndexChanged += (_, _) => SyncToActiveView();
        tabs.MouseUp += OnTabsMouseUp;

        split.Dock = DockStyle.Fill;
        split.FixedPanel = FixedPanel.Panel1;
        // Added after the tree so it is docked first, and the tree fills what is left above it.
        split.Panel1.Controls.Add(tree);
        split.Panel1.Controls.Add(actionsPanel);
        split.Panel2.Controls.Add(tabs);
        return split;
    }

    Control BuildStatusBar()
    {
        // White, as Explorer's is, with its text further in from the edge, and no grip: Windows
        // 11 windows are resized from their edges.
        var status = new StatusStrip
        {
            Renderer = new ExplorerRenderer(),
            SizingGrip = false,
            Padding = new(LogicalToDeviceUnits(10), LogicalToDeviceUnits(2), LogicalToDeviceUnits(10), LogicalToDeviceUnits(2))
        };
        status.Items.Add(statusLabel);
        status.Items.Add(updateLabel);
        status.Items.Add(spaceLabel);
        updateLabel.Click += (_, _) => ShowOptions("Updates");
        ShowUpdate();
        return status;
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
                        File.Exists(_))
            .ToList();

    void OpenInitialTabs(string[] arguments)
    {
        var paths = Openable(arguments);
        var active = paths.Count - 1;
        if (paths.Count == 0)
        {
            // The tab that was active is as many along as there are tabs before it still to
            // open: one whose folder has gone since leaves those after it a place earlier.
            active = 0;
            for (var index = 0; index < settings.Tabs.Count; index++)
            {
                var path = settings.Tabs[index];
                if (!Locations.IsThisPC(path) &&
                    !Locations.IsRemote(path) &&
                    !Directory.Exists(path))
                {
                    continue;
                }

                if (index < settings.ActiveTab)
                {
                    active++;
                }

                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            paths.Add(settings.StartLocation());
        }

        foreach (var path in paths)
        {
            AddTab(path, false);
        }

        tabs.SelectedIndex = Math.Clamp(active, 0, tabs.TabCount - 1);
        SyncToActiveView();
    }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
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

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ActiveView?.FocusList();
    }

    // Again whenever WinForms makes the window anew, which changing some properties does.
    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        TaskbarIdentity.Apply(Handle);

        // The title bar in Mica, the material Explorer's tab strip is drawn in, tinted by the
        // desktop behind the window. Windows 10 and early Windows 11 don't have it, and ignore
        // the setting.
        var backdrop = DwmSystemBackdropMainWindow;
        DwmSetWindowAttribute(Handle, DwmWindowSystemBackdropType, ref backdrop, sizeof(int));
    }

    protected override void OnHandleDestroyed(EventArgs eventArgs)
    {
        TaskbarIdentity.Clear(Handle);
        base.OnHandleDestroyed(eventArgs);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        base.OnFormClosing(eventArgs);
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
                     eventArgs.CloseReason is
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
        settings.Tabs = tabs.TabPages
            .Cast<TabPage>()
            .Select(_ => _.Controls.OfType<FolderView>().First().CurrentPath)
            .ToList();
        settings.ActiveTab = tabs.SelectedIndex;
        settings.Save();

        if (hiding)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    public FolderView AddTab(string path, bool activate)
    {
        var view = new FolderView(settings, places)
        {
            Dock = DockStyle.Fill,
            // Until it is the tab showing, which is when it reads its folder.
            Showing = false
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
        view.StatusChanged += () => WindowOf(view)?.OnViewStatusChanged(view);
        view.NewTabRequested += _ => WindowOf(view)?.AddTab(_, false);
        tabs.TabPages.Add(page);
        view.OpenAt(path);

        if (activate)
        {
            tabs.SelectedTab = page;
            // In a window not shown yet, the tab control has no handle, so selecting a tab only
            // notes which, and nothing says it changed: the title, address bar, buttons and
            // tree are brought in line here instead.
            if (!tabs.IsHandleCreated)
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
        var file = !Locations.IsRemote(path) && File.Exists(path);
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

        tabs.SelectedTab = (TabPage)view.Parent!;
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

    void CloseTab(TabPage page)
    {
        if (tabs.TabCount == 1)
        {
            Close();
            return;
        }

        var index = tabs.TabPages.IndexOf(page);
        var wasSelected = tabs.SelectedTab == page;
        RememberClosed(page);
        tabs.TabPages.Remove(page);
        page.Dispose();
        if (wasSelected)
        {
            tabs.SelectedIndex = Math.Min(index, tabs.TabCount - 1);
            SyncToActiveView();
        }
        else
        {
            // A folder that had the same name as this one's may go back to its name alone.
            Retitle();
        }
    }

    void RememberClosed(TabPage page)
    {
        closedTabs.Add(page.Controls.OfType<FolderView>().First().CurrentPath);
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

        AddTab(path, true);
    }

    static MainForm? WindowOf(Control? control) =>
        control?.FindForm() as MainForm;

    void OnTabDraggedOff(TabPage page, Point screenLocation)
    {
        // A tab dropped on the Pinned header pins its folder, and stays where it was.
        if (WindowAt(screenLocation) is FolderTree tree &&
            tree.PinIfOverPinned(screenLocation, page.Controls.OfType<FolderView>().First().CurrentPath))
        {
            return;
        }

        var target = WindowOf(WindowAt(screenLocation));
        if (target != null &&
            target != this)
        {
            MoveTab(page, target);
            target.Activate();
            return;
        }

        // Dropped away from the tabs, in this window or off every window, it gets a window of
        // its own. Within this window counts too, as a maximized window leaves nowhere else to
        // drop it. A lone tab would only swap one window for another.
        if (tabs.TabCount > 1)
        {
            MoveTabToNewWindow(page, screenLocation);
        }
    }

    // The top-most of this app's controls at a point on the screen, if any.
    static Control? WindowAt(Point screenLocation) =>
        FromChildHandle(WindowFromPoint(new(screenLocation.X, screenLocation.Y)));

    /// <summary>
    /// Moves a tab, with its history, to the end of another window's tabs and shows it there.
    /// This window closes if that was its last tab.
    /// </summary>
    public void MoveTab(TabPage page, MainForm target)
    {
        RemoveTab(page);
        target.AdoptTab(page);
    }

    /// <summary>
    /// Moves a tab into a window of its own, placed so the tab lands near the point.
    /// </summary>
    public MainForm MoveTabToNewWindow(TabPage page, Point screenLocation)
    {
        // Puts the new window's tab under the pointer, as if it had been carried there.
        var firstTab = tabs.GetTabRect(0);
        var firstTabOnScreen = tabs.PointToScreen(firstTab.Location);
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

    void AdoptTab(TabPage page)
    {
        tabs.TabPages.Add(page);
        tabs.SelectedTab = page;
        var view = page.Controls.OfType<FolderView>().First();
        OnViewPathChanged(view);
        SyncToActiveView();
        view.FocusList();
    }

    // Takes a tab out of this window without disposing it, so it can go to another. Closes the
    // window when it was the last.
    void RemoveTab(TabPage page)
    {
        var index = tabs.TabPages.IndexOf(page);
        var wasSelected = tabs.SelectedTab == page;
        tabs.TabPages.Remove(page);
        if (tabs.TabCount == 0)
        {
            Close();
            return;
        }

        if (wasSelected)
        {
            tabs.SelectedIndex = Math.Min(index, tabs.TabCount - 1);
            SyncToActiveView();
        }
        else
        {
            Retitle();
        }
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

        if (view == ActiveView)
        {
            SyncToActiveView();
        }
        else
        {
            Retitle();
        }
    }

    /// <summary>
    /// Names the tabs, and titles the window: a folder is called by its name, unless another tab
    /// in this window has a folder with the same name, when both take in as much of their paths
    /// as tells them apart, "Lode\src" and "Other\src". Tabs in other windows don't count: each
    /// window is seen on its own.
    /// </summary>
    void Retitle()
    {
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
            tabs.SetIcon(page, TabIcon(view.CurrentPath));
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
    void SyncToActiveView()
    {
        // First, so the tab now showing has started reading its folder by the time its status
        // is asked for.
        UpdateShowing();
        var view = ActiveView;
        if (view == null)
        {
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
        tree.SelectPath(view.CurrentPath);
        UpdateStatus(view);
    }

    // Only ever the active view. Its selection is read from the list once, for the buttons and
    // the status bar both: this follows every change of selection.
    void UpdateStatus(FolderView view)
    {
        var selection = view.SelectedEntries();
        UpdateActions(selection);
        statusLabel.Text = view.StatusFor(selection);
        spaceLabel.Text = view.FreeSpaceText;
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

        var windowShowing = !hidden && !minimized;
        var active = ActiveView;
        foreach (var view in Views)
        {
            view.Showing = windowShowing && view == active;
        }

        tree.Showing = windowShowing;
    }

    // Shown and then hidden, to the tray, rather than not shown yet: a window being built reads
    // its first folder before it shows.
    bool hidden;
    bool minimized;

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        base.OnVisibleChanged(eventArgs);
        hidden = !Visible;
        UpdateShowing();

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

    readonly Timer trayTimer = new()
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

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
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
        ShellIcons.ForgetNetworkDrives();
        TextEditors.ForgetTypes();
        tree.RefreshDrives();
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
        if (ActiveView is {ListsPlaces: false} view)
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
            Locations.IsThisPC(view.CurrentPath))
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
        window.AddTab(path, true);
        window.Show();
    }

    Size NormalSize => WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;

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
                RebuildActionsBar();
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
                    tree.SelectPath(active.CurrentPath);
                }

                return;
        }
    }

    void RebuildTree() =>
        SelectAfter(tree.Rebuild());

    // Quick access changed, a pin or an unpin say: only Pinned and Recent are made again.
    void RebuildPlaces() =>
        SelectAfter(tree.RebuildPlaces());

    // Not when the tree kept a pinned folder selected that was just moved, so moving it again
    // with the keyboard carries on from it.
    void SelectAfter(bool selectedMoved)
    {
        if (!selectedMoved &&
            ActiveView is { } view)
        {
            tree.SelectPath(view.CurrentPath);
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
        page.LinkClicked += (_, eventArgs) =>
        {
            if (eventArgs.LinkHref == raiseIssue)
            {
                IssueLauncher.Launch();
                return;
            }

            IssueLauncher.OpenUrl(eventArgs.LinkHref);
        };
        TaskDialog.ShowDialog(this, page);
    }

    void FocusAddress() =>
        breadcrumb.BeginEdit();

    void OnAddressKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        var view = ActiveView;
        if (view == null)
        {
            return;
        }

        if (eventArgs.KeyCode == Keys.Enter)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            view.Navigate(Locations.Normalize(addressBox.Text, view.CurrentPath) ?? addressBox.Text);
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
        else if (eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            breadcrumb.Path = view.CurrentPath;
            view.FocusList();
        }
    }

    void OnFilterTextChanged(object? sender, EventArgs eventArgs)
    {
        if (!updatingFilter &&
            ActiveView is { } view)
        {
            view.Filter = filterBox.Text;
        }
    }

    void OnFilterKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Escape)
        {
            filterBox.Clear();
        }
        else if (eventArgs.KeyCode is not (Keys.Enter or Keys.Down))
        {
            return;
        }

        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        ActiveView?.FocusList();
    }

    void OnTabsMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        var page = TabAt(eventArgs.Location);
        if (page == null)
        {
            return;
        }

        if (eventArgs.Button == MouseButtons.Middle)
        {
            CloseTab(page);
        }
        else if (eventArgs.Button == MouseButtons.Right)
        {
            var view = page.Controls.OfType<FolderView>().First();
            var menu = new ExplorerMenu();
            menu.Items.Add("Duplicate tab", null, (_, _) => AddTab(view.CurrentPath, true));
            menu.Items.Add("Close tab", null, (_, _) => CloseTab(page));
            var index = tabs.TabPages.IndexOf(page);
            menu.Items.Add("Close other tabs", null, (_, _) => CloseOtherTabs(page));
            menu.Items.Add(new ToolStripMenuItem("Close tabs to the left", null, (_, _) => CloseTabsToTheLeft(page)) { Enabled = index > 0 });
            menu.Items.Add(new ToolStripMenuItem("Close tabs to the right", null, (_, _) => CloseTabsToTheRight(page)) { Enabled = index < tabs.TabCount - 1 });
            menu.Items.Add("Close all tabs", null, (_, _) => CloseAllTabs());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Reopen closed tab", null, (_, _) => ReopenClosedTab()) { Enabled = CanReopenClosedTab, ShortcutKeyDisplayString = "Ctrl+Shift+T" });
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(tabs, eventArgs.Location);
        }
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
        var page = tabs.SelectedTab;
        var index = tabs.SelectedIndex;
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
        if (page != null)
        {
            Add("Duplicate tab", () => AddTab(view!.CurrentPath, true));
            Add("Close tab", () => CloseTab(page), "Ctrl+W");
            Add("Close other tabs", () => CloseOtherTabs(page), enabled: tabs.TabCount > 1);
            Add("Close tabs to the left", () => CloseTabsToTheLeft(page), enabled: index > 0);
            Add("Close tabs to the right", () => CloseTabsToTheRight(page), enabled: index < tabs.TabCount - 1);
            Add("Close all tabs", CloseAllTabs);
        }

        Add("Reopen closed tab", ReopenClosedTab, "Ctrl+Shift+T", CanReopenClosedTab);
        Add(UndoHistory.Instance.UndoText, () => ActiveView?.Undo(), "Ctrl+Z", UndoHistory.Instance.CanUndo);
        Add(UndoHistory.Instance.RedoText, () => ActiveView?.Redo(), "Ctrl+Y", UndoHistory.Instance.CanRedo);
        Add("Next tab", () => SelectAdjacentTab(1), "Ctrl+Tab", tabs.TabCount > 1);
        Add("Previous tab", () => SelectAdjacentTab(-1), "Ctrl+Shift+Tab", tabs.TabCount > 1);
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

    TabPage? TabAt(Point location)
    {
        for (var index = 0; index < tabs.TabCount; index++)
        {
            if (tabs.GetTabRect(index).Contains(location))
            {
                return tabs.TabPages[index];
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

    IEnumerable<TabPage> TabPages => tabs.TabPages.Cast<TabPage>();

    void CloseTabs(IEnumerable<TabPage> pages)
    {
        foreach (var page in pages.ToList())
        {
            CloseTab(page);
        }
    }

    void SelectAdjacentTab(int offset)
    {
        if (tabs.TabCount > 1)
        {
            tabs.SelectedIndex = (tabs.SelectedIndex + offset + tabs.TabCount) % tabs.TabCount;
            ActiveView?.FocusList();
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
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
                if (tabs.SelectedTab is { } selected)
                {
                    CloseTab(selected);
                }

                return true;
            case Keys.Control | Keys.Z when !IsEditingText():
                ActiveView?.Undo();
                return true;
            case Keys.Control | Keys.Y when !IsEditingText():
            case Keys.Control | Keys.Shift | Keys.Z when !IsEditingText():
                ActiveView?.Redo();
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
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    // The history changes on the thread that did the work; the buttons follow on the UI thread.
    void OnUndoChanged()
    {
        if (IsHandleCreated &&
            !IsDisposed)
        {
            BeginInvoke(UpdateActions);
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

    // The mouse's back and forward buttons arrive as app commands from whichever control is
    // under the pointer.
    protected override void WndProc(ref Message message)
    {
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
            toolTip.Dispose();
            trayTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
