class MainForm : Form
{
    // Segoe MDL2 Assets code points.
    const string backGlyph = "\uE72B";
    const string forwardGlyph = "\uE72A";
    const string upGlyph = "\uE74A";
    const string refreshGlyph = "\uE72C";
    const string addGlyph = "\uE710";
    const string moreGlyph = "\uE712";

    const int browserBackwardCommand = 1;
    const int browserForwardCommand = 2;
    const int maximumTabTextLength = 28;

    Font glyphFont = new("Segoe MDL2 Assets", 10f);
    ToolTip toolTip = new();
    Breadcrumb breadcrumb = new();
    TextBox addressBox;
    ToolStrip actionsBar = new();
    ToolStripDropDownButton newButton = new("New");
    ToolStripButton cutButton = new("Cut");
    ToolStripButton copyButton = new("Copy");
    ToolStripButton pasteButton = new("Paste");
    ToolStripButton renameButton = new("Rename");
    ToolStripButton deleteButton = new("Delete");
    ToolStripButton terminalButton = new("Terminal");
    ToolStripButton propertiesButton = new("Properties");
    TextBox filterBox = new();
    SplitContainer split = new();
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
    ToolStripMenuItem showHiddenItem = new("Show hidden items")
    {
        ShortcutKeyDisplayString = "Ctrl+H"
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
        places.Changed += RebuildTree;

        SuspendLayout();
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Lode";
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
        var menuButton = CreateButton(moreGlyph, "More", () => { });
        var menu = BuildMenu();
        menuButton.Click += (_, _) => menu.Show(menuButton, new(0, menuButton.Height));

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
        filterBox.AccessibleName = "Filter";
        filterBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        filterBox.Margin = new(4, 0, 4, 0);
        filterBox.PlaceholderText = "Filter this folder (Ctrl+F)";
        filterBox.TextChanged += OnFilterTextChanged;
        filterBox.KeyDown += OnFilterKeyDown;

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new(4, 3, 4, 3)
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
        bar.Controls.Add(filterBox, 5, 0);
        bar.Controls.Add(newTabButton, 6, 0);
        bar.Controls.Add(menuButton, 7, 0);
        return bar;
    }

    Button CreateButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Text = glyph,
            Font = glyphFont,
            FlatStyle = FlatStyle.Flat,
            Size = new(36, 30),
            Margin = new(1),
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        toolTip.SetToolTip(button, tip);
        return button;
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("New tab", null, (_, _) => NewTab()) { ShortcutKeyDisplayString = "Ctrl+T" });
        menu.Items.Add(new ToolStripMenuItem("New window", null, (_, _) => NewWindow()) { ShortcutKeyDisplayString = "Ctrl+N" });
        menu.Items.Add(new ToolStripMenuItem("New folder", null, (_, _) => ActiveView?.CreateFolder()) { ShortcutKeyDisplayString = "Ctrl+Shift+N" });
        var reopenItem = new ToolStripMenuItem("Reopen closed tab", null, (_, _) => ReopenClosedTab()) { ShortcutKeyDisplayString = "Ctrl+Shift+T" };
        menu.Items.Add(reopenItem);
        menu.Items.Add(new ToolStripMenuItem("Command palette", null, (_, _) => ShowCommandPalette()) { ShortcutKeyDisplayString = "Ctrl+Shift+P" });
        menu.Items.Add(new ToolStripSeparator());
        showHiddenItem.Checked = settings.ShowHidden;
        showHiddenItem.Click += (_, _) => ToggleHidden();
        menu.Items.Add(showHiddenItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Terminal", null, (_, _) => OpenTerminal());
        var pinItem = new ToolStripMenuItem("Pin to Quick access", null, (_, _) => TogglePinned());
        menu.Items.Add(pinItem);
        menu.Opening += (_, _) =>
        {
            reopenItem.Enabled = CanReopenClosedTab;
            var path = PinnablePath();
            pinItem.Visible = path != null;
            if (path != null)
            {
                pinItem.Text = PinMenuText(path);
            }
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Options…", null, (_, _) => ShowOptions());
        menu.Items.Add("About Lode", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Lode", null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    // Explorer's command bar: buttons for what is most often done to the selection.
    Control BuildActionsBar()
    {
        actionsBar.Dock = DockStyle.Top;
        actionsBar.GripStyle = ToolStripGripStyle.Hidden;
        // A tool strip takes the menu font otherwise, smaller than the rest of the window.
        actionsBar.Font = DefaultFont;
        actionsBar.RenderMode = ToolStripRenderMode.System;
        // Roomy, as Explorer's command bar is.
        actionsBar.Padding = new(LogicalToDeviceUnits(6), LogicalToDeviceUnits(4), LogicalToDeviceUnits(6), LogicalToDeviceUnits(4));
        var iconSize = LogicalToDeviceUnits(20);
        actionsBar.ImageScalingSize = new(iconSize, iconSize);
        SetIcon(newButton, Icons.New, iconSize);
        SetIcon(cutButton, Icons.Cut, iconSize);
        SetIcon(copyButton, Icons.Copy, iconSize);
        SetIcon(pasteButton, Icons.Paste, iconSize);
        SetIcon(renameButton, Icons.Rename, iconSize);
        SetIcon(deleteButton, Icons.Delete, iconSize);
        SetIcon(terminalButton, Icons.Terminal, iconSize);
        SetIcon(propertiesButton, Icons.Properties, iconSize);
        newButton.DropDownOpening += (_, _) => FillNewMenu();
        // Without an item it would show no arrow until first opened.
        newButton.DropDownItems.Add("Folder");
        cutButton.ToolTipText = "Cut (Ctrl+X)";
        copyButton.ToolTipText = "Copy (Ctrl+C)";
        pasteButton.ToolTipText = "Paste (Ctrl+V)";
        renameButton.ToolTipText = "Rename (F2)";
        deleteButton.ToolTipText = "Delete (Delete)";
        propertiesButton.ToolTipText = "Properties (Alt+Enter)";
        cutButton.Click += (_, _) => ActiveView?.Copy(true);
        copyButton.Click += (_, _) => ActiveView?.Copy(false);
        pasteButton.Click += (_, _) => ActiveView?.Paste();
        renameButton.Click += (_, _) => RunOnList(_ => _.BeginRename());
        deleteButton.Click += (_, _) => ActiveView?.Delete(false);
        terminalButton.Click += (_, _) => OpenTerminal();
        propertiesButton.Click += (_, _) => ActiveView?.ShowProperties();
        actionsBar.Items.AddRange(
        [
            newButton,
            ActionsSeparator(),
            cutButton,
            copyButton,
            pasteButton,
            renameButton,
            deleteButton,
            ActionsSeparator(),
            terminalButton,
            propertiesButton
        ]);
        return actionsBar;
    }

    // Disabled buttons grey their icon themselves.
    void SetIcon(ToolStripItem item, char icon, int size)
    {
        item.Image = Icons.Bitmap(icon, size, SystemColors.ControlText);
        item.ImageScaling = ToolStripItemImageScaling.None;
        // As in Explorer's command bar, only New keeps its name beside its icon; the rest show
        // theirs as a tooltip.
        item.Padding = new(LogicalToDeviceUnits(8), LogicalToDeviceUnits(5), LogicalToDeviceUnits(8), LogicalToDeviceUnits(5));
        if (item == newButton)
        {
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.TextImageRelation = TextImageRelation.ImageBeforeText;
        }
        else
        {
            item.DisplayStyle = ToolStripItemDisplayStyle.Image;
            item.ToolTipText = item.Text;
        }

        item.Margin = new(LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(2), 0);
    }

    ToolStripSeparator ActionsSeparator() =>
        new()
        {
            Margin = new(LogicalToDeviceUnits(8), LogicalToDeviceUnits(4), LogicalToDeviceUnits(8), LogicalToDeviceUnits(4))
        };

    void RunOnList(Action<FolderView> action)
    {
        if (ActiveView is { } view)
        {
            view.FocusList();
            action(view);
        }
    }

    void FillNewMenu()
    {
        newButton.DropDownItems.Clear();
        newButton.DropDownItems.Add("Folder", null, (_, _) => RunOnList(_ => _.CreateFolder()));
        newButton.DropDownItems.Add(new ToolStripSeparator());
        foreach (var entry in ShellNew.Current.OrderBy(_ => FolderView.NewLabel(_.TypeName), StringComparer.CurrentCultureIgnoreCase))
        {
            newButton.DropDownItems.Add(FolderView.NewLabel(entry.TypeName), null, (_, _) => RunOnList(_ => _.CreateFile(entry)));
        }
    }

    /// <summary>
    /// Enables the actions bar's buttons for the active view and its selection.
    /// </summary>
    internal void UpdateActions()
    {
        var view = ActiveView;
        var inFolder = view != null && !Locations.IsThisPC(view.CurrentPath);
        var selection = view?.SelectedEntries() ?? [];
        var files = selection.Count > 0 && selection.All(_ => !_.IsDrive);
        newButton.Enabled = inFolder;
        cutButton.Enabled = files;
        copyButton.Enabled = files;
        // Not tied to the clipboard, which changes without telling Lode. Paste with nothing
        // to paste does nothing.
        pasteButton.Enabled = inFolder;
        renameButton.Enabled = files && selection.Count == 1;
        deleteButton.Enabled = files;
        terminalButton.Enabled = inFolder;
        propertiesButton.Enabled = view != null && (selection.Count > 0 || inFolder);
    }

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
        var status = new StatusStrip();
        status.Items.Add(statusLabel);
        status.Items.Add(spaceLabel);
        return status;
    }

    void OpenInitialTabs(string[] arguments)
    {
        var paths = arguments
            .Select(_ => Locations.Normalize(_))
            .OfType<string>()
            .Where(_ => Directory.Exists(_) ||
                        File.Exists(_))
            .ToList();
        var active = paths.Count - 1;
        if (paths.Count == 0)
        {
            paths = settings.Tabs
                .Where(_ => Locations.IsThisPC(_) ||
                            Directory.Exists(_))
                .ToList();
            active = settings.ActiveTab;
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

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        base.OnFormClosing(eventArgs);
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.Left = bounds.Left;
        settings.Top = bounds.Top;
        settings.Width = bounds.Width;
        settings.Height = bounds.Height;
        settings.Maximized = WindowState == FormWindowState.Maximized;
        settings.SplitterDistance = split.SplitterDistance;
        settings.Tabs = tabs.TabPages
            .Cast<TabPage>()
            .Select(_ => _.Controls.OfType<FolderView>().First().CurrentPath)
            .ToList();
        settings.ActiveTab = tabs.SelectedIndex;
        settings.Save();

        var lastVisible = !Application.OpenForms.OfType<MainForm>().Any(_ => _ != this && _.Visible);
        // UserClosing is the X, Alt+F4 or the taskbar's Close; None is another program sending
        // WM_CLOSE. Windows shutting down, or Exit (which turns HideLastWindowOnClose off), closes
        // for real.
        if (HideLastWindowOnClose &&
            lastVisible &&
            eventArgs.CloseReason is
                CloseReason.UserClosing or
                CloseReason.None)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    public FolderView AddTab(string path, bool activate)
    {
        var view = new FolderView(settings, places)
        {
            Dock = DockStyle.Fill
        };
        var page = new TabPage
        {
            Padding = Padding.Empty
        };
        page.Controls.Add(view);

        // Handled by whichever window the tab is in at the time, since a tab can be dragged to
        // another window.
        view.PathChanged += () => WindowOf(view)?.OnViewPathChanged(view);
        view.StatusChanged += () => WindowOf(view)?.OnViewStatusChanged(view);
        view.NewTabRequested += _ => WindowOf(view)?.AddTab(_, false);
        tabs.TabPages.Add(page);
        if (File.Exists(path))
        {
            // A file path means "show me this file": open its folder with it selected.
            view.Reveal(path);
        }
        else
        {
            view.Navigate(path);
        }

        if (activate)
        {
            tabs.SelectedTab = page;
            view.FocusList();
        }

        return view;
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
    /// Opens the most recently closed tab again, at the folder it was showing.
    /// </summary>
    internal void ReopenClosedTab()
    {
        if (closedTabs.Count == 0)
        {
            return;
        }

        var path = closedTabs[^1];
        closedTabs.RemoveAt(closedTabs.Count - 1);
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
        if (target == this)
        {
            return;
        }

        if (target != null)
        {
            MoveTab(page, target);
            target.Activate();
        }
        else if (tabs.TabCount > 1)
        {
            // A lone tab dragged off its own window would only swap one window for another.
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
        if (view.Parent is not TabPage page)
        {
            return;
        }

        var name = Locations.DisplayName(view.CurrentPath);
        page.Text = name.Length > maximumTabTextLength ? string.Concat(name.AsSpan(0, maximumTabTextLength), "…") : name;
        page.ToolTipText = view.CurrentPath;
        if (view == ActiveView)
        {
            SyncToActiveView();
        }
    }

    // Brings the address bar, filter box, buttons, tree and status bar in line with whichever
    // tab is showing.
    void SyncToActiveView()
    {
        var view = ActiveView;
        if (view == null)
        {
            return;
        }

        breadcrumb.Path = view.CurrentPath;
        updatingFilter = true;
        filterBox.Text = view.Filter;
        updatingFilter = false;
        Text = Locations.DisplayName(view.CurrentPath);
        backButton.Enabled = view.CanGoBack;
        forwardButton.Enabled = view.CanGoForward;
        upButton.Enabled = view.CanGoUp;
        tree.SelectPath(view.CurrentPath);
        UpdateStatus(view);
    }

    void UpdateStatus(FolderView view)
    {
        UpdateActions();
        statusLabel.Text = view.StatusText;
        spaceLabel.Text = view.FreeSpaceText;
    }

    void Reload()
    {
        tree.RefreshDrives();
        ActiveView?.Reload();
    }

    void ToggleHidden()
    {
        settings.ShowHidden = !settings.ShowHidden;
        ApplySettingsToAllWindows();
    }

    void OpenTerminal()
    {
        if (ActiveView is { } view &&
            !Locations.IsThisPC(view.CurrentPath))
        {
            FileOperations.OpenTerminal(view.CurrentPath);
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

    string PinMenuText(string path)
    {
        if (IsPinned(path))
        {
            return "Unpin from Quick access";
        }

        return "Pin to Quick access";
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

    // In this process rather than a new one, so tabs can be dragged between the two.
    void NewWindow()
    {
        var offset = new Size(LogicalToDeviceUnits(30), LogicalToDeviceUnits(30));
        var window = CreateWindow(new(Location + offset, NormalSize));
        window.AddTab(ActiveView?.CurrentPath ?? settings.StartLocation(), true);
        window.Show();
    }

    Size NormalSize => WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;

    void ShowOptions()
    {
        using var options = new OptionsForm(ExplorerReplacement.ForCurrentUser(), settings, TextEditors.Find());
        options.SettingsChanged += ApplySettingsToAllWindows;
        options.ShowDialog(this);
    }

    // Settings are shared by every window, so a change shows in all of them.
    static void ApplySettingsToAllWindows()
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            window.ApplySettings();
        }
    }

    void ApplySettings()
    {
        showHiddenItem.Checked = settings.ShowHidden;
        foreach (var view in Views)
        {
            view.Reload();
        }

        RebuildTree();
    }

    void RebuildTree()
    {
        tree.Rebuild();
        if (ActiveView is { } view)
        {
            tree.SelectPath(view.CurrentPath);
        }
    }

    const string projectUrl = "https://github.com/SimonCropp/Lode";

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
                    """,
            EnableLinks = true,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK }
        };
        page.LinkClicked += (_, eventArgs) =>
            Process.Start(new ProcessStartInfo(eventArgs.LinkHref) { UseShellExecute = true })?.Dispose();
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
            view.Navigate(addressBox.Text);
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
            var menu = new ContextMenuStrip();
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
        Add("Filter this folder", FocusFilter, "Ctrl+F");
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
        Add("Next tab", () => SelectAdjacentTab(1), "Ctrl+Tab", tabs.TabCount > 1);
        Add("Previous tab", () => SelectAdjacentTab(-1), "Ctrl+Shift+Tab", tabs.TabCount > 1);
        Add(settings.ShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden, "Ctrl+H");
        Add("Terminal", OpenTerminal, enabled: PinnablePath() != null);
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
            else if (!Locations.IsThisPC(view.CurrentPath))
            {
                viewCommands = view.BackgroundCommands(FileOperations.ReadClipboard() != null);
            }
            else
            {
                viewCommands = [];
            }

            AddViewCommands(commands, view, viewCommands, "");
        }

        Add("Options…", ShowOptions);
        Add("About Lode", ShowAbout);
        Add("Exit Lode", () => ExitRequested?.Invoke());

        // A view command named as a window one (New folder, say) would only repeat it.
        return commands
            .DistinctBy(_ => _.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // A submenu's entries are named after it: "New: Text Document", "Git: Pull".
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
            places.Changed -= RebuildTree;
            glyphFont.Dispose();
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
