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
    TextBox addressBox = new();
    TextBox filterBox = new();
    SplitContainer split = new();
    Settings settings;
    Places places;
    FolderTree tree;
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
        places.Changed += RebuildTree;

        SuspendLayout();
        AutoScaleDimensions = new(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Meridian";
        Icon = ShellIcons.FolderWindowIcon();
        ClientSize = new(1200, 760);
        MinimumSize = new(520, 320);

        backButton = CreateButton(backGlyph, "Back (Alt+Left)", () => ActiveView?.GoBack());
        forwardButton = CreateButton(forwardGlyph, "Forward (Alt+Right)", () => ActiveView?.GoForward());
        upButton = CreateButton(upGlyph, "Up (Alt+Up)", () => ActiveView?.GoUp());

        Controls.Add(BuildContent());
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
        addressBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        addressBox.Margin = new(6, 0, 4, 0);
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
        bar.Controls.Add(addressBox, 4, 0);
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
        menu.Items.Add(new ToolStripSeparator());
        showHiddenItem.Checked = settings.ShowHidden;
        showHiddenItem.Click += (_, _) => ToggleHidden();
        menu.Items.Add(showHiddenItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open terminal here", null, (_, _) => OpenTerminal());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Options…", null, (_, _) => ShowOptions());
        menu.Items.Add("About Meridian", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Meridian", null, (_, _) => ExitRequested?.Invoke());
        return menu;
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
        split.Panel1.Controls.Add(tree);
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
            .Select(Locations.Normalize)
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
        tabs.TabPages.Remove(page);
        page.Dispose();
        if (wasSelected)
        {
            tabs.SelectedIndex = Math.Min(index, tabs.TabCount - 1);
            SyncToActiveView();
        }
    }

    static MainForm? WindowOf(Control? control) =>
        control?.FindForm() as MainForm;

    void OnTabDraggedOff(TabPage page, Point screenLocation)
    {
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

        addressBox.Text = view.CurrentPath;
        updatingFilter = true;
        filterBox.Text = view.Filter;
        updatingFilter = false;
        Text = $"{Locations.DisplayName(view.CurrentPath)} - Meridian";
        backButton.Enabled = view.CanGoBack;
        forwardButton.Enabled = view.CanGoForward;
        upButton.Enabled = view.CanGoUp;
        tree.SelectPath(view.CurrentPath);
        UpdateStatus(view);
    }

    void UpdateStatus(FolderView view)
    {
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
        using var options = new OptionsForm(ExplorerReplacement.ForCurrentUser(), settings);
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

    const string projectUrl = "https://github.com/SimonCropp/Meridian";

    void ShowAbout()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        var page = new TaskDialogPage
        {
            Caption = "About Meridian",
            Heading = $"Meridian {version}",
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

    void FocusAddress()
    {
        addressBox.Focus();
        addressBox.SelectAll();
    }

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
            addressBox.Text = view.CurrentPath;
            view.FocusList();
        }
        else if (eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            addressBox.Text = view.CurrentPath;
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
            menu.Items.Add("Close other tabs", null, (_, _) => CloseOtherTabs(page));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(tabs, eventArgs.Location);
        }
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

    void CloseOtherTabs(TabPage keep)
    {
        foreach (var page in tabs.TabPages.Cast<TabPage>().Where(_ => _ != keep).ToList())
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
                filterBox.Focus();
                filterBox.SelectAll();
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
