/// <summary>
/// Shows the file picked in a window's Changes or History pane where Options says changes
/// show: in the Diff tab, beside the folders' tabs, the panes staying beside it; over the folder
/// list of the tab showing, till the file is no longer picked; or in the diff window, with the
/// files down its left. Shown in the text editor or git's diff tool, the panes open those
/// themselves, as before Lode drew diffs.
/// </summary>
sealed class DiffHost : IDisposable
{
    Form owner;
    Settings settings;
    ClosableTabControl tabs;
    Func<FolderView?> activeView;
    Action focusList;

    TabPage? page;
    DiffView? tabView;
    DiffView? overlay;
    DiffWindow? window;

    /// <param name="owner">The window whose panes ask, which the diff window opens over.</param>
    public DiffHost(Form owner, Settings settings, ClosableTabControl tabs, Func<FolderView?> activeView, Action focusList)
    {
        this.owner = owner;
        this.settings = settings;
        this.tabs = tabs;
        this.activeView = activeView;
        this.focusList = focusList;
    }

    /// <summary>
    /// The Diff tab, while there is one.
    /// </summary>
    public TabPage? Page => page;

    /// <summary>
    /// Whether the Diff tab is the tab showing.
    /// </summary>
    public bool TabShowing =>
        page != null &&
        tabs.SelectedTab == page;

    /// <summary>
    /// The repository of the diff in the Diff tab, for the panes to stay on while it shows.
    /// </summary>
    public string? TabRepo { get; private set; }

    internal DiffView? TabView => tabView;

    internal DiffView? Overlay => overlay;

    internal DiffWindow? Window => window;

    /// <summary>
    /// Whether the diff shows over the folder list.
    /// </summary>
    public bool Overlaid => overlay is { Parent: not null };

    public void Request(DiffRequest request)
    {
        switch (settings.DiffPlacement)
        {
            case DiffPlacement.Tab:
                InTab(request);
                return;
            case DiffPlacement.Folder:
                OverFolder(request);
                return;
            case DiffPlacement.Window:
                InWindow(request);
                return;
        }
    }

    void InTab(DiffRequest request)
    {
        if (request.Source is { } source)
        {
            TabRepo = source.Repo;
            var view = ShowTab();
            view.Show(source);
            page!.Text = $"Diff: {Path.GetFileName(source.File.Path)}";
            page.ToolTipText = source.File.Path;
            return;
        }

        if (request.Picked > 1 &&
            tabView != null)
        {
            tabView.ShowMessage($"{request.Picked:N0} files picked");
        }
    }

    DiffView ShowTab()
    {
        if (tabView == null ||
            page == null)
        {
            tabView = new(settings)
            {
                Dock = DockStyle.Fill
            };
            page = new()
            {
                Padding = Padding.Empty,
                Text = "Diff"
            };
            page.Controls.Add(tabView);
            tabs.TabPages.Add(page);
        }

        if (tabs.SelectedTab != page)
        {
            tabs.SelectedTab = page;
        }

        return tabView;
    }

    void OverFolder(DiffRequest request)
    {
        if (request.Source is not { } source)
        {
            if (request.Picked == 0)
            {
                CloseOverlay(focus: false);
            }
            else if (Overlaid)
            {
                overlay!.ShowMessage($"{request.Picked:N0} files picked");
            }

            return;
        }

        if (activeView() is not { Parent: TabPage showing } folder)
        {
            return;
        }

        if (overlay == null)
        {
            overlay = new(settings)
            {
                Dock = DockStyle.Fill,
                ShowsClose = true
            };
            overlay.CloseRequested += () => CloseOverlay(focus: true);
        }

        if (overlay.Parent != showing)
        {
            CloseOverlay(focus: false);
            showing.Controls.Add(overlay);
        }

        // In the folder list's place, which is hidden rather than covered, so the focus can't
        // go into it unseen.
        hidden = folder;
        folder.Visible = false;
        overlay.BringToFront();
        overlay.Show(source);
    }

    // The folder list the diff is in place of.
    FolderView? hidden;

    /// <summary>
    /// Puts the diff over the folder list away, the focus going back to the list when
    /// <paramref name="focus"/>.
    /// </summary>
    public void CloseOverlay(bool focus)
    {
        if (overlay?.Parent is not { } parent)
        {
            return;
        }

        var hadFocus = overlay.ContainsFocus;
        parent.Controls.Remove(overlay);
        hidden?.Visible = true;
        hidden = null;

        if (focus ||
            hadFocus)
        {
            focusList();
        }
    }

    void InWindow(DiffRequest request)
    {
        if (request.Source == null)
        {
            return;
        }

        if (!request.Open &&
            window is not { IsDisposed: false, Visible: true })
        {
            return;
        }

        if (window is not { IsDisposed: false })
        {
            window = new(settings);
            window.FormClosed += (_, _) => window = null;
            window.Icon = owner.Icon;
            window.Location = new(
                owner.Left + Math.Max(0, (owner.Width - window.Width) / 2),
                owner.Top + Math.Max(0, (owner.Height - window.Height) / 2));
        }

        // Shown before it is filled: its list given rows before it has a window of its own asks
        // Windows for the first row's bounds while it is being made, before it has any.
        if (!window.Visible)
        {
            window.Show();
        }

        window.Show(request);

        if (request.Open)
        {
            if (window.WindowState == FormWindowState.Minimized)
            {
                window.WindowState = FormWindowState.Normal;
            }

            window.Activate();
        }
    }

    /// <summary>
    /// What is uncommitted in <paramref name="repo"/> was read again: a diff of one of its files,
    /// wherever it shows, is read again too.
    /// </summary>
    public void ChangesRead(string repo)
    {
        foreach (var view in (DiffView?[]) [tabView, overlay])
        {
            if (view?.Source is { IsUncommitted: true } source &&
                Locations.Same(source.Repo, repo))
            {
                view.Reload();
            }
        }

        window?.ChangesRead(repo);
    }

    /// <summary>
    /// Another tab was picked: the diff over the last tab's folder list goes.
    /// </summary>
    public void TabChanged()
    {
        if (overlay?.Parent is { } parent &&
            parent != tabs.SelectedTab)
        {
            CloseOverlay(focus: false);
        }
    }

    /// <summary>
    /// A tab's folder changed: the diff over its list goes.
    /// </summary>
    public void FolderChanged(FolderView view)
    {
        if (overlay?.Parent is { } parent &&
            parent == view.Parent)
        {
            CloseOverlay(focus: false);
        }
    }

    /// <summary>
    /// A tab is closing, or going to another window: the diff over its list goes, and the Diff
    /// tab is forgotten when it is that.
    /// </summary>
    public void Leaving(TabPage leaving)
    {
        if (overlay?.Parent == leaving)
        {
            CloseOverlay(focus: false);
        }

        if (leaving == page)
        {
            page = null;
            tabView = null;
            TabRepo = null;
        }
    }

    public void Dispose()
    {
        overlay?.Dispose();
        window?.Close();
        window?.Dispose();
    }
}
