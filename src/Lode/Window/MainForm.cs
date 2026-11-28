class MainForm : Form
{
    const int browserBackwardCommand = 1;
    const int browserForwardCommand = 2;
    const int maximumTabTextLength = 28;

    /// <summary>
    /// Along the top: Back, Forward and Up, the folder's address, the search box, and the ⋯ menu,
    /// for the tab showing.
    /// </summary>
    internal AddressBar AddressBar { get; }

    /// <summary>
    /// The window's own commands: what the ⋯ menu, the command palette and the keys offer, and
    /// those only they run.
    /// </summary>
    internal Commands Commands { get; }

    /// <summary>
    /// Explorer's command bar, under the address bar: buttons for what is most often done to the
    /// selection in the tab showing.
    /// </summary>
    internal ActionsBar ActionsBar { get; }

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

    /// <summary>
    /// Along the bottom: what the tab showing has, or has selected, its repository and the free
    /// space.
    /// </summary>
    internal StatusBar StatusBar { get; }

    // The instance the status bar and Options follow, kept so the bar stops listening to that
    // one when the window closes.
    Updates updates = Updates.Instance;

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
    /// Exit Lode, chosen from a window's menu or the command palette.
    /// </summary>
    internal static void RequestExit() =>
        ExitRequested?.Invoke();

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
        OpenInitialTabs(arguments, settings.LastWindow());

    // A window as Lode had it when it closed for an update: in its place, with its tabs.
    MainForm(SavedWindow saved, Settings settings, Places places) :
        this(settings, places)
    {
        restored = saved;
        OpenInitialTabs([], saved);
    }

    // The place it opens in, for a window as Lode had it when it closed for an update. Null for
    // every other, which opens where the window that closed last was.
    SavedWindow? restored;

    /// <summary>
    /// The windows a start opens: one, with the folders and files the launch gave it, or, with
    /// none, the tabs of the window that closed last. Started by an update, with none given,
    /// every window Lode had as it closed for it, each in its place with its tabs, the one used
    /// last last, so it opens in front. The settings then forget them, whichever this start
    /// was, so the start after opens one window again.
    /// </summary>
    internal static List<MainForm> ForStart(string[] arguments, Settings settings, Places places)
    {
        var restart = settings.RestartWindows;
        if (restart.Count > 0)
        {
            settings.RestartWindows = [];
            settings.Save();
        }

        if (restart.Count == 0 ||
            Openable(arguments).Count > 0)
        {
            return [new(arguments, settings, places)];
        }

        return restart
            .Select(_ => new MainForm(_, settings, places))
            .ToList();
    }

    // A window with no tabs yet.
    MainForm(Settings settings, Places places)
    {
        this.settings = settings;
        this.places = places;
        OpenSite = url => FileOperations.Open(this, url);
        Tree = new(settings, places);
        FindHost = new(this, settings, Tabs, () => ActiveView, () => Views, ShowInFolder);
        GitPanes = new(this, settings)
        {
            FocusList = () => ActiveView?.FocusList()
        };
        GitPanes.ShowInFolderRequested += ShowInFolder;
        GitPanes.OpenSiteRequested += url => OpenSite(url);
        DiffHost = new(this, settings, Tabs, () => ActiveView, () => ActiveView?.FocusList());
        GitPanes.DiffRequested += DiffHost.Request;
        GitPanes.ChangesRead += DiffHost.ChangesRead;
        GitPanes.StripLabelsChanged += ApplyStripLabelsInAllWindows;
        Commands = new(this, settings, places);
        // Behind the tab showing, as the tree and the list open folders in new tabs. A zip on the
        // way to a folder inside it opens inside it, as the list's Open in new tab opens one: in
        // the address it is somewhere gone through, rather than a file.
        AddressBar = new(this, settings, () => ActiveView, Reload, NewTab, _ => AddTab(_, false, intoZip: true), Commands.Menu);
        ActionsBar = new(this, settings, () => ActiveView, ShowOptions, ApplySettingsToAllWindows);
        StatusBar = new(this, settings, updates, () => ActiveView, ShowOptions, url => OpenSite(url), folder => AddTab(folder, true));
        FindHost.ProgressChanged += () => StatusBar.ShowProgress(FindHost.Progress);
        places.Changed += RebuildPlaces;
        UndoHistory.Instance.Changed += OnUndoChanged;
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

        Controls.Add(BuildContent());

        // Straight after the content, so it docks after the bars, the find strip and the status
        // bar have taken their edges: down the right between the command bar and the find strip.
        Controls.Add(GitPanes.Strip);
        Controls.Add(ActionsBar.Strip);
        Controls.Add(AddressBar.Row);

        // Before the status bar, so it docks above it.
        Controls.Add(FindHost.Panel);
        Controls.Add(StatusBar.Strip);
        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>
    /// New on the tree's right-click menu: the folder opens in the tab the tree goes to, see
    /// <see cref="GoFromTree"/>, and once it has been read, New does there what it does in the
    /// list, making the item with its name ready to edit. Nothing is made if the tab ends up
    /// somewhere else, as when the folder has gone.
    /// </summary>
    internal async Task CreateFromTree(string folder, ShellNew.Entry? type)
    {
        var view = ActiveView;
        if (view == null ||
            !Locations.Same(view.CurrentPath, folder))
        {
            view = GoFromTree(folder);
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

    // The folder's tab last showing, which the tree goes to while the Diff tab or Find Results
    // shows.
    FolderView? lastView;

    /// <summary>
    /// A folder picked in the tree: the tab showing goes there. The Diff tab and Find Results
    /// have no folder, so with one of them showing, the folder's tab that showed before it goes
    /// there instead, and shows in its place, the other staying open behind: or, that tab closed
    /// since, the folder's tab looked at last, or, with none left, a new tab.
    /// </summary>
    FolderView GoFromTree(string path)
    {
        if (ActiveView is { } active)
        {
            active.Navigate(path);
            return active;
        }

        var view = ViewBeforeNoFolder();
        if (view == null)
        {
            return AddTab(path, true);
        }

        // Before the tab shows, as AddTab does, so it reads only the folder picked, and the tree
        // isn't walked back to the folder it had on the way.
        view.Navigate(path);
        Tabs.SelectedTab = (TabPage)view.Parent!;
        return view;
    }

    // With the Diff tab or Find Results showing, the folder's tab that showed before it: or,
    // that tab closed since, the folder's tab looked at last. None with no folder's tab left.
    FolderView? ViewBeforeNoFolder()
    {
        if (lastView != null &&
            Views.Contains(lastView))
        {
            return lastView;
        }

        return Views.MaxBy(_ => _.LastShown);
    }

    Control BuildContent()
    {
        Tree.Dock = DockStyle.Fill;
        Tree.PathSelected += _ => GoFromTree(_);
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

    // With no folders or files given it, the window opens with the tabs saved for it.
    void OpenInitialTabs(string[] arguments, SavedWindow window)
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
            var saved = window.TabsLastShown.Count == window.Tabs.Count;
            var now = Time.GetUtcNow();

            // The tab that was active is as many along as there are tabs before it still to
            // open: one whose folder has gone since leaves those after it a place earlier.
            active = 0;
            for (var index = 0; index < window.Tabs.Count; index++)
            {
                var path = window.Tabs[index];
                if (!Locations.IsThisPC(path) &&
                    !Locations.IsRemote(path) &&
                    !Directory.Exists(path) &&
                    !Zip.IsArchivePath(path))
                {
                    continue;
                }

                if (index < window.ActiveTab)
                {
                    active++;
                }

                paths.Add(path);

                // Not after now, which a clock put back would leave: the tab would stay until
                // the clock caught up.
                if (saved &&
                    window.TabsLastShown[index] < now)
                {
                    lastShown.Add(window.TabsLastShown[index]);
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
        var place = restored ?? settings.LastWindow();
        if (restoreBounds)
        {
            var saved = new Rectangle(place.Left, place.Top, place.Width, place.Height);
            var onScreen = Screen.AllScreens.Any(_ => _.WorkingArea.IntersectsWith(saved));
            if (saved is { Width: > 0, Height: > 0 } && onScreen)
            {
                Bounds = saved;
            }

            if (place.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }

        var distance = place.SplitterDistance > 0 ? place.SplitterDistance : LogicalToDeviceUnits(250);
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

        var bounds = NormalBounds;
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

    // Where the window is, as it is to open again: as it was before it was maximized or
    // minimized.
    Rectangle NormalBounds =>
        WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

    // Puts the window's tabs in the settings, for the next start to open. Not those of a window
    // with no folder left in it, its last tab dragged to another window, which closes for
    // that: none would be saved, in place of the tabs last saved, until the other closed.
    internal void RememberTabs()
    {
        if (Saved() is not { } saved)
        {
            return;
        }

        settings.Tabs = saved.Tabs;
        settings.TabsLastShown = saved.TabsLastShown;
        settings.ActiveTab = saved.ActiveTab;
    }

    /// <summary>
    /// The window's place and its tabs, as it is to open again. Null for a window with no folder
    /// left in it, its last tab dragged to another window, which closes for that.
    /// </summary>
    internal SavedWindow? Saved()
    {
        if (!Views.Any())
        {
            return null;
        }

        // The tab showing is looked at until now.
        var now = Time.GetUtcNow();
        foreach (var view in Views.Where(_ => _.Showing))
        {
            view.LastShown = now;
        }

        var bounds = NormalBounds;
        return new()
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WasMaximized,
            SplitterDistance = split.SplitterDistance,
            Tabs = Views
                .Select(_ => _.CurrentPath)
                .ToList(),
            TabsLastShown = Views
                .Select(_ => _.LastShown)
                .ToList(),
            // Among the folders' tabs, which are all that open again: with Find Results showing,
            // the one before it.
            ActiveTab = Math.Max(0, TabPages.Take(Tabs.SelectedIndex + 1).Count(HoldsFolder) - 1)
        };
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

    internal void NewTab() =>
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
            lastView = synced;
            Synced?.Invoke(synced);
        }

        // First, so the tab now showing has started reading its folder by the time its status
        // is asked for.
        UpdateShowing();
        var view = ActiveView;
        AddressBar.Update(view);
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

        Retitle();
        Tree.SelectPath(view.CurrentPath);
        UpdateStatus(view);
    }

    // Only ever the active view. Its selection is read from the list once, for the buttons and
    // the status bar both: this follows every change of selection.
    void UpdateStatus(FolderView view)
    {
        var selection = view.SelectedEntries();
        ActionsBar.Update(selection);
        StatusBar.Update(view, selection);
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

    // A git action that started or finished.
    void ReadGitState()
    {
        foreach (var view in Views)
        {
            view.ReadGitState();
        }
    }

    /// <summary>
    /// Every window's repositories read again, as one of Lode's git actions finishing has them
    /// read: the background fetch fetched them, outside the Actions panel.
    /// </summary>
    internal static void ReadGitStateInAllWindows()
    {
        foreach (var window in Application.OpenForms.OfType<MainForm>().ToList())
        {
            window.ReadGitState();
        }
    }

    /// <summary>
    /// Where the background fetch looks for the repositories to fetch: the folder of every tab
    /// of every window showing, read yet or not, as a tab not looked at since Lode started has
    /// read nothing, and the repository each tab's status bar is about, one selected in a folder
    /// of repositories say. Not those of a window gone to the tray, which nobody is looking at:
    /// they are fetched once it is back, if they are due by then.
    /// </summary>
    internal static List<string> OpenFolders() =>
        Application.OpenForms
            .OfType<MainForm>()
            .Where(_ => _.Visible)
            .SelectMany(_ => _.Views)
            .SelectMany(_ => new[] {_.CurrentPath, _.GitRepo})
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Back at the front, where a commit made in a terminal meanwhile went unseen: git changes
    // only .git, not the folder listed. And a folder pinned or used meanwhile, in Explorer or
    // a file dialog, which Lode hears nothing of.
    protected override void OnActivated(EventArgs args)
    {
        base.OnActivated(args);
        usedLast = this;
        SaveTabsSoon();
        foreach (var view in Views)
        {
            view.ReadGitStateUnlessFresh();
        }

        places.ReadAgainIfStale();
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
    /// shows again when it comes back, so never the last one; with the Diff tab or Find Results
    /// showing, never the folder's tab that showed before it. The tab looked at longest ago
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

        // The Diff tab and Find Results have no folder, and no folder's tab is marked showing
        // while one of them is, so the one the tree would go to stays: else, left showing for the
        // hours given, every folder's tab would close, leaving the window nothing but that tab.
        var kept = ActiveView ?? ViewBeforeNoFolder();
        CloseTabs(
            Views
                .Where(_ => _ != kept &&
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
    // which types are text. And Quick access, for Pinned and Recent, off the UI thread.
    internal void Reload()
    {
        ShellIcons.ForgetFileIcons();
        ShellIcons.ForgetPlaceIcons();
        OpenWith.Forget();
        ShellIcons.ForgetDrives();
        TextEditors.ForgetTypes();
        Tree.ReadAgain();
        places.ReadAgain();
        GitPanes.Reload();
        if (FindHost.ResultsShowing)
        {
            FindHost.ResultsView?.SearchAgain();
            return;
        }

        ActiveView?.ReloadRows();
    }

    internal void NewWindow() =>
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

    internal void ShowOptions() =>
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
    internal static void ApplySettingsToAllWindows(SettingsChange change)
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
                var pinned = Commands.IsPinned(path);
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

    // As Find Results shows: what was found so far in the status bar, and none of what is a
    // folder's, its free space, its repository, or the actions on its selection.
    void UpdateResultsStatus()
    {
        StatusBar.ShowResults(FindHost.Progress);
        ActionsBar.Update();
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

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (Commands.RunKey(keyData))
        {
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
            ActionLog.Changed -= ReadGitState;
            GitPanes.Dispose();
            DiffHost.Dispose();
            StatusBar.Dispose();
            AddressBar.Dispose();
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
